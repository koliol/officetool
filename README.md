# Office 检验记录（ELN）· Office 文档管理工具

内网检验记录与文档管理平台，产品形态正在从「模板文件管理」转向 **ELN（电子实验记录）**：

- **在线模板**：记录格式（分区/字段/表格列）在网页上直接定义，版本化；不再上传 .docx 模板；
- **填写记录**：选模板 → 在项目/检项下创建记录 → 表单填写 → 提交冻结（提交后不可改，只能作废重开）；
- **PDF 提取**：上传仪器导出的 PDF 报告，由**产品内置的提取规则**（已内置：不溶性微粒，
  管理员设置生效范围后可用）解析出字段候选值，人工确认后填入记录；
  每次解析落审计（状态、命中片段、截断原文）；
- **历史文件**：ELN 之前的模板/文档/附件原样保留，只读访问。

依据《Office 文档管理工具 完整设计文档 V1.0》实现。

> 状态与需求覆盖 → **[docs/STATUS.md](docs/STATUS.md)**
> 部署一：群晖 NAS（Docker） → **[docs/deployment-synology.md](docs/deployment-synology.md)**
> 部署二：Windows 原生（Windows 服务） → **[docs/deployment-windows-native.md](docs/deployment-windows-native.md)**

---

## 快速开始

### 方式一：Docker（群晖 NAS / 任意 Linux）

```bash
cp .env.example .env      # 按实际环境修改 NAS_HOST / SHARE_PATH 等
docker compose up -d --build
# 浏览器访问 http://<NAS_IP>:8080
```

详见 [docs/deployment-synology.md](docs/deployment-synology.md)。

### 方式二：Windows 原生（Windows 服务，无需 Docker）

管理员 PowerShell：

```powershell
cd scripts\windows
.\install.ps1 -ClientRoot "\\NAS\OfficeDocs"   # 数据放本机则直接 .\install.ps1
```

脚本完成：前后端发布 → 数据目录 → 配置生成 → 注册 Windows 服务
（开机自启、崩溃自动重启 10s/30s/60s）→ 防火墙 → 启动 → 健康检查。
升级 `.\upgrade.ps1`（自动备份 SQLite）；卸载 `.\uninstall.ps1`。

详见 [docs/deployment-windows-native.md](docs/deployment-windows-native.md)。

### 方式三：本地开发

```bash
# 后端（默认 5080）
cd src/OfficeTool.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --urls http://127.0.0.1:5080

# 前端（5173，/api 自动代理到 5080）
cd web && npm install && npm run dev      # → http://127.0.0.1:5173
```

开发环境使用 `Storage:Mode=Local`，文件落在 `./officetool-share/`。

## 测试

```bash
dotnet test                       # 189 项单元/集成测试
bash scripts/api-smoke.sh         # 接口冒烟（需后端在 5080 运行）
```

## 两个「视角」——部署前必读

同一份文件有两个地址，配置里要分别描述：

| 视角 | 形如 | 配置项 |
|---|---|---|
| 服务端（容器内） | `/data/Data/QLS2409/SEC/20260912.docx` | `Storage:DataRoot` |
| 客户端（Windows） | `\\NAS\OfficeDocs\Data\QLS2409\SEC\20260912.docx` | `Storage:ClientDataRoot` |

容器里 `/data` 通过 volume 映射到 NAS 的共享文件夹，因此两者指向同一份数据。
界面上展示给用户、供其复制到资源管理器或桌面插件打开的，是**客户端视角**的路径。

## 目录结构

```
src/OfficeTool.Core/     领域逻辑（无 ASP.NET 依赖，可独立测试）
  Abstractions/          IFileStore 文件存储抽象
  Storage/               LocalFileStore（容器/本地）/ UncFileStore + UncConnection（Windows UNC）
  Services/              PathGuard 路径安全（含符号链接逃逸守卫）、FormSchema 表单结构校验、
                         ExtractionRule/ExtractionEngine 提取规则引擎、PdfTextReader（PdfPig）、
                         RecordDataValidator 记录校验、NameValidator、DocumentNamingService、PathLayout
  Models/ Options/       实体与配置（含 ELN 六表与 SecurityOptions）
src/OfficeTool.Api/      ASP.NET Core 8 Web API
  Controllers/           form-templates / form-records / extraction-rules(管理) /
                         projects / templates / documents / attachments / auth / admin / system
  Services/              FormTemplateService / FormRecordService / ExtractionService /
                         ExtractionRuleAdminService / BuiltInContentSeeder / 编目 / 审计
  Data/                  EF Core 上下文 + 迁移（0001–0006）
  Middleware/            统一异常 → 语义化状态码；强制首次改密；上传体积统一
tests/                   单元/集成测试
web/                     Vue 3 + Element Plus 前端（记录中心 / 模板库 / 设计器 / 填写页 / 规则管理）
scripts/windows/         Windows 原生部署（install / upgrade / uninstall）
scripts/api-smoke.sh     接口冒烟测试
Dockerfile               多阶段构建（前端 → 后端 → 运行时）
docker-compose.yml       群晖 / Linux 部署编排
docs/                    状态记录与部署文档
```

## 核心设计要点

| 主题 | 做法 |
|---|---|
| 路径安全 | 所有路径基于配置根拼接，分段校验（禁分隔符/`..`），规范化后验前缀，**再做逐段符号链接逃逸检查** |
| 命名去重 | 进程内按目录 `SemaphoreSlim` 串行 + 跨进程 `FileMode.CreateNew` 原子创建兜底 |
| 存储解耦 | `IFileStore` 抽象；容器/NAS 用 LocalFileStore（bind mount），Windows UNC 用 UncFileStore（连接状态带 TTL 重探测） |
| 双视角路径 | `ClientPathBuilder` 统一生成客户端可访问路径，避免 Linux 下混合分隔符 |
| ELN 不可篡改 | 记录提交即冻结（服务端 409 拦截）；schema 快照随记录保存，模板升级不影响历史记录 |
| PDF 解析 | 引擎只吃文本（可单测），PdfPig 按视觉行重建；解析是**建议**，写入必须人工确认，全程审计 |
| 错误语义 | 领域异常映射为 400/403/404/409/413/502/503，生产环境不回传内部细节 |
| 持久化 | SQLite + Data Protection 密钥环 + 日志全部落在 Docker 卷 / 数据目录上 |

## 命名规则（历史文档）

同一天同一目录下依次生成（ELN 记录号规则：`项目-检项-yyyyMMdd-序号`）：

```
20260912.docx   20260912A.docx   20260912B.docx   ...   20260912Z.docx
```

字母固定大写，扩展名完全跟随模板；`Z` 仍冲突则返回「当日编号已满」。
