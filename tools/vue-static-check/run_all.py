# -*- coding: utf-8 -*-
"""
前端静态校验总入口。

背景：本机的终端安全策略**禁止执行 node.exe**（AppLocker/WDAC 级别，提权也无法绕过），
所以 `npm install` 与 `vite build` 都跑不了。一份"没人跑过"的前端代码交付出去
风险很高，于是用一组静态检查替代编译期验证。

四道检查（按「能查出什么」排列，越靠前越致命）：
  1. check_syntax.py         语法层：括号/引号/模板串闭合、import 能否解析
  2. check_sfc_structure.py  SFC 层：template / script / style 标签配平
  3. check_template_refs.py  引用层：模板里调的变量与函数是否在 script 里声明
  4. check_api_calls.py      契约层：前端调的 api 方法是否在 api.js 里定义

用法：
    python run_all.py                 # 检查默认前端目录
    python run_all.py <src 目录>       # 检查指定目录
    python run_all.py --selftest      # 先自检校验器本身是否有效

注意：这些检查**不是**编译器。它们能挡掉绝大多数手写低级错误，
但替代不了真正的构建。交付前若条件允许，仍应在能装 node 的机器上跑一次
`npm ci && npm run build`。
"""
import os
import subprocess
import sys
from pathlib import Path

# 中文 Windows 的控制台默认是 GBK。若子进程输出里有控制台编码不了的字符
# （最常见的是解码失败产生的 U+FFFD），直接 print 会抛 UnicodeEncodeError
# 把整个校验器带崩 —— 而它本该只是"报告结果"。
# 这里保留控制台的原有编码，只把无法编码的字符替换掉，保证不崩。
try:
    sys.stdout.reconfigure(errors="replace")
    sys.stderr.reconfigure(errors="replace")
except Exception:  # noqa: BLE001 - 老版本/被重定向时忽略
    pass

HERE = Path(__file__).parent

# 默认目标：<repo>/web/src。也可用 run_all.py <目录> 显式指定。
DEFAULT_SRC = Path(__file__).resolve().parent.parent.parent / "web" / "src"

CHECKS = [
    ("语法（括号/引号/模板串/import）", "check_syntax.py"),
    ("SFC 结构（template/script/style）", "check_sfc_structure.py"),
    ("模板引用（变量与函数声明）", "check_template_refs.py"),
    ("API 契约（调用与定义一致）", "check_api_calls.py"),
]


def run(script: str, src: Path) -> tuple[int, str]:
    # PYTHONIOENCODING 必须显式指定：子进程的 stdout 是管道时，
    # Python 在 Windows 上会沿用系统区域编码（GBK）输出，而父进程按 UTF-8 解码，
    # 于是中文全变成 U+FFFD，报告读不成句。
    env = dict(os.environ, WEB_SRC=str(src), PYTHONIOENCODING="utf-8")
    proc = subprocess.run(
        [sys.executable, str(HERE / script)],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
    )
    return proc.returncode, ((proc.stdout or "") + (proc.stderr or "")).strip()


def main() -> int:
    args = [a for a in sys.argv[1:] if a != "--selftest"]

    if "--selftest" in sys.argv:
        print("##### 校验器自检 #####")
        rc, out = run("selftest_syntax.py", DEFAULT_SRC)
        print(out)
        if rc != 0:
            print("\n自检未通过，后续检查结果不可信。")
            return 1
        print()

    src = Path(args[0]) if args else DEFAULT_SRC
    if not src.is_dir():
        print(f"目录不存在：{src}")
        return 2

    print(f"##### 目标：{src} #####\n")

    failed = []
    for label, script in CHECKS:
        rc, out = run(script, src)
        status = "通过" if rc == 0 else "失败"
        print(f"───── {label} ───── [{status}]")
        print(out)
        print()
        if rc != 0:
            failed.append(label)

    if failed:
        print(f"##### 结果：{len(failed)} 项失败 #####")
        for f in failed:
            print("  - " + f)
        return 1

    print(f"##### 结果：{len(CHECKS)} 项全部通过 #####")
    return 0


if __name__ == "__main__":
    sys.exit(main())
