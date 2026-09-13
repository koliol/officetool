# Office 文档管理工具

内网环境下统一管理 Office 模板与基于模板生成的文档副本。
网页为主界面，模板库与数据目录两套平行结构，部署在**群晖 NAS（Docker）**上。

依据《Office 文档管理工具 完整设计文档 V1.0》实现。

> 状态与需求覆盖 → **[docs/STATUS.md](docs/STATUS.md)**
> 群晖部署与验证 → **[docs/deployment-synology.md](docs/deployment-synology.md)**

---

## 快速开始

### 方式一：Docker（群晖 NAS / 任意 Linux）

```bash
cp .env.example .env      # 按实际环境修改 NAS_HOST / SHARE_PATH 等
docker compose up -d --build
# 浏览器访问 http://<NAS_IP>:8080
```

详见 [docs/deployment-synology.md](docs/deployment-synology.md)。

### 方式二：本地开发（无需 Docker）

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
dotnet test                       # 73 项单元测试
bash scripts/api-smoke.sh         # 39 项接口冒烟（需后端在 5080 运行）
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
  Services/              PathGuard 路径安全、NameValidator 校验、DocumentNamingService 命名并发、
                         PathLayout 目录布局、ClientPathBuilder 客户端路径
  Models/ Options/       实体与配置（StorageOptions / UploadOptions / DesktopPluginOptions）
src/OfficeTool.Api/      ASP.NET Core 8 Web API
  Controllers/           projects / templates / documents / system
  Services/              编目、操作日志、凭据加解密
  Data/                  EF Core 上下文
  Middleware/            统一异常 → 语义化状态码
tests/                   单元测试
web/                     Vue 3 + Element Plus 前端
scripts/api-smoke.sh     接口冒烟测试
Dockerfile               多阶段构建（前端 → 后端 → 运行时）
docker-compose.yml       群晖 / Linux 部署编排
docs/                    状态记录与部署文档
```

## 核心设计要点

| 主题 | 做法 |
|---|---|
| 路径安全 | 所有路径基于配置根拼接，分段校验（禁分隔符/`..`），规范化后再验前缀 |
| 命名去重 | 进程内按目录 `SemaphoreSlim` 串行 + 跨进程 `FileMode.CreateNew` 原子创建兜底 |
| 存储解耦 | `IFileStore` 抽象；容器/NAS 用 LocalFileStore（bind mount），Windows UNC 用 UncFileStore |
| 双视角路径 | `ClientPathBuilder` 统一生成客户端可访问路径，避免 Linux 下混合分隔符 |
| 一致性 | 文件系统为准、数据库为索引，提供 `/api/system/sync` 修正 |
| 错误语义 | 领域异常映射为 400/403/404/409/502/503，生产环境不回传内部细节 |
| 持久化 | SQLite + Data Protection 密钥环 + 日志全部落在 Docker 卷上，容器重建不丢 |

## 命名规则（设计文档 §4.4）

同一天同一目录下依次生成：

```
20260912.docx   20260912A.docx   20260912B.docx   ...   20260912Z.docx
```

字母固定大写，扩展名完全跟随模板；`Z` 仍冲突则返回「当日编号已满」。
