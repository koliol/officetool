# -*- coding: utf-8 -*-
"""
前端深度静态校验（在 node/vite 不可用的环境下替代编译期检查）。

覆盖几类会在运行时才炸、但静态可查的错误：
  1. import 路径能否解析到真实文件（写错路径 -> 白屏）
  2. script 块括号 / 引号 / 模板串是否闭合（最典型的手写错误）
  3. Vue SFC 块是否存在、模板里是否混入 script 标签
  4. 裸包名是否在 package.json 里声明过

设计说明（为什么不用正则）：
  正则无法正确处理字符串里的 `//`、模板串里的 `${}` 嵌套、正则字面量、
  以及三引号混用。所以这里手写词法扫描器，用显式栈维护上下文。

  `${...}` 被视为**字符串内部**，不产生任何括号事件 —— 这与 JS 语法一致：
  整个模板串是一个表达式 token。插值内部的括号自成一体，单独配平。
"""
import json
import re
import sys
from pathlib import Path

import os
# 默认指向本仓库的前端源码目录（脚本位于 <repo>/tools/vue-static-check/）。
# 可用环境变量 WEB_SRC 覆盖，便于对任意目录跑检查。
_DEFAULT_SRC = Path(__file__).resolve().parent.parent.parent / "web" / "src"
SRC = Path(os.environ.get("WEB_SRC", str(_DEFAULT_SRC)))

# 中文 Windows 控制台默认是 GBK。报告文本里一旦出现控制台编码不了的字符，
# 直接 print 会抛 UnicodeEncodeError 把校验器带崩 —— 而它本该只是"报告结果"。
# 保留控制台原有编码，只把无法编码的字符替换掉。
try:
    sys.stdout.reconfigure(errors="replace")
    sys.stderr.reconfigure(errors="replace")
except Exception:  # noqa: BLE001
    pass

WEB = SRC.parent
PKG = WEB / "package.json"

errors: list[str] = []
warnings: list[str] = []

# 期待操作数的位置：此处的 `/` 是正则开始而非除号
REGEX_PREV = set("(,=:[!&|?{};+-*%^~<>\n")


class Unclosed(Exception):
    def __init__(self, line: int, what: str):
        super().__init__(f"第 {line} 行的 {what} 未闭合")
        self.line = line


# 闭合括号 -> 对应的开括号。注意键必须是**闭合**字符，
# 写成 {")": "(", ...} 而不是 {"(": "(", ...} —— 后者会让 .get(')') 返回 None，
# 括号永远弹不掉，进而把后面的模板串误判成未闭合。
CLOSE_TO_OPEN = {")": "(", "]": "[", "}": "{"}


def lex(code: str) -> list[tuple[str, int]]:
    """
    返回 [(结构字符, 行号)]，注释与字符串内容被剔除。

    栈里可能有：'(' '[' '{' 以及两个特殊标记
      'tpl'    当前在模板串原文中
      'interp' 当前在 ${ } 内部
    """
    out: list[tuple[str, int]] = []
    stack: list[str] = []
    i, n, line = 0, len(code), 1
    prev = ""  # 上一个有效字符，用于判断 / 是正则还是除号

    def mode() -> str:
        return stack[-1] if stack else "code"

    while i < n:
        ch = code[i]

        # ── 模板串原文 ────────────────────────────────────────────────
        if mode() == "tpl":
            if ch == "\\":
                if i + 1 < n and code[i + 1] == "\n":
                    line += 1
                i += 2
                continue
            if ch == "\n":
                line += 1
                i += 1
                continue
            if ch == "`":
                stack.pop()
                i += 1
                prev = "s"
                continue
            if ch == "$" and i + 1 < n and code[i + 1] == "{":
                stack.append("interp")
                i += 2
                prev = "{"
                continue
            i += 1
            continue

        # ── 代码上下文 ────────────────────────────────────────────────
        if ch == "\n":
            line += 1
            i += 1
            prev = "\n"
            continue

        if ch == "/" and i + 1 < n and code[i + 1] == "/":
            j = code.find("\n", i)
            i = n if j < 0 else j
            continue

        if ch == "/" and i + 1 < n and code[i + 1] == "*":
            j = code.find("*/", i + 2)
            if j < 0:
                raise Unclosed(line, "块注释 /*")
            line += code.count("\n", i, j)
            i = j + 2
            continue

        # 正则字面量：仅当同行能找到结束的 `/` 才认定，否则退回除号处理。
        # 这样误判的代价是「少识别一个正则」，而不是「误报未闭合」。
        if ch == "/" and prev in REGEX_PREV:
            j = i + 1
            ok = False
            while j < n:
                c = code[j]
                if c == "\\":
                    j += 2
                    continue
                if c == "\n":
                    break
                if c == "[":
                    k = j + 1
                    while k < n and code[k] not in "]\n":
                        if code[k] == "\\":
                            k += 1
                        k += 1
                    j = k + 1
                    continue
                if c == "/":
                    ok = True
                    break
                j += 1
            if ok:
                i = j + 1
                # 跳过 flags
                while i < n and code[i].isalpha():
                    i += 1
                prev = "r"
                continue

        if ch in "'\"`":
            if ch == "`":
                stack.append("tpl")
                i += 1
                continue
            q, ln = ch, line
            i += 1
            while i < n:
                c = code[i]
                if c == "\\":
                    i += 2
                    continue
                if c == "\n":
                    raise Unclosed(ln, f"字符串 {q}")
                if c == q:
                    i += 1
                    break
                i += 1
            else:
                raise Unclosed(ln, f"字符串 {q}")
            prev = "s"
            continue

        # 插值的结束：不产生括号事件，只回到模板串
        if ch == "}" and mode() == "interp":
            stack.pop()
            i += 1
            prev = "}"
            continue

        if ch in "([{":
            out.append((ch, line))
            stack.append(ch)
            prev = ch
            i += 1
            continue

        if ch in ")]}":
            if stack and stack[-1] == CLOSE_TO_OPEN[ch]:
                stack.pop()
            out.append((ch, line))
            prev = ch
            i += 1
            continue

        if ch not in " \t\r":
            prev = ch
        i += 1

    if mode() == "tpl":
        raise Unclosed(line, "模板串 `")
    if mode() == "interp":
        raise Unclosed(line, "模板插值 ${")

    return out


def check_balanced(rel: str, code: str) -> None:
    try:
        tokens = lex(code)
    except Unclosed as e:
        errors.append(f"{rel}: {e}")
        return

    pairs = {")": "(", "]": "[", "}": "{"}
    stack: list[tuple[str, int]] = []

    for ch, ln in tokens:
        if ch in "([{":
            stack.append((ch, ln))
        else:
            if not stack:
                errors.append(f"{rel}:{ln}  多余的 `{ch}`")
                return
            open_ch, open_line = stack.pop()
            if open_ch != pairs[ch]:
                errors.append(f"{rel}:{ln}  `{ch}` 与第 {open_line} 行的 `{open_ch}` 不匹配")
                return

    if stack:
        open_ch, open_line = stack[-1]
        errors.append(f"{rel}: 第 {open_line} 行的 `{open_ch}` 没有闭合（{len(stack)} 层未闭合）")


# ── import 解析 ───────────────────────────────────────────────────────

IMPORT_RE = re.compile(r"""import\s+(?:[\w*{},\s$]+\s+from\s+)?['"]([^'"]+)['"]""")


def load_pkg_names() -> set[str]:
    if not PKG.exists():
        return set()
    data = json.loads(PKG.read_text(encoding="utf-8"))
    return set(data.get("dependencies", {})) | set(data.get("devDependencies", {}))


PKG_NAMES = load_pkg_names()


def resolve_import(rel: str, spec: str) -> bool:
    # 注意：`@/` 是路径别名，`@scope/pkg` 是包名，必须先区分开
    if spec == "@" or spec.startswith("@/"):
        base = SRC / spec[2:]
    elif spec.startswith("."):
        base = (SRC / rel).parent / spec
    else:
        name = "/".join(spec.split("/")[:2]) if spec.startswith("@") else spec.split("/")[0]
        return name in PKG_NAMES

    base = base.resolve()
    candidates = [
        base,
        base.with_suffix(".js"),
        base.with_suffix(".vue"),
        base.with_suffix(".json"),
        base.with_suffix(".mjs"),
        base / "index.js",
    ]
    return any(c.is_file() for c in candidates)


def check_imports(rel: str, code: str) -> None:
    for m in IMPORT_RE.finditer(code):
        spec = m.group(1)
        if not resolve_import(rel, spec):
            ln = code[: m.start()].count("\n") + 1
            errors.append(f"{rel}:{ln}  导入无法解析：{spec!r}")


# ── SFC 结构 ──────────────────────────────────────────────────────────

SCRIPT_RE = re.compile(r"<script(?:\s[^>]*)?>(.*?)</script>", re.S)
TEMPLATE_RE = re.compile(r"<template(?:\s[^>]*)?>(.*)</template>", re.S)


def check_sfc(rel: str, text: str) -> str | None:
    ms = SCRIPT_RE.search(text)
    mt = TEMPLATE_RE.search(text)

    if not mt:
        errors.append(f"{rel}: 缺少顶层 <template>")
    if not ms:
        warnings.append(f"{rel}: 没有 <script> 块")

    if mt:
        tpl = mt.group(1)
        # 插槽写法是 <template #slot>，所以只查 script / style 混入
        for bad in ("</script>", "</style>"):
            if bad in tpl:
                errors.append(f"{rel}: 模板里混入了 {bad}")

    return ms.group(1) if ms else None


def check_file(path: Path) -> None:
    rel = path.relative_to(SRC).as_posix()
    text = path.read_text(encoding="utf-8")

    script = check_sfc(rel, text) if path.suffix == ".vue" else text
    if script:
        check_balanced(rel, script)
        check_imports(rel, script)


def main() -> int:
    files = sorted(list(SRC.rglob("*.vue")) + list(SRC.rglob("*.js")))
    for f in files:
        check_file(f)

    print(f"=== 扫描 {len(files)} 个文件，package.json 声明 {len(PKG_NAMES)} 个包 ===")
    if warnings:
        print(f"\n=== 提示 {len(warnings)} ===")
        for w in warnings:
            print("  " + w)
    if errors:
        print(f"\n=== 错误 {len(errors)} ===")
        for e in errors:
            print("  " + e)
        return 1

    print("\n=== 无错误 ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
