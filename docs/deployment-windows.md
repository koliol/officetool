# Windows Server 2016 部署与验证清单

> ## ⚠️ 本文档已归档（不再对应当前部署形态）
>
> 项目现在的目标形态是 **群晖 NAS + Docker**，见 **[deployment-synology.md](deployment-synology.md)**。
> 本文保留仅供以下用途：
> - 需要退回 **Windows + UNC 共享** 部署时作参考（`Storage:Mode=Unc`，仍受支持但未验证）
> - 其中「桌面插件」相关步骤对任意部署形态都适用
>
> **注意配置项已重命名**：本文中的 `Smb:*` 全部已改为 `Storage:*`，
> 且 `Smb:UseLocalFileStore`（布尔）已改为 `Storage:Mode`（`Local` / `Unc`）。
> 另外新增了 `Storage:ClientTemplatesRoot` / `Storage:ClientDataRoot` 用于客户端视角路径。
>
> ---
>
> 本机开发环境为 Linux，以下步骤**均未在真实环境执行过**。
> 请在目标机上逐项执行并勾选，把实测结果回填到本节末尾的「验证记录」。
> 对应设计文档：§7.2 凭据模拟、§10 部署、§11 安全。

---

## 一、前置准备

### 1.1 服务器环境
- [ ] 安装 **.NET 8 Hosting Bundle**（不是单纯 Runtime，IIS 托管必需）
      https://dotnet.microsoft.com/download/dotnet/8.0 → "ASP.NET Core Runtime 8 → Hosting Bundle"
- [ ] 启用 IIS：`控制面板 → 程序 → 启用或关闭 Windows 功能 → Internet Information Services`
      至少勾选：Web 管理工具、IIS 管理控制台、万维网服务 → 应用程序开发功能 → **ASP.NET 4.6+**（用于反向代理配置界面）
- [ ] 安装完成后 `iisreset`，确认 `dotnet --info` 可跑

### 1.2 SQLite 或 SQL Server Express
- [ ] SQLite：无需安装，`Microsoft.Data.Sqlite` 自带原生库（默认方案）
- [ ] 若改用 SQL Server Express：安装后修改 `appsettings.Production.json` 的 `ConnectionStrings:Default`，
      并把 `Program.cs` 里的 `options.UseSqlite(...)` 换成 `options.UseSqlServer(...)` + 补 `Microsoft.EntityFrameworkCore.SqlServer` 包

### 1.3 SMB 共享准备（设计文档 §10.2）
- [ ] 在文件服务器创建共享目录：`\\server\share\OfficeDocs\`
- [ ] 在其下创建 `Templates\` 与 `Data\` 两个子目录
- [ ] 创建专用域账号，例如 `YOURDOMAIN\svc_officetool`
- [ ] 授予该账号对 `OfficeDocs` 的**读写**权限（最小权限：仅此目录）
- [ ] 在 Web 服务器上用该账号手动访问 `\\server\share\OfficeDocs\`，确认网络与权限连通

---

## 二、生成加密密码

凭据以密文形式写入配置，明文不落盘（设计文档 §7.2）。

- [ ] 先在**临时**环境启动一次应用（可用默认配置），调用：

```powershell
curl -X POST http://localhost:5080/api/system/protect `
     -H "Content-Type: application/json" `
     -d "{\"plainText\":\"你的SMB账号密码\"}"
# 返回：{"encrypted":"CfDJ8..."}
```

- [ ] 把 `encrypted` 的值填到 `appsettings.Production.json` 的 `Smb:EncryptedPassword`

> ⚠️ **密钥环必须在同一位置且持久化**。Data Protection 默认把密钥放在
> `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys` 或注册表。
> 在 IIS 下，应用程序池标识（ApplicationPoolIdentity）的密钥环**不随发布目录走**，
> 换机器 / 换应用池标识 / 清理用户配置后**密文将无法解密**。
> 必须显式指定持久化位置，例如：

```csharp
// Program.cs 中替换 AddDataProtection() 那一行
builder.Services.AddDataProtection()
    .SetApplicationName("OfficeTool")
    .PersistKeysToFileSystem(new DirectoryInfo(@"D:\OfficeTool\keys"));  // 该目录须给应用池标识读写权限
```

- [ ] 部署前完成上述调整，并**在目标机上**重新生成一次密文（不要把开发机的密文搬过去）

---

## 三、发布与部署

### 3.1 构建
```powershell
# 1) 前端构建（产物会落到 src\OfficeTool.Api\wwwroot）
cd web
npm ci
npm run build

# 2) 后端发布
cd ..\src\OfficeTool.Api
dotnet publish -c Release -o D:\OfficeTool\app
```
- [ ] 确认 `D:\OfficeTool\app\wwwroot\index.html` 存在
      （**顺序很重要**：先 `npm run build` 再 `dotnet publish`，否则 wwwroot 为空、页面 404）

### 3.2 配置
- [ ] 复制 `appsettings.Production.json` 到 `D:\OfficeTool\app\`，填写：
      - `Smb:Root` / `TemplatesRoot` / `DataRoot` → `\\server\share\OfficeDocs\...`
      - `Smb:Domain` / `UserName` → 专用域账号
      - `Smb:EncryptedPassword` → 第二节生成的密文
      - `Smb:UseLocalFileStore` → **`false`**
      - `DesktopPlugin:DownloadUrl` → 插件安装包的内网地址
- [ ] 确认应用池标识对该目录有读写权限：
      `icacls D:\OfficeTool\app /grant "IIS AppPool\OfficeTool"^(OI^)(CI^)M`
- [ ] 确认 `D:\OfficeTool\app\logs` 目录可写（Serilog 按天滚动）

### 3.3 IIS 站点
- [ ] 新建应用程序池 `OfficeTool`：**.NET CLR 版本 = 无托管代码**，托管管道 = 集成
- [ ] 新建网站，物理路径指向 `D:\OfficeTool\app`，绑定端口
- [ ] （可选）配置 HTTPS 绑定并强制跳转
- [ ] 启用 WebSocket？—— 本项目不需要
- [ ] 确认 `web.config` 已由 `dotnet publish` 生成（含 `AspNetCoreModuleV2`）

### 3.4 Kestrel 方案（替代 IIS）
```powershell
# 注册为 Windows 服务
sc.exe create OfficeTool binPath= "D:\OfficeTool\app\OfficeTool.Api.exe --urls http://0.0.0.0:5080" start= auto
sc.exe start OfficeTool
```
- [ ] 用 IIS 做反向代理指向 `http://127.0.0.1:5080`（或直接暴露端口，按内网策略决定）

---

## 四、上线后验证（必做）

> 以下每一项都要**看到实际输出/截图**再打勾，不要凭"应该没问题"跳过。

### 4.1 基础连通
- [ ] `GET /api/health` → 200，`storeType` 返回 **`UncFileStore`**（不是 LocalFileStore）
      ```powershell
      curl http://localhost/api/health
      # 期望：{"status":"ok",...,"storeAvailable":true,"storeType":"UncFileStore"}
      ```
      ⚠️ 若返回 `LocalFileStore`，说明 `UseLocalFileStore` 仍为 true 或主机非 Windows，**SMB 未生效**
- [ ] `GET /api/system/config` → `fileStoreIsRemoteUnc: true`、`templatesRoot` 为 `\\server\...` 形式
- [ ] 页面 `http://<host>/` 正常打开，浏览器控制台**无红色报错**

### 4.2 SMB 凭据模拟（最高风险项）
- [ ] 新建项目 `SMOKE01` → 观察文件服务器上**同时出现**
      `Templates\SMOKE01\` 与 `Data\SMOKE01\`
- [ ] 若失败，看 `D:\OfficeTool\app\logs\officetool-<日期>.log` 中的 Win32 错误码：
      | 错误码 | 含义 | 处置 |
      |---|---|---|
      | 5 | 拒绝访问 | 账号密码错误，或未授予共享权限 |
      | 53 / 67 | 找不到网络路径/网络名 | UNC 路径写错、共享不存在 |
      | 86 / 1326 | 密码不正确/用户名或密码错误 | 检查 `Domain\UserName` 拼写 |
      | 1219 | 已有冲突凭据 | 该服务器存在其他已连接凭据，需先断开或改用同一账号 |
- [ ] 未加域场景确认：使用 `LOGON32_LOGON_NEW_CREDENTIALS` 语义
      （本实现用 `WNetAddConnection2`，等价于"只对本次连接使用新凭据"，不影响服务器其他进程的身份）

### 4.3 核心业务流
- [ ] 上传模板：在 `SMOKE01` 下新建检项 `T1`，上传一个真实 `.docx`
      → 文件服务器对应目录出现该文件
- [ ] 上传非法扩展名（如 `.exe`）→ 400 且提示中文原因
- [ ] 新建文档 ×3 → 依次得到 `yyyyMMdd.docx`、`yyyyMMddA.docx`、`yyyyMMddB.docx`
      → 文件服务器上三个文件都存在、内容与模板一致
- [ ] **并发验证**（关键）：连点/并发 10 次"新建文档"，确认无重名、无报错
      ```powershell
      1..10 | ForEach-Object -Parallel {
        Invoke-RestMethod -Method Post -Uri http://localhost/api/documents/create `
          -ContentType 'application/json' `
          -Body '{"project":"SMOKE01","check":"T1","templateFileName":"<你的模板名>.docx"}'
      } -ThrottleLimit 10
      # 期望：得到 10 个互不相同的文件名，无 500
      ```
- [ ] 用 **Excel/Word 打开**生成的文档，确认无损（含 `.xlsm`/`.docm` 的宏项目是否保留）
- [ ] 编辑后保存回 SMB，刷新页面仍能列出

### 4.4 安全
- [ ] 项目/检项/文件名传入 `../` → 400，且文件服务器上**没有**越界文件产生
- [ ] 未登录状态（当前无鉴权）从**非内网网段**访问 → 应被防火墙/网络策略阻断
      （建议在 IIS 或网络层加 IP 限制，对应设计文档 §11 可选 IP 白名单）
- [ ] `appsettings.Production.json` 中无明文密码
- [ ] 日志文件不含明文密码
- [ ] 生产环境 500 响应体**不包含**异常堆栈（仅 `{"code":"internal_error",...}`）

### 4.5 界面对齐（设计文档 §8.3）
- [ ] 未装插件时点击文件 → 弹窗显示 UNC 路径 + "复制路径"按钮可复制
- [ ] 协议链接形如 `officetool://open?path=%5C%5Cserver%5C...`（已 URL 编码）
- [ ] 装插件后点击文件 → 唤起本机 Office 打开 SMB 原文件

### 4.6 运维
- [ ] `POST /api/system/sync` 可修正元数据（手工在 SMB 放一个文件 → 同步后被索引）
- [ ] `GET /api/logs` 能看到上传/新建/删除/重命名记录，含客户端 IP
- [ ] 日志按天滚动，保留 31 天
- [ ] 备份：文件服务器备份策略覆盖 `OfficeDocs`；数据库 `officetool.db` 纳入备份
- [ ] 恢复演练：恢复 SMB + 数据库后执行一次 `/api/system/sync`，索引与文件一致

---

## 五、已知限制（部署前需知）

1. **每天每目录最多 27 个文档**：`yyyyMMdd` + `A..Z`。超出返回 409「当日编号已满」。若业务量可能超过，需要扩展命名规则（设计文档 §4.4 提到"实际场景预计不会发生"）。
2. **同名模板不可覆盖**：重复上传返回 409，需先重命名或删除。
3. **无鉴权**：设计文档 §11 把 Windows 身份验证列为"后续"，当前任何人只要能访问站点就有全部权限。务必保证站点仅内网可达。
4. **Data Protection 密钥环**：见第二节警告，不持久化会导致密文失效。
5. **数据库用 `EnsureCreated()`**：升级时若表结构变化不会自动迁移。正式迭代前应改用 EF Core Migrations。
6. **桌面插件尚未实现**：设计文档 §9 的 `OfficeTool.exe` 与 MSI 待开发，当前只能走"复制路径"路径。

---

## 六、验证记录（部署时回填）

| 日期 | 环境 | 执行人 | 结果 | 备注 |
|---|---|---|---|---|
| | | | | |
