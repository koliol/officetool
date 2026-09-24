"""模板引用校验。

结构没问题不代表能跑起来。最容易静默出错的是：
  · 模板里 @click="foo(...)" 写了个脚本里不存在的函数（点下去才报错）
  · 用了 <XxxView /> 但忘了 import（编译期报 unresolved component）
  · v-model="bar" 绑了个未声明的变量

这里用保守规则扫一遍：只报「能明确定位到缺失」的情况，不猜测。
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


# Element Plus 全局注册的图标（main.js 里批量注册），出现即视为已声明
GLOBAL_ICONS = {
    "Search", "Upload", "Refresh", "RefreshRight", "ArrowDown", "Plus", "Key",
    "Lock", "User", "Loading", "Folder", "FolderAdd", "CopyDocument",
    "Delete", "Edit", "Setting", "Download", "Close", "Check", "Warning",
}

JS_KEYWORDS = {
    "true", "false", "null", "undefined", "typeof", "instanceof", "new", "return",
    "if", "else", "for", "while", "in", "of", "Math", "Date", "JSON", "Object",
    "Array", "String", "Number", "Boolean", "console", "window", "document",
    "navigator", "sessionStorage", "localStorage", "this", "void", "await",
    "async", "function", "class", "const", "let", "var", "try", "catch",
    "throw", "switch", "case", "default", "break", "continue", "do", "delete",
    "setTimeout", "setInterval", "parseInt", "parseFloat", "isNaN", "Promise",
    "encodeURIComponent", "decodeURIComponent", "process", "globalThis",
}

errors = []
checked = 0


def declared_names(script: str) -> set:
    names = set()

    # import ... from  ——  取导入的标识符
    for m in re.finditer(r"import\s+([^;]*?)\s+from\s+['\"]", script):
        clause = m.group(1).strip()
        # 花括号要先整体剥掉再按逗号切：否则只有第一个标识符会被正确解析，
        # 后面的会带着 " }" 尾巴，等于没导入（这正是早期版本漏报的原因）
        if clause.startswith("{"):
            clause = clause.strip("{} ")
        for part in clause.split(","):
            part = part.strip()
            if not part:
                continue
            if part.startswith("*"):
                names.add(part.split(" as ")[-1].strip())
            else:
                names.add(part.split(" as ")[-1].strip())

    # 顶层声明（含缩进在 setup 内的 const/let/function）
    for m in re.finditer(r"\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)", script):
        names.add(m.group(1))
    for m in re.finditer(r"\bfunction\s+([A-Za-z_$][\w$]*)", script):
        names.add(m.group(1))
    # defineProps 的 props 与 defineEmits 的 emit
    if "defineProps" in script:
        names.add("props")
    if "defineEmits" in script:
        names.add("emit")

    # defineProps({ ... }) 里的每个 prop 在模板中可以直接用名字引用，
    # 不写 props.xxx —— 漏掉这一步会把它们全判成「未声明」
    for m in re.finditer(r"defineProps\s*\(\s*\{", script):
        start = m.end() - 1
        depth = 0
        end = start
        for i in range(start, len(script)):
            if script[i] == "{":
                depth += 1
            elif script[i] == "}":
                depth -= 1
                if depth == 0:
                    end = i
                    break
        body = script[start + 1:end]
        for pm in re.finditer(r"(?:^|,)\s*([A-Za-z_$][\w$]*)\s*:", body):
            names.add(pm.group(1))

    # defineProps(['a', 'b']) 数组写法
    for m in re.finditer(r"defineProps\s*\(\s*\[([^\]]*)\]", script):
        for piece in m.group(1).split(","):
            piece = piece.strip().strip("'\"")
            if piece:
                names.add(piece)

    # 解构：const { a, b } = ...
    for m in re.finditer(r"\bconst\s*\{([^}]*)\}\s*=", script):
        for piece in m.group(1).split(","):
            piece = piece.strip()
            if not piece:
                continue
            names.add(piece.split(":")[-1].split("=")[0].strip())

    return {n for n in names if n and re.fullmatch(r"[A-Za-z_$][\w$]*", n)}


def scoped_names(template: str) -> set:
    """收集模板里由 v-for 与作用域插槽引入的局部变量。

    这些名字只存在于模板作用域，脚本里当然找不到，不能当成未声明。
    """
    names = set()

    # v-for="(item, index) in list" / v-for="g in groups"
    for m in re.finditer(r"v-for\s*=\s*\"\s*\(?([^)\"]*?)\)?\s+(?:in|of)\s", template):
        for piece in m.group(1).split(","):
            piece = piece.strip()
            if re.fullmatch(r"[A-Za-z_$][\w$]*", piece):
                names.add(piece)

    # 作用域插槽 #default="{ row }" / v-slot="{ node, data }"
    for m in re.finditer(r"(?:#[\w-]+|v-slot(?::[\w-]+)?)\s*=\s*\"\s*\{([^}]*)\}", template):
        for piece in m.group(1).split(","):
            piece = piece.strip().split(":")[-1].strip()
            if re.fullmatch(r"[A-Za-z_$][\w$]*", piece):
                names.add(piece)

    return names


def check_file(path: Path):
    global checked
    rel = path.relative_to(SRC).as_posix()
    text = path.read_text(encoding="utf-8")

    ms = re.search(r"<script setup>(.*?)</script>", text, re.S)
    if not ms:
        return
    script = ms.group(1)

    mt = re.search(r"<template>(.*)</template>", text, re.S)
    if not mt:
        return
    template = re.sub(r"<!--.*?-->", " ", mt.group(1), flags=re.S)

    names = declared_names(script) | scoped_names(template)
    checked += 1

    # 1) 事件/指令里的裸标识符调用： @click="foo(...)"  @change="(v) => bar(v)"
    #    跳过 a.b(...) 这种成员调用 —— 被调的是对象的属性，不是本文件里的声明
    for m in re.finditer(r"(?:@|v-on:)[\w.:-]+\s*=\s*\"([^\"]+)\"", template):
        expr = m.group(1)
        for call in re.finditer(r"(?:^|[^\w$.])([A-Za-z_$][\w$]*)\s*\(", expr):
            fn = call.group(1)
            if fn in names or fn in JS_KEYWORDS:
                continue
            errors.append(f"{rel}: 事件表达式调用了未声明的函数 {fn}() -> {expr[:60]}")
        # 直接引用函数名（无括号），同样排除 a.b
        stripped = expr.strip()
        if re.fullmatch(r"[A-Za-z_$][\w$]*", stripped):
            if stripped not in names and stripped not in JS_KEYWORDS:
                errors.append(f"{rel}: 事件绑定了未声明的标识符 {stripped}")

    # 2) v-model 绑定的裸标识符
    for m in re.finditer(r"v-model(?::[\w-]+)?\s*=\s*\"([^\"]+)\"", template):
        expr = m.group(1).strip()
        if re.fullmatch(r"[A-Za-z_$][\w$]*", expr) and expr not in names and expr not in JS_KEYWORDS:
            errors.append(f"{rel}: v-model 绑定了未声明的变量 {expr}")

    # 3) PascalCase 组件标签是否已导入或全局注册
    for m in re.finditer(r"<([A-Z][A-Za-z0-9_.]*)", template):
        comp = m.group(1)
        if comp in names or comp in GLOBAL_ICONS:
            continue
        errors.append(f"{rel}: 使用了未导入的组件 <{comp}>")

    # 4) {{ }} 里的裸标识符
    for m in re.finditer(r"\{\{(.*?)\}\}", template, re.S):
        expr = m.group(1).strip()
        if re.fullmatch(r"[A-Za-z_$][\w$]*", expr) and expr not in names and expr not in JS_KEYWORDS:
            errors.append(f"{rel}: 插值引用了未声明的变量 {expr}")


for path in sorted(SRC.rglob("*.vue")):
    check_file(path)

print(f"=== 检查了 {checked} 个 .vue 文件 ===")
if errors:
    print(f"=== 错误 {len(errors)} ===")
    for e in errors:
        print("  " + e)
    sys.exit(1)
print("=== 模板引用全部可解析 ===")
