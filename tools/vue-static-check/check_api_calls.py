# -*- coding: utf-8 -*-
"""
校验前端 api.js 的导出方法 与 视图里实际调用的 xxxApi.method 是否一一对应。

为什么需要这个：node 被终端策略禁用，跑不了 vite build，语法/引用错误
只会在运行时炸。而 axios 的 `client.get(...)` 写错路径不会有任何编译期保护，
调用一个不存在的 api 方法则会抛 TypeError —— 这类问题必须靠静态比对兜住。
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

API = SRC / "api.js"

errors: list[str] = []


def extract_exports(text: str) -> dict[str, set[str]]:
    """
    解析 `export const xxxApi = { ... }` 与嵌套一层的方法名。

    只做一层嵌套（users / groups / acl 这种），够用且不会误判。
    """
    result: dict[str, set[str]] = {}

    # 顶层：export const nameApi = {
    for m in re.finditer(r"export\s+const\s+(\w+)\s*=\s*\{", text):
        name = m.group(1)
        start = m.end() - 1
        depth = 0
        i = start
        while i < len(text):
            ch = text[i]
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        body = text[start + 1 : i]

        methods: set[str] = set()
        # 顶层方法： `name: (...)` 或 `name(...)`，缩进 2 空格
        for mm in re.finditer(r"^\s{2}(\w+)\s*[:(]", body, re.M):
            methods.add(mm.group(1))
        # 嵌套对象的方法：`sub: { ... name: (`, 缩进 4 空格
        for mm in re.finditer(r"^\s{4}(\w+)\s*[:(]", body, re.M):
            methods.add(mm.group(1))
        result[name] = methods

    return result


def main() -> int:
    text = API.read_text(encoding="utf-8")
    exports = extract_exports(text)

    if not exports:
        print("!! 未能从 api.js 解析出任何 export const")
        return 1

    print("=== api.js 导出的 API 组 ===")
    for name, methods in sorted(exports.items()):
        print(f"  {name}: {len(methods)} 个方法")

    # 所有 api 组名，用于识别 "xxxApi.method" 里的 xxxApi
    api_names = set(exports.keys())

    # 扫描 .vue 与 .js —— auth.js / store.js 里也有大量 api 调用，
    # 只扫 .vue 会漏掉一半，并让「未调用」列表充满误报。
    sources = sorted(list(SRC.rglob("*.vue")) + list(SRC.rglob("*.js")))

    checked = 0
    for path in sources:
        if path.name == "api.js":
            continue
        rel = path.relative_to(SRC).as_posix()
        body = path.read_text(encoding="utf-8")

        # 去掉注释，避免注释里的示例被当成调用
        stripped = re.sub(r"//[^\n]*", " ", body)
        stripped = re.sub(r"/\*.*?\*/", " ", stripped, flags=re.S)
        stripped = re.sub(r"<!--.*?-->", " ", stripped, flags=re.S)

        for m in re.finditer(r"\b(\w+Api)\.(\w+)\b", stripped):
            group, method = m.group(1), m.group(2)
            if group not in api_names:
                continue
            checked += 1
            if method not in exports[group]:
                line = stripped[: m.start()].count("\n") + 1
                errors.append(f"{rel}:{line}  调用了不存在的 {group}.{method}()")

    # 反向：定义了却从没被调用的方法（不报错，只提示）
    used_by_group: dict[str, set[str]] = {k: set() for k in api_names}
    for path in sources:
        if path.name == "api.js":
            continue
        body = path.read_text(encoding="utf-8")
        body = re.sub(r"<!--.*?-->", " ", body, flags=re.S)
        for m in re.finditer(r"\b(\w+Api)\.(\w+)\b", body):
            if m.group(1) in used_by_group:
                used_by_group[m.group(1)].add(m.group(2))

    # 嵌套命名空间（adminApi.users 这类）由外层对象承载，
    # 真正的叶子方法名不会直接出现在 `adminApi.xxx` 里，跳过以免满屏误报。
    nested = {name for name, methods in exports.items() if name in ("adminApi",) and "users" in methods}

    print(f"\n=== 检查了 {checked} 处 api 调用 ===")
    if errors:
        print("\n=== 错误 ===")
        for e in errors:
            print("  " + e)
        return 1

    print("=== 全部存在 ===")

    unused = []
    for g, methods in exports.items():
        if g in nested:
            continue
        unused.extend(f"{g}.{mm}" for mm in sorted(methods - used_by_group[g]))
    if unused:
        print(f"\n=== 未被调用（{len(unused)} 个，仅供参考） ===")
        for u in unused:
            print("  " + u)

    return 0


if __name__ == "__main__":
    sys.exit(main())
