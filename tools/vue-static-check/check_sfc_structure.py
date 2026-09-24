"""前端源码静态校验（node 不可用时的替代方案）。

做三件事：
1. 依赖存在性：每个 import 的相对路径能不能落到真实文件
2. JS 分隔符平衡：剥离注释/字符串/模板串后，检查 {} () [] 是否配对
3. Vue 模板标签平衡：忽略自闭合与 void 元素后，检查开闭标签配对
"""
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


VOID_TAGS = {
    "area", "base", "br", "col", "embed", "hr", "img", "input",
    "link", "meta", "param", "source", "track", "wbr",
}

errors = []
warnings = []
stats = []


def strip_js(code: str) -> str:
    """把注释、字符串、模板串替换成等长空白，保留换行以便定位。"""
    out = []
    i, n = 0, len(code)
    while i < n:
        ch = code[i]

        # 行注释
        if code.startswith("//", i):
            j = code.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
            continue

        # 块注释
        if code.startswith("/*", i):
            j = code.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("".join("\n" if c == "\n" else " " for c in code[i:j]))
            i = j
            continue

        # 单双引号字符串
        if ch in "'\"":
            j = i + 1
            while j < n:
                if code[j] == "\\":
                    j += 2
                    continue
                if code[j] == ch:
                    j += 1
                    break
                j += 1
            out.append("".join("\n" if c == "\n" else " " for c in code[i:j]))
            i = j
            continue

        # 模板串
        if ch == "`":
            j = i + 1
            depth = 0
            while j < n:
                if code[j] == "\\":
                    j += 2
                    continue
                if code.startswith("${", j):
                    depth += 1
                    j += 2
                    continue
                if code[j] == "}" and depth > 0:
                    depth -= 1
                    j += 1
                    continue
                if code[j] == "`" and depth == 0:
                    j += 1
                    break
                j += 1
            out.append("".join("\n" if c == "\n" else " " for c in code[i:j]))
            i = j
            continue

        out.append(ch)
        i += 1

    return "".join(out)


def check_balance(name: str, code: str, kind: str):
    pairs = {"(": ")", "{": "}", "[": "]"}
    closers = {v: k for k, v in pairs.items()}
    stack = []
    line = 1
    for idx, ch in enumerate(code):
        if ch == "\n":
            line += 1
            continue
        if ch in pairs:
            stack.append((ch, line))
        elif ch in closers:
            if not stack:
                errors.append(f"{name}: {kind} 第 {line} 行多余的 '{ch}'")
                return
            opened, opened_line = stack.pop()
            if pairs[opened] != ch:
                errors.append(
                    f"{name}: {kind} 第 {line} 行的 '{ch}' 与第 {opened_line} 行的 '{opened}' 不匹配"
                )
                return
    for opened, opened_line in stack:
        errors.append(f"{name}: {kind} 第 {opened_line} 行的 '{opened}' 没有闭合")


TAG_RE = re.compile(r"<(/?)([A-Za-z][-A-Za-z0-9_.]*)((?:\"[^\"]*\"|'[^']*'|[^>\"'])*?)(/?)>")


def check_template(name: str, tpl: str):
    # 先剥掉 HTML 注释：注释里出现的 `<a>`、`<div>` 之类是说明文字，不是标签
    tpl = re.sub(r"<!--.*?-->", lambda m: "".join(
        "\n" if c == "\n" else " " for c in m.group(0)), tpl, flags=re.S)

    stack = []
    line = 1
    pos = 0
    for m in TAG_RE.finditer(tpl):
        line += tpl.count("\n", pos, m.start())
        pos = m.start()
        closing, tag, attrs, self_close = m.group(1), m.group(2), m.group(3), m.group(4)
        low = tag.lower()

        if low in VOID_TAGS or self_close == "/":
            continue
        if closing == "/":
            if not stack:
                errors.append(f"{name}: template 第 {line} 行多余的 </{tag}>")
                return
            opened, opened_line = stack.pop()
            if opened != low:
                errors.append(
                    f"{name}: template 第 {line} 行的 </{tag}> 与第 {opened_line} 行的 <{opened}> 不匹配"
                )
                return
        else:
            stack.append((low, line))
    if stack:
        opened, opened_line = stack[-1]
        errors.append(f"{name}: template 第 {opened_line} 行的 <{opened}> 没有闭合")


def resolve_import(base: Path, spec: str):
    if not spec.startswith("."):
        return True  # 包依赖，交给 npm
    target = (base.parent / spec).resolve()
    for cand in (target, target.with_suffix(".js"), target.with_suffix(".vue"),
                 target / "index.js", target / "index.vue"):
        if cand.is_file():
            return True
    return False


files = sorted(SRC.rglob("*.vue")) + sorted(SRC.rglob("*.js"))
for path in files:
    rel = path.relative_to(SRC).as_posix()
    text = path.read_text(encoding="utf-8")

    if path.suffix == ".js":
        check_balance(rel, strip_js(text), "script")
        stats.append((rel, len(text.splitlines())))
    else:
        m = re.search(r"<script setup>(.*?)</script>", text, re.S)
        if not m:
            warnings.append(f"{rel}: 没有找到 <script setup>")
        else:
            check_balance(rel, strip_js(m.group(1)), "script")

        m2 = re.search(r"<template>(.*)</template>", text, re.S)
        if not m2:
            warnings.append(f"{rel}: 没有找到 <template>")
        else:
            check_template(rel, m2.group(1))

        stats.append((rel, len(text.splitlines())))

    # import 路径
    for spec in re.findall(r"from\s+['\"]([^'\"]+)['\"]", text):
        if not resolve_import(path, spec):
            errors.append(f"{rel}: import 无法解析 -> {spec}")

print("=== 文件行数 ===")
for rel, n in stats:
    print(f"  {n:5d}  {rel}")

print()
print(f"=== 警告 {len(warnings)} ===")
for w in warnings:
    print("  " + w)

print()
if errors:
    print(f"=== 错误 {len(errors)} ===")
    for e in errors:
        print("  " + e)
    sys.exit(1)

print("=== 无结构性错误 ===")
