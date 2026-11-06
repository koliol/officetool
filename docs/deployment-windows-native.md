# Windows 原生部署（无 Docker）

与 `docs/deployment-synology.md`（Docker/群晖）并列的第二种部署方式：
直接跑在 Windows 服务器上，以 **Windows 服务**常驻，数据放在本地目录或共享盘。

## 什么时候选这种

| 场景 | 推荐 |
|---|---|
| 服务器是 Windows Server，已有一个共享盘（SMB） | **Windows 原生** |
| 数据必须落在 NAS 共享盘、由群晖管理权限 | Docker（群晖） |
| 只想在内网一台 Windows 机器上快速跑起来给一个科室用 | Windows 原生 |

两种方式共用同一份代码：Windows 服务身份的启动逻辑在 `Program.cs`
（`UseWindowsService()`），Linux/容器形态下该行不生效。

## 前置条件

1. **.NET 8 Runtime**（或用 `-SelfContained` 免装，体积 +80MB）：
   <https://dotnet.microsoft.com/download/dotnet/8.0> → ASP.NET Core Runtime 8.0.x (Windows Hosting Bundle)
2. 构建机需要 Node.js 18+ 与 .NET SDK 8（**只在构建时需要**；用发布产物部署的目标机不需要 Node）
3. 管理员 PowerShell（注册服务、开防火墙都需要）

## 一键安装

```powershell
# 在仓库根目录，管理员 PowerShell
cd scripts\windows

# 数据放本机：
.\install.ps1

# 数据放共享盘（客户端看到 \\NAS\OfficeDocs\...）：
.\install.ps1 -ClientRoot "\\NAS\OfficeDocs"

# 不想装 .NET Runtime：
.\install.ps1 -SelfContained

# 只看布局不实际安装：
.\install.ps1 -WhatIf
```

默认布局（都可以用参数改）：

```
C:\OfficeTool\app            程序（dotnet publish 产物）
C:\OfficeToolData\
  ├─ Templates\              模板（历史文件；ELN 模板在数据库里）
  ├─ Data\                   历史文档
  ├─ Attachments\            附件
  ├─ ExtractionRules\        提取规则（旧版文件形态；新版规则在数据库里）
  ├─ Records\                ELN 记录的原始报告 PDF（按 项目\检项\记录号 分层）
  ├─ _trash\                 回收站
  ├─ state\officetool.db     SQLite 数据库
  ├─ keys\                   Data Protection 密钥环（必须持久化！丢了 Cookie 全部失效）
  └─ logs\                   按天滚动的日志
```

安装脚本会：发布前后端 → 建目录 → 生成 `appsettings.Production.json`
（覆盖容器版配置）→ 注册 Windows 服务（开机自启、崩溃自动重启：10s/30s/60s）→
开防火墙端口 → 启动 → 健康检查。

## 首次登录

初始管理员密码只在**服务日志**里出现一次：

```powershell
Select-String -Path "C:\OfficeToolData\logs\officetool-*.log" -Pattern "初始密码"
```

登录后第一件事是改密码，然后到「提取规则」页把内置规则（不溶性微粒）
**设置生效范围** —— 没设置范围的规则对普通用户不可见。

## 升级

```powershell
.\upgrade.ps1        # 自动读取安装参数：停服务 → 备份数据库 → 重新发布 → 启动
```

数据库每次升级前自动备份到 `state\officetool.<时间戳>.bak`（保留最近 5 份）。

## 卸载

```powershell
.\uninstall.ps1               # 删服务/防火墙/程序目录，保留数据
.\uninstall.ps1 -RemoveData   # 连数据一起删（不可恢复！）
```

## 与 Docker 部署的配置差异

| 项 | Docker（群晖） | Windows 原生 |
|---|---|---|
| 配置文件 | 环境变量（docker-compose.yml） | `app\appsettings.Production.json`（脚本生成） |
| 数据路径 | `/data/...`（容器内） | `C:\OfficeToolData\...` |
| 客户端路径 | `\\NAS\OfficeDocs\...` | `-ClientRoot` 指定，或与本机路径相同 |
| 进程管理 | 容器 restart + entrypoint 降权 | Windows 服务（SCM 崩溃自动重启） |
| 日志 | stdout + 卷 | `DataDir\logs\officetool-*.log` |

## 常见问题

- **服务启动后立即停止**：先看 `DataDir\logs`。最常见是存储根不可写
  （共享盘权限、盘符不存在）—— 应用启动时会主动校验并写明缺哪个目录。
- **改了共享盘路径要重新生成配置**：配置由脚本生成，改参数后重跑
  `install.ps1`（幂等，数据不受影响），或直接编辑 `app\appsettings.Production.json`
  后重启服务。
- **端口被占用**：换 `-Port` 重装，或编辑 `appsettings` 后用
  `sc.exe config OfficeTool binPath= ...` 修改启动参数。
- **升级后配置被还原**：升级沿用了 `install-settings.json` 里保存的参数；
  手工改过配置文件的，升级后需要重新改一次（或把定制项挪进
  `appsettings.Overrides.json`，ASP.NET Core 会自动叠加）。
