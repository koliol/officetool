# 前端静态校验工具

## 为什么存在

本项目的目标部署环境（群晖 NAS）和开发环境都出现过 **`node.exe` 被终端安全策略禁用**
（AppLocker / WDAC 级别，提权也无法绕过）的情况。后果是：

- `npm install` 装不了依赖
- `vite build` 跑不了，前端**没有任何编译期验证**

一份"没人跑过"的前端代码交付出去风险很高。这组脚本用静态分析替代一部分编译期检查。

## 用法

```bash
# 检查默认目录（<repo>/web/src），并先自检校验器本身
python tools/vue-static-check/run_all.py --selftest

# 检查指定目录
python tools/vue-static-check/run_all.py /path/to/web/src

# 也可以单独跑某一项（WEB_SRC 环境变量覆盖目标目录）
WEB_SRC=/path/to/src python tools/vue-static-check/check_syntax.py
```

退出码 `0` = 全部通过，非 `0` = 有失败项。

## 四道检查

| 脚本 | 层次 | 能查出什么 |
|---|---|---|
| `check_syntax.py` | 语法 | 括号 / 引号 / 模板串是否闭合；`import` 路径能否解析；裸包名是否在 `package.json` 里声明 |
| `check_sfc_structure.py` | SFC 结构 | `<template>` / `<script>` / `<style>` 标签配平；模板里是否误混入 `</script>` |
| `check_template_refs.py` | 模板引用 | 模板里调用的函数与变量是否在 `<script>` 里声明过 |
| `check_api_calls.py` | API 契约 | 视图里调用的 `xxxApi.method` 是否真的在 `api.js` 里定义 |

`selftest_syntax.py` 用**临时生成**的坏样本验证 `check_syntax.py` 真的会报错。
这一步不能省——一个永远返回 0 的校验器和真的通过，输出完全一样。

## 设计要点

**为什么 `check_syntax.py` 要手写词法扫描器而不是用正则。**
正则无法正确处理这几类情况，而它们在本项目的前端代码里全都真实出现过：

- 字符串里的 `//`（例如 URL 或正则字面量）会被误判成行注释
- 模板串里的 `${}` 嵌套（本项目大量使用，且存在**模板串套模板串**，如
  `` `#${p.startsWith('/') ? p : `/${p}`}` ``）
- 三种引号混用与转义
- 正则字面量 `/.../ ` 与除号 `/` 的区分

扫描器用显式栈维护上下文：`${...}` 被视为**字符串内部**（不产生括号事件，
与 JS 语法一致——整个模板串是一个表达式 token），插值内部的括号自成一体单独配平。

**部分已知局限**（都是有意取舍）：

- 只校验模板里引用的**函数与变量名是否存在**，不做类型检查
- `check_api_calls.py` 的"未被调用"列表对嵌套命名空间（`adminApi.users.list`）
  会漏报，所以只作为参考信息，不作为失败条件
- 不做 CSS、Props 类型、运行时行为的任何验证

## 重要提醒

**这组工具不是编译器，替代不了真正的构建。**

它们挡掉了手写 Vue 组件时绝大多数低级错误（写错导入路径、漏声明函数、
括号不闭合、调用不存在的 API），但**无法**发现类型错误、运行时逻辑错误、
打包配置问题。

**上线前若条件允许，仍应在能装 node 的机器上跑一次：**

```bash
cd web && npm ci && npm run build
```

需要注意的是，本仓库的 Docker 构建会执行 `npm ci && vite build`
（`prebuilt/webdist/index.html` 不存在时），所以**容器镜像里的前端是真实构建过的**。
这组工具的作用是让本地在无 node 环境下也有基本的正确性保障。
