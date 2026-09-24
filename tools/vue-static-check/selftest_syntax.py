# -*- coding: utf-8 -*-
"""
自检：用**临时生成**的错误样本验证 check_syntax.py 真的能抓错。

为什么需要自检：
  「0 errors」这个结论只有在「有错时确实会报」的前提下才有价值。
  一个永远返回 0 的校验器和真的通过，输出完全一样。

样本在临时目录里现造现删，不依赖仓库里的任何文件。
"""
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).parent

# 中文 Windows 控制台默认是 GBK。报告文本里一旦出现控制台编码不了的字符，
# 直接 print 会抛 UnicodeEncodeError 把校验器带崩 —— 而它本该只是"报告结果"。
# 保留控制台原有编码，只把无法编码的字符替换掉。
try:
    sys.stdout.reconfigure(errors="replace")
    sys.stderr.reconfigure(errors="replace")
except Exception:  # noqa: BLE001
    pass

CHECKER = HERE / "check_syntax.py"

BAD_JS = """import { missing } from './nope'
import axios from 'axios'
import lodash from 'lodash'

function f() {
  const a = `unclosed ${x
  return a
}
"""

BAD_VUE_STRING = """<template>
  <div>{{ x }}</div>
</template>
<script setup>
const s = 'abc
</script>
"""

BAD_VUE_BRACKET = """<template><div/></template>
<script setup>
function g() {
  return (1 + 2
}
</script>
"""

# 与上面不同：这个是**真的少一个右括号**（括号不匹配），
# 上面那个是提前多了一个 `}`。两条错误路径都要覆盖。
BAD_VUE_UNCLOSED = """<template><div/></template>
<script setup>
function h() {
  if (a) {
    b()
  }
</script>
"""

GOOD_VUE = """<template>
  <div :class="cls">{{ label }}</div>
</template>
<script setup>
import { computed, ref } from 'vue'
const n = ref(0)
const cls = computed(() => `n-${n.value}`)
const label = computed(() => `${n.value} / ${n.value}`)
</script>
"""

PKG = """{
  "name": "selftest",
  "private": true,
  "dependencies": {
    "axios": "^1.7.9",
    "vue": "^3.5.13"
  }
}
"""


def main() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="vuecheck-selftest-"))
    try:
        src = tmp / "src"
        src.mkdir(parents=True)
        (tmp / "package.json").write_text(PKG, encoding="utf-8")
        (src / "bad.js").write_text(BAD_JS, encoding="utf-8")
        (src / "bad2.vue").write_text(BAD_VUE_STRING, encoding="utf-8")
        (src / "bad3.vue").write_text(BAD_VUE_BRACKET, encoding="utf-8")
        (src / "bad4.vue").write_text(BAD_VUE_UNCLOSED, encoding="utf-8")
        (src / "good.vue").write_text(GOOD_VUE, encoding="utf-8")

        # PYTHONIOENCODING：子进程 stdout 是管道时，Windows 上 Python 会按系统区域编码
        # （GBK）输出，父进程按 UTF-8 解码会把中文读成 U+FFFD。
        env = dict(os.environ, WEB_SRC=str(src), PYTHONIOENCODING="utf-8")
        proc = subprocess.run(
            [sys.executable, str(CHECKER)],
            capture_output=True,
            text=True,
            encoding="utf-8",
            env=env,
        )
        out = (proc.stdout or "") + (proc.stderr or "")
        print(out)

        failures: list[str] = []

        for name in ("bad.js", "bad2.vue", "bad3.vue", "bad4.vue"):
            if name not in out:
                failures.append(f"{name} 的已知错误未被检出")

        if "good.vue" in out:
            failures.append("good.vue 被误报")

        # 错误类型也要命中：报错但报错类型不对，同样属于失效
        for label, needle in [
            ("无法解析的相对导入", "'./nope'"),
            ("未声明的裸包", "'lodash'"),
            ("未闭合模板串", "模板串"),
            ("未闭合字符串", "字符串"),
            ("括号不匹配", "不匹配"),
            ("括号未闭合", "没有闭合"),
        ]:
            if needle not in out:
                failures.append(f"未检出：{label}")

        # 有错时退出码应为 1
        if proc.returncode != 1:
            failures.append(f"校验器退出码应为 1（有错），实际 {proc.returncode}")

        print()
        if failures:
            print("=== 自检失败 ===")
            for f in failures:
                print("  " + f)
            return 1

        print("=== 自检通过：坏样本全部报错、好样本零误报 ===")
        return 0
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
