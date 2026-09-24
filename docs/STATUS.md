# Office 文档管理工具 — 项目状态记录

> 本文档是项目交接与状态追踪的**唯一事实来源**。每次改动后更新。

## 一、项目标识

| 项 | 值 |
|---|---|
| 项目名 | Office 文档管理工具（OfficeTool） |
| 依据文档 | 《Office 文档管理工具 完整设计文档 V1.0》 |
| 仓库路径 | `/home/ubuntu/projects/OfficeTool` |
| 本轮改造工作副本 | `D:\WorkBoddy\office\officetool`（Windows，从 zip 解压，无 `.git`） |
| 改造设计文档 | `OfficeTool-改造设计-v1.md`（鉴权/模板复制/页面改版，本轮唯一设计事实来源） |
| 部署形态 | **群晖 NAS + Docker**（`docker-compose.yml` + 多阶段 `Dockerfile`） |
| 技术栈 | ASP.NET Core 8 (net8.0) + EF Core 8 + SQLite(WAL) + Vue 3 + Element Plus + Vite |
| 开发/验证机 | Ubuntu 24.04.4 LTS + Docker 29.1.3 (x86_64) |

### 部署形态变更记录
| 版本 | 形态 | 说明 |
|---|---|---|
| 初版 | Windows Server 2016 + IIS + SMB/UNC | 依设计文档 §8、§10 实现，含 `WNetAddConnection2` 凭据模拟 |
| **当前** | **群晖 NAS + Docker（bind mount）** | 容器直接读写挂载进来的共享文件夹，**不再需要 SMB 凭据模拟**；`UncFileStore` 保留为可选项（`Storage:Mode=Unc`） |

---

## 二、两个「视角」（部署核心概念）

| 视角 | 形如 | 配置项 |
|---|---|---|
| 服务端（容器内） | `/data/Data/QLS2409/SEC/20260912.docx` | `Storage:DataRoot` |
| 客户端（Windows） | `\\NAS\OfficeDocs\Data\QLS2409\SEC\20260912.docx` | `Storage:ClientDataRoot` |

容器 `/data` ← bind mount ← NAS `/volume1/OfficeDocs`，两者是同一份数据的不同观察点。
界面向用户展示、供桌面插件调用的是**客户端视角**。

---

## 三、资源清单

### Core `src/OfficeTool.Core`（无 ASP.NET 依赖，可独立测试）
| 组件 | 职责 |
|---|---|
| `IFileStore` / `LocalFileStore` / `UncFileStore` + `UncConnection` | 存储抽象与两种实现 |
| `PathGuard` | 路径安全：分段校验 + 规范化 + 根前缀验证 |
| `NameValidator` | 项目/检项编码、文件名、扩展名白名单 |
| `DocumentNamingService` | `yyyyMMdd` + `A..Z`、目录锁 + `FileMode.CreateNew` 原子创建 |
| `PathLayout` | Templates/Data 平行两级目录布局 |
| `ClientPathBuilder` | 客户端可访问路径生成（UNC 拼接，避免混合分隔符） |
| `StorageOptions` / `UploadOptions` / `DesktopPluginOptions` / `ExtractionOptions` | 配置模型（含附件根与提取规则） |

### Api `src/OfficeTool.Api`
| 组件 | 职责 |
|---|---|
| `ProjectsController` / `TemplatesController` / `DocumentsController` / `SystemController` | §7.5 全部端点 + sync/logs/config/protect |
| `AttachmentsController` | 附件列表/上传/删除 + 提取（**契约已定、当前 501**） |
| `RulesController` | 提取规则列表/上传/删除/备注/复制到其他文件夹 |
| **`AuthController`**（本轮新增） | `/api/auth/*`：`me` / `sso`(Negotiate) / `login` / `logout` / `change-password` / `tokens`(CRUD) / `rights/map` / `rights` |
| **`AdminController`**（本轮新增） | `/api/admin/*`：用户 / 组 / 授权条目 CRUD + 有效权限展开 + 配置自检；仅 `IsSystemAdmin`（每请求回查实体） |
| `AppDbContext` | EF Core：Projects / Checks / Templates / Documents / **RuleNotes** / OperationLogs / **Users / Groups / UserGroups / AclEntries / ApiTokens** |
| `ProjectCatalogService` / `ContentCatalogService` | 编目与文件操作编排；四个平行目录集中在一处 |
| **`AccessControlService`**（本轮新增） | 权限解析：递进等级 + 项目级展开 + 检项级例外覆盖；10 分钟缓存 + 主动失效 |
| **`AdminService`**（本轮新增） | 用户/组/授权条目 CRUD、超管引导、有效权限展开、自锁防护、域组保护 |
| **`ApiTokenService`**（本轮新增） | 访问令牌颁发（明文仅返回一次、库存 SHA-256）/ 校验 / 吊销（即时清缓存） |
| `AttachmentService` | 附件存储与查询（**附件不进库**，直接以文件系统为准） |
| `RuleCatalogService` | 规则文件 + 备注（备注进库、规则进文件系统） |
| `OperationLogService` / `RequestContext` / `CredentialProtector` | 审计日志、请求上下文、凭据加解密 |
| `ApiExceptionMiddleware` | 领域异常 → 语义化状态码；生产不回传内部细节 |

### Docker 交付物
| 文件 | 说明 |
|---|---|
| `Dockerfile` | 三阶段：node 构建前端 → dotnet restore/publish → aspnet 运行时（非 root + HEALTHCHECK + TZ） |
| `docker-compose.yml` | 卷映射、端口、环境变量、健康检查、日志轮转 |
| `.dockerignore` / `.env.example` | 上下文裁剪与配置模板 |

### 测试与脚本
| 路径 | 说明 |
|---|---|
| `tests/OfficeTool.Core.Tests` | xUnit **77 项** |
| `tests/OfficeTool.Api.Tests` | xUnit **40 项**（含 4 条端点安全守卫：匿名白名单反射校验，已做变异测试确认会失败） |
| `scripts/api-smoke.sh` | **39 项**接口冒烟，可用 `BASE_URL` / `SHARE_ROOT` 指向任意环境；支持令牌或账号口令凭据 |
| `scripts/docker-verify.sh` | 容器形态验证：启动探测、冒烟、客户端路径、持久化、重建；**第 8 节验证鉴权开启形态** |
| `tools/vue-static-check/` | 前端静态校验（见第七节）；`node.exe` 被禁下的替代手段 |

---

## 四、访问方式

### Docker（群晖）
```bash
cp .env.example .env      # 改 NAS_HOST / SHARE_PATH 等
docker compose up -d --build
# http://<NAS_IP>:8080
```

### 本地开发
```bash
cd src/OfficeTool.Api && ASPNETCORE_ENVIRONMENT=Development dotnet run --urls http://127.0.0.1:5080
cd web && npm install && npm run dev     # http://127.0.0.1:5173
```

---

## 五、部署状态

| 环境 | 状态 | 说明 |
|---|---|---|
| 本机 Docker 容器 | ✅ **已验证** | 镜像真实构建 + 容器真实运行；39 项冒烟、客户端路径、bind mount 落盘、重启/重建持久化全部实测通过 |
| 群晖 NAS 实机 | ⏳ 未验证 | 按 `docs/deployment-synology.md` 逐项执行（主要变数是共享文件夹权限） |
| Windows + UNC 模式 | ⏳ 未验证（已不推荐） | 代码保留，`Storage:Mode=Unc` |

---

## 六、设计文档需求覆盖

| 需求 | 状态 | 位置 |
|---|---|---|
| 1 上传模板到对应项目/检项目录 | ✅ | `TemplatesController.Upload` |
| 2 按模板生成副本、按日期命名 | ✅ | `ContentCatalogService.CreateDocumentAsync` |
| 3 项目/检项两级路径 | ✅ | `PathLayout`、`PathGuard` |
| 4 网页显示列表并支持查找 | ✅ | `FilePanel.vue`、`FileQuery` |
| 5 打开/编辑模板、复制模板 | ✅ | `OpenFileDialog.vue`、`CopyTemplateAsync` |
| 6 装插件后网页调用 Office 打开 | ⏳ 前端就绪，插件待做 | `buildProtocolUrl()`；插件本体见 §9 |
| 7 未装插件时显示路径可复制 | ✅ | `OpenFileDialog.vue` |
| 8 部署在服务器 + 共享存储 | ✅ 形态已改 | 群晖 Docker + bind mount（原 Windows+SMB 方案保留） |
| 9 内网共用一套目录 | ✅ | 单一共享文件夹配置 |
| 10 普通用户可新建项目/检项并自动建目录 | ✅ | `ProjectCatalogService` |
| 11 命名 `yyyyMMdd` + `A..Z`，大写，不超 Z | ✅ | `DocumentNamingService` |
| 12 扩展名跟随模板 | ✅ | `NameValidator.EnsureAllowedExtension`（保留原大小写） |
| 13 并发自动递增保证唯一 | ✅ | 目录锁 + `FileMode.CreateNew` 原子创建 |
| 14 专用账号访问共享 | ✅ 已不需要 | 容器 bind mount + NAS ACL / PUID；UNC 模式保留 |
| 15 密码加密存储 + 凭据模拟 | ✅ 已不需要 | Data Protection；UNC 模式用 `WNetAddConnection2` |
| 16 二期：描述需求生成模板 | ⛔ 二期 | 未实现 |
| 17 左侧树按资源管理器逻辑只显示文件夹 | ✅ | `ProjectTree.vue` | 
| 18 「新建文档」独立成列、醒目按钮 | ✅ | `FilePanel.vue` | 
| 19 新建文档后直接弹出「打开文件」 | ✅ | `FilePanel.createDocument` | 
| 20 上传附件 + 提取规则（提取脚本待实现） | 🔶 **接口预留** | `AttachmentsController` / `AttachmentService` |
| 21 桌面插件（托盘 + `officetool://` 协议） | ✅ | `src/OfficeTool.Desktop` |
| 22 右侧「提取规则列表」页签，路径同模板/文档 | ✅ | `RulePanel.vue`、`ExtractionRules/{项目}/{检项}` |
| 23 建项目/检项时同步创建规则目录 | ✅ | `ProjectCatalogService.DirectoriesFor` |
| 24 规则上传 + 备注栏网页直接修改 | ✅ | `RulesController`、`RuleNotes` 表 |
| 25 规则可复制到其他文件夹（选目标路径） | ✅ | `POST /api/extraction-rules/copy` |
| 26 文档复制改为选目标路径（非当前文件夹） | ✅ | `POST /api/documents/copy`、`CopyToDialog.vue` |

### 本轮改造：鉴权与权限（原设计文档未覆盖，用户新增需求）

原设计文档的需求 1–26 只覆盖「文档管理」功能，**没有鉴权**。本轮按用户新增的三项要求改造：

| 新增需求 | 状态 | 落点 |
|---|---|---|
| A1 域（AD/LDAP）集成，把域用户拉进组 | ✅ | `UserDirectoryService.UpsertAdUserAsync`；SSO 登录时同步用户与域组关系（整体覆盖） |
| A2 以组为单位定权限，权限控制**可见性**（文件夹/文件/规则） | ✅ | `AccessControlService` + 数据级过滤；`AclEntries`（组 × 项目/检项 × 等级） |
| A3 模板复制支持**选择目标路径** | ✅ | `POST /api/templates/copy`、`CopyToDialog.vue`（`kind="template"`） |
| A4 页面改版（贴合最终用户） | ✅ | 零依赖 hash 路由 + 登录页 + 按权限裁剪的工作台 + 6 个管理页 |

设计决策、数据模型、API 清单与实施进度见 **`OfficeTool-改造设计-v1.md`**（本项目改造的唯一设计事实来源）。

### 本轮新增：提取规则（第 22–26 项）

**规则从「配置常量」改为「目录 + 文件」。** 早期版本把规则做成 `Extraction:Rules`
配置数组，但需求变成「能上传、有备注、按文件夹管理、能复制到别的文件夹」之后，
配置数组已无法承载，于是改成与模板/文档/附件同一套结构：

```
ExtractionRules/{项目}/{检项}/*.json      ← 规则本体是文件，共享盘上直接可见
数据库 RuleNotes 表：相对路径 → 备注      ← 备注是应用侧元数据，不污染共享盘
```

**四个平行目录集中在 `ProjectCatalogService.DirectoriesFor` 一处**：
创建项目/检项时一并创建，删除时一并做「非空」判定。集中管理是因为分散写
很容易在某次新增目录类型时漏掉一个分支（加附件目录时就出现过这个隐患）。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/extraction-rules?project=&check=` | 列出该文件夹的规则（含备注） |
| POST | `/api/extraction-rules/upload` | 上传规则文件 |
| DELETE | `/api/extraction-rules?project=&check=&fileName=` | 删除（备注一并清除） |
| POST | `/api/extraction-rules/note` | **网页上直接改备注**（传空串即清除） |
| POST | `/api/extraction-rules/copy` | 复制到其他项目/检项（备注一并带过去） |
| POST | `/api/documents/copy` | 文档复制到其他项目/检项（目标由用户选，非当前文件夹） |

**⚠️ RuleNotes 是后加的表，升级路径必须理解**：EF 的 `EnsureCreated()` 只在库里
一张表都没有时建整套表，对**已存在**的库什么都不做。因此启动时额外执行一次
`CREATE TABLE IF NOT EXISTS`（见 `Program.cs`）。**已实测**：建库 → 手工 DROP
RuleNotes → 重启 → 表被自动补建、无报错、备注功能可用。

**规则文件只存储、不执行**，白名单给得较宽（json/xml/yaml/csv/脚本/表格/文档），
可用 `Upload:RuleExtensions` 收窄。真正的执行要等提取脚本实现。

### 跨文件夹复制的两条命名策略（刻意不同）

| 场景 | 策略 | 理由 |
|---|---|---|
| 复制模板（同目录） | 总是加「_副本」 | 同一目录内复制**必然**重名 |
| 复制文档（跨目录） | **沿用原名**，目标已有同名才加「_副本」 | 「把这份文档放到另一个文件夹」保留原名更符合直觉 |

显式指定了新文件名又撞名 → 直接 409，不做静默改名（用户表达的是明确意图）。
规则批量复制允许**部分成功**：目标已存在的跳过并明确回报，不静默覆盖、也不因一条冲突整体失败。

**目标一律只接受项目/检项编码**，路径由服务端按配置拼装 —— 前端无法构造越界路径。

### 本轮新增：附件与提取（第 20 项）

**存储**：附件**与模板分开存放**于 `Attachments/{项目}/{检项}/`，与 `Templates`、`Data` 平级。
`Storage:AttachmentsRoot` 留空时自动推导为 `TemplatesRoot` 的同级目录。

**为什么附件不进数据库**：模板/文档进库是为了稳定 Id 与「由哪个模板生成」等索引信息；
附件只需列出/上传/删除，文件系统天然能回答。更重要的是 —— EF 的 `EnsureCreated()`
**不会给已存在的库补建表**，线上 SQLite 加表会导致 `no such table`。少一张表就少一次升级事故。

**接口契约（已定稿）**

| 方法 | 路径 | 状态 |
|---|---|---|
| GET | `/api/attachments?project=&check=` | ✅ 可用 |
| POST | `/api/attachments/upload` | ✅ 可用 |
| DELETE | `/api/attachments?project=&check=&fileName=` | ✅ 可用 |
| GET | `/api/extraction-rules` | ✅ 可用（规则来自配置 `Extraction:Rules`） |
| POST | `/api/attachments/extract` | 🔶 **501**，契约已定、脚本未实现 |

`extract` 会先做**完整校验**（项目/检项 → 附件存在 → 规则存在 → 目标文档存在），
全部通过后才抛 501。这样前端在脚本未就绪时就能把整条参数链路验证通，
并且拿到的是明确的「尚未实现」而不是含糊的 500。

**启用提取的接入点**（一处即可放开）：
1. 把 `AttachmentsController.ExtractionAvailable` 改为 `true`（前端按钮自动解禁、提示自动消失）
2. 在 `AttachmentService.ExtractAsync` 里按 `ExtractionRule.Script` 实现真正的提取
3. 在 `Extraction:Rules` 里按业务定义规则（当前只有一条占位「示例规则」）

**附件白名单**比模板宽（模板仅 6 种 Office 格式；附件默认 18 种，含 pdf/ofd/图片等），
因为附件是「待提取的原始材料」，实际业务里大量是扫描件与导出报表。可用
`Upload:AttachmentExtensions` 收窄。

---

## 七、验证结果

### 单元测试与接口冒烟（本轮更新）

| 项目 | 结果 |
|---|---|
| `dotnet test` | **117 / 117 通过**（Core 77 + Api 40） |
| `dotnet build` 编译告警 | **0 warning / 0 error** |
| 前端静态校验（`tools/vue-static-check/run_all.py --selftest`） | **4 项全通过 + 校验器自检通过** |
| `bash -n scripts/api-smoke.sh scripts/docker-verify.sh` | 通过 |
| `bash scripts/api-smoke.sh`（本机 5080） | **78 / 78 通过**（原 53 项 + 提取规则/跨文件夹复制 25 项） |
| 浏览器实测（开发 + 生产构建） | 页面加载 / 树联动 / 新建项目 / 新建文档 / 打开对话框 / 下拉菜单 全部通过，控制台 0 错误 |

> **前端无法真实构建的说明（重要）**：本机 `node.exe` 被终端安全策略禁用（AppLocker/WDAC 级），
> 既装不了依赖也跑不了 `vite build`。为不交付「从没人跑过的代码」，采取两条替代措施：
> 1. **不引入 vue-router**，改为自研零依赖 hash 路由 `web/src/router.js`（接口形状对齐 vue-router 便于日后替换）；
> 2. 用 `tools/vue-static-check/` 一组静态校验替代编译期检查，并**先自检校验器本身**（用临时生成的坏样本
>    验证它确实会报错）再使用。**交付前仍应在能装 node 的机器上执行 `npm ci && npm run build` 复核一遍。**

### 容器形态（`scripts/docker-verify.sh`）
**结果：8 / 8 通过**（在 Ubuntu 24.04 + Docker 29.1.3 x86_64 实测）

| 检查项 | 结果 |
|---|---|
| 镜像构建 | ✅ `officetool:1.0.0`，磁盘占用 394MB / 内容 114MB |
| 容器健康 | ✅ 3 秒就绪，`docker inspect` 状态 running |
| 存储根探测 | ✅ 启动日志输出「模板根/数据根 校验通过（可读写）」 |
| 接口冒烟 | ✅ 复用 39 项脚本，**39 / 39 通过** |
| 客户端视角路径 | ✅ `clientDataRoot` = `\\NAS\OfficeDocs\Data`，列表 `accessPath` 为该形式 |
| bind mount 落盘 | ✅ 宿主机共享目录出现 4 个文件（模板 ×2 + 文档 ×2） |
| 容器重启 | ✅ 项目数/文档数不变 |
| **容器重建**（数据卷保留） | ✅ 文档数不变 |

浏览器实测（容器 `http://127.0.0.1:8080/`）：
页面加载 ✅、树联动 ✅、打开文件弹窗显示 `\\NAS\OfficeDocs\Templates\QLS2409\SEC\...` ✅、
协议链接 `officetool://open?path=%5C%5CNAS%5C...` ✅、**控制台 0 错误 / 0 条日志** ✅

### 构建过程中修正的三个部署可用性问题
1. **基础镜像拉取**：`mcr.microsoft.com` 在国内约 100KB/s（预计 85 分钟）。
   验证时改用 daocloud 代理拉取后 `docker tag` 成官方名，Dockerfile 无需为镜像源妥协。
   → 群晖上同理，文档已给出镜像加速配置。
2. **npm 官方源不可用**：群晖实测 `npm ci` 跑到 74 秒后 `Exit handler never called!`
   （npm 自身崩溃）。新增构建参数 `ARG NPM_REGISTRY`，默认官方源保持可移植，
   国内用 `NPM_REGISTRY=https://registry.npmmirror.com`。
   **实测：官方源 74s 失败 → 镜像源 6s 装完 83 个包。**
3. **apt 硬依赖（已彻底移除）**：原 Dockerfile 为装 `curl`（HEALTHCHECK 用）
   而引入 apt，导致 `deb.debian.org` 连不上时整个构建失败——国内 NAS 上实测
   `connection timed out`。经查验运行时镜像**自带 tzdata(2026b) 与 bash**，
   于是改为：不装任何 apt 包，HEALTHCHECK 用 bash 的 `/dev/tcp` 发真实 HTTP 请求。
   **实测：镜像内确认无 curl，HEALTHCHECK 状态 healthy，时区 CST 正确。**
4. **npm 大流量传输被卡（群晖现场）**：诊断确认 NAS 能访问 npm 源
   （`wget` 探测 npmjs.org / npmmirror.com 均 OK），但 `npm ci` 仍跑 744 秒后
   `Exit handler never called!`。小请求通、大流量传不动，属 NAS 网络/内存层面问题。
   → 提供 `prebuilt/webdist` 机制：部署包内含已构建的前端，
   Dockerfile 检测到即跳过 `npm ci` 与 `vite build`，**构建完全不访问 npm 源**。
   **实测：跳过后镜像功能完好 —— SPA 正常、资源大小精确匹配、
   HEALTHCHECK healthy、接口冒烟 39/39。**
   打包脚本 `scripts/make-deploy-package.sh` 可复现生成该部署包。
5. **运行用户与数据卷归属（群晖现场）**：共享文件夹走 Synology ACL，
   容器内置 uid 1654 被拒；但直接改用 compose 的 `user:` 切到 1026 后，
   `/app/state` 等**数据卷仍是 1654 建的**，应用写不进 SQLite 目录 → 启动即失败（端口不监听）。
   → 改为「入口脚本以 root 修正 state/keys/logs 归属 → setpriv 降权到 PUID:PGID」，
   使运行用户可随时切换且旧卷自愈。
   **实测：1654 建的卷改用 PUID=1026 后，卷属主自动更正为 `1026:users`，
   应用就绪、建项目返回 201、共享目录内文件属主 `1026:100`、HEALTHCHECK healthy、冒烟 39/39。**
   同时启动错误日志现在会打印 `当前运行用户 uid=xxxx` 与处置步骤，可自诊断。
6. **附加组被清空导致「有权限却 denied」（群晖现场）**：
   现场 `synoacltool -get` 显示 ACL 只授给 `group:administrators`，
   而账号 `koliol` 的权限正是通过**附加组** `administrators(101)` 获得的。
   入口脚本当时用了 `setpriv --clear-groups`，把附加组全清掉，进程只剩 `users(100)`
   → 复现「明明这个账号能访问，容器却报 Permission denied」。
   → 入口脚本新增 `SUP_GIDS`（照抄 `id` 的 groups），用
   `setpriv --groups=<PGID>,<SUP_GIDS>` 保留附加组，并对重复组号去重。
   **实测（用真实组 ACL 复现，只授 gid 101）：**
   - `PUID=1026 PGID=100`（附加组 `[100]`）→ 模板根 不可用 ← 精确复现故障
   - 加上 `SUP_GIDS=100,101`（附加组 `[100,101]`）→ 校验通过、建项目 **201**、
     共享目录内文件属主 `1026:100`、冒烟 **39/39**

---

## 八、Git 稳定节点

| 提交 | 内容 |
|---|---|
| `76cd3a1` | Core 层：领域模型、路径安全、命名并发、文件存储抽象 |
| `63ae07a` | API 层：全量端点 + EF Core/SQLite + 统一异常处理 |
| `a08e0d3` | 前端 + 项目文档与 Windows 部署清单 |
| `0d757d2` | **转向 Docker/群晖**：Storage 配置重构 + 客户端视角路径 + Dockerfile/compose |
| `afe09e6` | 入口脚本保留附加组（修「账号能访问但容器被拒」） |
| `ef3e2d2` | 资源管理器式目录树、新建文档独立列、附件与提取接口预留 |
| `26f0b13` | Windows 桌面托盘插件 + 交付打包 |
| `9e88271` | 提取规则改为目录+文件（备注/上传/跨文件夹复制）+ 文档跨文件夹复制 |
| `26f0b13`… | EF Migrations + 旧库 Baseline；列表查询下推 SQL；`_trash` 回收站；`/api/projects/tree` |
| **（本轮，未推送）** | **鉴权体系**：双通道认证 + 递进权限 + 数据级可见性过滤 + 权限管理 API；模板复制选目标路径；前端改版（零依赖路由 + 登录/403/回收站/令牌/用户/组/授权页） |

> **本轮改动尚未推送到 GitHub。**
>
> 本机工作副本是从 zip 解压而来，没有 `.git`；且企业环境禁用了 git 的 https remote helper
> （`remote helper 'https' aborted session`），因此推送走 **GitHub REST API**（详见
> `OfficeTool-改造设计-v1.md` 第 11 节「推送流程」）。
>
> **当前阻塞**：换发后的 PAT **可读不可写** —— `GET /user` → 200（`login=koliol`）、读取仓库/ref 均 200，
> 但 `POST /repos/koliol/officetool/git/blobs` → **403 `Resource not accessible by personal access token`**，
> 即 fine-grained PAT 缺 **Contents: Read and write**。
> （注意：`/repos` 响应里的 `permissions` 字段是**用户对该仓库的角色**，不是令牌授权，别被它误导。）
> 修好后执行 `GH_TOKEN=… python _push/gh_push.py --apply` 即可（约 63 次 API 调用）。
> **远端完全未被改动**：第 1 个 blob 就失败，脚本随即退出，未建任何 tree/commit，`main` 仍是 `1747a87d`。
>
> **推送前侦察已完成**（对远端 tree 逐文件比对 blob SHA）：远端 HEAD `1747a87d67a1`（`2026-09-13`）、
> 103 个 blob；本地 134 个文件；**新增 31 / 修改 32 / 未改动 71 / 远端独有 0**。
> 71 个文件与远端字节级相同 → **基线一致**，且本次为**纯增量**（无需删除远端任何文件）。
> 仓库是 public，只读侦察可匿名进行（限额 60 次/小时）。

---

## 九、已知问题与待办

1. **桌面插件本体**：见 `src/OfficeTool.Desktop`（WinForms 托盘 + `officetool://` 协议注册）。
2. **提取脚本未实现**：接口契约、参数校验、前端入口均已就绪，调用返回 501。
   启用只需改 `AttachmentsController.ExtractionAvailable` + 实现 `ExtractAsync`（见第六节）。
3. **提取规则只有占位**：规则文件需要按业务实际格式上传（当前白名单较宽，
   可用 `Upload:RuleExtensions` 收窄）；`Extraction:Rules` 配置数组已废弃。
4. **群晖实机未验证**：按 `docs/deployment-synology.md` 执行，权限（PUID/ACL/附加组）是主要变数。
5. **鉴权已落地（本轮）**：三种通道统一到同一张应用 Cookie ——
   - **SSO**（Negotiate/Kerberos，域环境）、**本地账户**（表单登录）、**Bearer 访问令牌**（外部脚本/工具）；
   - 行为开关 `Auth:Enabled`（默认 true）；**设 `false` 可退回「全放行」**（`UserAccess.Unrestricted`），
     用于内网临时排障，但同步与结构性维护仍按超管语义处理；
   - 权限语义为**四级递进** `None < Read < Write < Manage < RuleManage`（`AclEntries.Level`），
     项目级条目可被**检项级条目覆盖**（含显式 `None` 黑名单例外）；
   - **数据级可见性过滤**：模板/文档列表下推 SQL、项目树与项目列表裁剪、规则/附件/回收站判权；
   - 权限管理 API `/api/admin/*` 仅 `Users.IsSystemAdmin` 可用（**每次请求回查实体**，不信任 Cookie 声明）；
   - 首次部署自动生成本地超管（密码打印在启动日志中，仅一次），可自助改密。
   - **部署前务必设定** `Auth__*` 相关环境变量并阅读 `docs/deployment-synology.md` 第七节。
   - 已知限制见下方「待办」。
6. **数据库升级已改为 EF Migrations**：
   - 迁移在 `src/OfficeTool.Api/Data/Migrations/`
   - 启动走 `DatabaseInitializer`：空库 `Migrate()`；**旧 EnsureCreated 库自动 Baseline**
     （标记 `0001_InitialCreate` 已应用）后再跑增量；不再手写 `CREATE TABLE IF NOT EXISTS`
   - 改表结构必须新增迁移，勿改历史迁移文件
   - 升级前仍建议备份 `officetool-state` 卷
7. **删除默认进回收站**（本轮）：模板/文档/附件/规则删除会移到 `{TrashRoot}/{Kind}/{yyyyMMdd}/`，
   库表 `TrashItems` 登记；API：`GET /api/trash`、`POST /api/trash/{id}/restore`、`DELETE /api/trash/{id}`。
   `TrashRoot` 默认与 Templates 同级 `_trash`，必须与业务根同一挂载卷。
   回收站**不计入**「项目/检项非空禁删」。
8. **列表查询已下推 SQL**（本轮）：模板/文档列表 Join + Where/Count/分页在数据库完成，
   并为 `Documents.CreatedAt`、`Templates.ModifiedAt` 建索引。
9. **前端树一次拉全**（本轮）：`GET /api/projects/tree`，`store.loadProjects` 不再 N+1。
10. **`ContentCatalogService` 仍偏大**：已抽出 `TrashService`，完整拆 Template/Document 服务待后续。
11. **前端回收站 UI 已完成（本轮）**：`web/src/views/TrashView.vue`，支持列表 / 恢复 / 彻底删除。
12. **前端包体积**：Element Plus 全量引入，约 1.27MB（gzip 410KB）。内网可接受；可改按需引入优化。
13. **`npm audit` 告警**：esbuild/vite 开发服务器相关，仅影响本地 dev server，不影响构建产物；升级 vite 8 为破坏性变更，暂缓。
14. 二期项：**登录鉴权 / 角色权限（已在本轮完成）** / 在线预览 / 版本管理 / 审批流 / AI 生成模板。

### 鉴权相关的已知缺口（本轮，交付前留意）

| 项 | 说明 |
|---|---|
| 前端未经真实构建 | `node.exe` 被禁，只做了静态校验。**交付前应在能装 node 的机器上 `npm ci && npm run build`** |
| Negotiate SSO 未实机验证 | 需域环境 + keytab + FQDN 访问，本地无法验证；已保留表单登录兜底 |
| `MustChangePassword` 仅为提示，未强制 | 本地账号初始密码由管理员转述，中途可能被第三方看到；**有意不强制**——强制改密需白名单放行 `change-password` 等端点，一旦有误会把账号彻底锁死，内网工具收益不抵风险 |
| 群晖实机未验证 | 与改造前一致，NAS 现场（共享文件夹 ACL、反向代理）仍是唯一未验证环节 |
| 授权矩阵视图 | 当前权限管理页是「条目列表 + 筛选」，超管以外场景够用；批量配置（几十组 × 几十项目）需矩阵视图 |
| 本地账户自助注册 | 当前不支持（首次部署由系统生成超管），是否需要待定 |
| 端口/子路径部署 | 路由改用 hash 后挂在 `/officetool` 之类子路径下不会失效，但未实测 |

### 已知行为约定（非缺陷）
- 同名模板重复上传 → 409，需先重命名或删除（不静默覆盖）。
- 单目录每天最多 27 个文档（`yyyyMMdd` + `A..Z`），超出报「当日编号已满」。
- 项目/检项目录非空时禁止删除；删除时空目录一并清理。
  **四个目录（Templates/Data/Attachments/ExtractionRules）都计入非空判定；`_trash` 不计入。**
- 删除模板/文档/附件/规则 → **移入回收站**（可恢复）；彻底删除需调用 `DELETE /api/trash/{id}`。
- 左侧树只呈现文件夹（项目/检项两级），文件一律在右侧列表操作。
- 复制模板（同目录）总是加「_副本」；复制文档（跨目录）优先沿用原名、撞名才加「_副本」。
- 规则批量复制允许部分成功：目标已存在的跳过并回报，不覆盖、不整体失败。
- 移动/复制/删除规则时备注跟着走；在共享盘上手工删掉规则文件会留下孤儿备注（无害，重新上传同名规则会复用）。
- 找不到的 `/api/*` 返回 JSON 404，不被 SPA 回退吞成 HTML。
- 容器时区未设 `TZ` 会导致凌晨生成的文档日期差一天。
