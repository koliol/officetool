# 群晖 NAS (Docker) 部署指南

> 本文档对应「Docker 部署在群晖 NAS」这一形态。
> 原先的 Windows Server + IIS + SMB 方案见 git 历史与 `docs/deployment-windows.md`（已不推荐）。
>
> **本机验证状态**：镜像已在本机（Ubuntu 24.04 + Docker 29.1.3 x86_64）真实构建并启动——
> 39 项接口冒烟、客户端视角路径、bind mount 落盘、容器重启与重建后的数据持久化，
> 以及浏览器页面实测**全部通过**（控制台 0 错误）。
> 群晖特有的部分（Container Manager 界面、共享文件夹 ACL）需在设备上按本文逐步确认。

---

## 一、先理解两个「视角」（本文最重要的一节）

部署在 NAS 上，最大的坑是路径。同一份文件有两个不同的地址：

| 视角 | 谁在用 | 形如 | 由哪个配置项描述 |
|---|---|---|---|
| **服务端（容器内）** | 应用程序真正读写文件 | `/data/Data/QLS2409/SEC/20260912.docx` | `Storage:TemplatesRoot` / `Storage:DataRoot` |
| **客户端（Windows）** | 用户复制到资源管理器、桌面插件打开 | `\\NAS\OfficeDocs\Data\QLS2409\SEC\20260912.docx` | `Storage:ClientTemplatesRoot` / `Storage:ClientDataRoot` |

两者指向 NAS 共享文件夹里的**同一份数据**，只是一个从容器里看、一个从 Windows 里看。

```
群晖 NAS
└── /volume1/OfficeDocs/          ← 共享文件夹「OfficeDocs」（客户端看到 \\NAS\OfficeDocs）
    ├── Templates/                ← 模板库
    │   └── QLS2409/SEC/模板.docx
    ├── Data/                     ← 生成的文档副本
    │   └── QLS2409/SEC/20260912.docx
    ├── Attachments/              ← 附件（与模板分开存放，供后续提取用）
    │   └── QLS2409/SEC/检验记录.pdf
    ├── ExtractionRules/          ← 提取规则（同样是文件，按项目/检项存放）
    │   └── QLS2409/SEC/标准字段.json
    └── plugin/OfficeToolPlugin.exe
```

> `Attachments` 与 `ExtractionRules` 目录**不用手工创建**：建项目/检项时应用会一并创建，
> 并列入启动自检（日志里会看到「附件根 校验通过」「规则根 校验通过」）。
> 它们和 `Templates`、`Data` 一样在 `/data` 之下，因此**不需要额外的 Docker 挂载**。
>
> 这两类目录都是「按项目/检项两级」，与模板/文档完全对称。
> 项目/检项删除时，四个目录都会做「非空」判定并一并清理。

容器里通过 volume 映射看到它：

```
/volume1/OfficeDocs  ──bind mount──▶  /data      (容器内)
                                       ├── Templates
                                       └── Data
```

> ⚠️ **注意**：本方案**不再使用 SMB/UNC 凭据**。容器直接读写挂载进来的本地路径，
> 权限由 Docker 卷映射 + 群晖共享文件夹 ACL 决定。原先的 `WNetAddConnection2` 那套
> 只有在 Windows 主机 + UNC 路径时才需要（`Storage:Mode=Unc`）。

---

## 二、前置准备

### 2.1 环境要求
- 群晖 DSM **7.0 及以上**（7.2+ 使用「Container Manager」，7.0/7.1 使用「Docker」套件）
- 至少 2GB 可用内存（镜像约 300MB，运行占用约 200-300MB）
- CPU 架构：x86_64（Plus/Play 系列）或 ARM64（部分型号）——基础镜像均为多架构，自动匹配

### 2.2 创建共享文件夹
- [ ] DSM → 控制面板 → 共享文件夹 → 新增 → 名称填 **`OfficeDocs`**
- [ ] 在其下创建子目录：`Templates`、`Data`、`plugin`（`Attachments`、`ExtractionRules` 由应用自动创建）
- [ ] 记下它的真实路径（通常是 `/volume1/OfficeDocs`），SSH 执行确认：
      ```bash
      ls -d /volume1/OfficeDocs
      ```

### 2.3 查一下你自己的账号 uid:gid:groups（`.env` 里要填）

容器需要以「对共享文件夹有权限的身份」运行，所以先把你自己账号这三个值记下来：

```bash
id 你的用户名
# 例：uid=1026(koliol) gid=100(users) groups=100(users),101(administrators)
#     → .env 里填 PUID=1026  PGID=100  SUP_GIDS=100,101
```

> 这里要的是**你的账号**的身份，不是共享文件夹的属主。
> 另外建议同时看一眼 ACL 到底授权给谁：
> ```bash
> sudo synoacltool -get /volume1/OfficeDocs
> # 例：[0] group:administrators:allow:rwxpdDaARWc--:fd--
> ```
> 两者对不上就会报「模板根 不可用」，详见「六、常见问题」第 1 条。

### 2.4 开启 SSH
- [ ] 控制面板 → 终端机和 SNMP → 启用 SSH 功能

---

## 三、部署方式 A：SSH + docker compose（推荐）

### 3.1 上传代码
把整个项目目录传到 NAS，例如 `/volume1/docker/officetool`：

```bash
# 在你的开发机上（把 <user> <nas_ip> 换成实际值）
rsync -av --exclude node_modules --exclude bin --exclude obj \
      ./OfficeTool/ <user>@<nas_ip>:/volume1/docker/officetool/
```

> 也可以只传源码后用群晖自带的 Git 功能克隆，效果相同。

### 3.2 配置 .env
```bash
cd /volume1/docker/officetool
cp .env.example .env
vi .env
```

按实际情况修改：

```ini
HOST_PORT=8080
TZ=Asia/Shanghai

NAS_HOST=NAS              # 你的 NAS 主机名或 IP（客户端会用它访问）
SHARE_NAME=OfficeDocs

SHARE_PATH=/volume1/OfficeDocs

# 运行用户：填 `id 你的用户名` 的结果（见 2.3 节）
# 群晖上不填会报「模板根 不可用」—— DSM 共享文件夹的 ACL 常常只授给某个组，
# 而你的权限可能是通过**附加组**拿到的，所以 SUP_GIDS 必须一起填。
PUID=1026
PGID=100
SUP_GIDS=100,101

# 日志不需要配置：已改用 Docker 命名卷 officetool-logs（自动创建）。
# 查看日志： sudo docker compose logs -f officetool
```

> 怎么确认该填什么组？`sudo synoacltool -get /volume1/OfficeDocs` 会显示
> ACL 授给了哪个组（例如 `group:administrators:allow:...`），
> 把包含该组的、你自己 `id` 输出里的 gid 全部填进 `SUP_GIDS` 即可。

> 若使用 root 运行（`PUID=0` `PGID=0`）可绕过 ACL，但生成的文件属主是 root，
> Windows 用户可能改不动 —— 非必要不用。
>
> **注意 `SHARE_PATH` 指向的目录必须已经存在**（在 DSM 里建好共享文件夹即可）。
> bind mount 不会自动创建目录，不存在时 compose 会报
> `Bind mount failed: '...' does not exist` 并拒绝创建容器。
> 其余三个挂载点（state / keys / logs）都是命名卷，Docker 会自动创建，无需手工建目录。

### 3.3 构建并启动
```bash
sudo docker compose up -d --build
```

- 首次构建需要拉取基础镜像（约 800MB）并编译，视 NAS 性能耗时 **5-15 分钟**
- **国内网络相关**（基本一定会遇到，建议先配好）：
  1. **Docker Hub 不可达** → 配镜像加速
     ```bash
     sudo vi /etc/docker/daemon.json
     # { "registry-mirrors": ["https://docker.m.daocloud.io"] }
     sudo synosystemctl restart docker    # 或 sudo systemctl restart docker
     ```
  2. **`mcr.microsoft.com` 拉取极慢**（实测约 100KB/s，SDK 层要一个多小时）
     → 用代理源拉取后打上官方 tag，Dockerfile 无需修改：
     ```bash
     sudo docker pull mcr.m.daocloud.io/dotnet/sdk:8.0
     sudo docker pull mcr.m.daocloud.io/dotnet/aspnet:8.0
     sudo docker tag  mcr.m.daocloud.io/dotnet/sdk:8.0    mcr.microsoft.com/dotnet/sdk:8.0
     sudo docker tag  mcr.m.daocloud.io/dotnet/aspnet:8.0 mcr.microsoft.com/dotnet/aspnet:8.0
     ```
  3. **`npm ci` 卡死或报 `Exit handler never called!`** —— 若已确认 NAS 能访问 npm 源
     （`wget` 探测 OK）却仍然失败，多半是**大流量传输被卡**（MTU 分片 / 内存不足），
     npm 会在长时间重试后崩掉。
     **首选方案：直接用部署包里附带的预构建前端，彻底跳过 npm**（见 3.4 节）。
     次选：换镜像源 + 缩短超时，避免长时间空转：
     ```ini
     NPM_REGISTRY=https://registry.npmmirror.com
     ```
     ```bash
     sudo docker compose build --build-arg NPM_REGISTRY=https://registry.npmmirror.com
     ```
  4. **一步到位**：写进 `.env` 后直接
     ```bash
     sudo docker compose up -d --build
     ```
     （compose 已配置从环境读取该构建参数）

  > 关于 apt：**构建全程不使用 apt**。运行时镜像 `mcr.microsoft.com/dotnet/aspnet:8.0`
  > 自带 tzdata 与 bash，HEALTHCHECK 也已改为用 bash 直连端口，不依赖 curl。
  > 因此 `deb.debian.org` 连不上（国内常见）**不影响构建**。
- 若 NAS 架构与构建机不同，需指定平台：
  ```bash
  # 或直接在 NAS 上构建（自动匹配本机架构，最省事）
  sudo docker build --platform linux/arm64 -t officetool:1.0.0 .
  ```

### 3.4 受限网络：完全跳过 npm（推荐给国内 NAS）

**官方部署包已内含 `prebuilt/webdist/`（构建好的前端产物）。**
Dockerfile 检测到它存在就会跳过 `npm ci` 与 `vite build`，
因此整个镜像构建**不访问任何 npm 源**。

判断方法：构建日志里出现这两行即生效

```
==> 检测到 prebuilt/webdist，跳过 npm ci
==> 使用预构建前端产物，跳过 vite build
```

适用场景：NAS 访问 npm 源不稳定 / 被防火墙策略拦截 / 大流量传输被卡。
（实测某台群晖：`wget` 探测 npm 源返回 OK，但 `npm ci` 跑 744 秒后仍
`Exit handler never called!` —— 小请求通、大流量传不动，此时最稳的做法就是跳过 npm。）

若无预构建产物而 npm 又不可用，可在**任意一台能联网的机器**上：

```bash
cd web && npm ci && npm run build -- --outDir ../prebuilt/webdist --emptyOutDir
```

把生成的 `prebuilt/webdist/` 目录传到 NAS 的项目目录下，再构建即可。

> 注意：即使跳过 npm，`node:22-alpine` 仍作为该构建阶段的基底镜像被引用，
> 需保证它已在 NAS 本地（`docker images | grep node`）。正常构建路径本来也需要它。

### 3.5 看日志
```bash
sudo docker compose logs -f officetool
```

启动成功时应看到：

```
模板根 校验通过（可读写）：/data/Templates
数据根 校验通过（可读写）：/data/Data
OfficeTool 启动完成。存储模式=Local，存储类型=LocalFileStore
  服务端模板根=/data/Templates
  服务端数据根=/data/Data
  客户端模板根=\\NAS\OfficeDocs\Templates
  客户端数据根=\\NAS\OfficeDocs\Data
Now listening on: http://[::]:8080
```

**若看到 `模板根 不可用`** → 跳到第六节第 1 条。

---

## 四、部署方式 B：Container Manager 图形界面（DSM 7.2+）

1. **套件中心**安装 **Container Manager**
2. **项目** → 新增 → 项目名称 `officetool`
3. 路径选择你上传代码的目录（如 `/volume1/docker/officetool`）
4. 来源选「**创建 docker-compose.yml**」，内容粘贴项目里的 `docker-compose.yml`
5. 若不想用 `.env` 文件，把变量直接写进 compose（或在「环境」页签逐条添加）：
   | 变量 | 值 |
   |---|---|
   | `TZ` | `Asia/Shanghai` |
   | `NAS_HOST` | 你的 NAS 名称或 IP |
   | `SHARE_NAME` | `OfficeDocs` |
   | `SHARE_PATH` | `/volume1/OfficeDocs` |
   | `PUID` | 你的账号 uid（`id 你的用户名`，如 `1026`） |
   | `PGID` | 你的账号 gid（如 `100`） |
   | `SUP_GIDS` | 你的账号附加组，如 `100,101`（照抄 `id` 的 groups） |
6. 下一步 → 完成，等待构建
7. **容器** 页面确认 `officetool` 状态为「运行中」，点「详情 → 日志」看启动日志

> Container Manager 的「项目」会读取项目目录下的 `.env` 文件，所以把 `.env.example`
> 复制成 `.env` 改好再建项目即可。另外：部署包内含 `prebuilt/webdist`，
> 构建会**跳过 npm**，因此不受 NAS 访问 npm 源的状况影响。

> DSM 7.0/7.1 的「Docker」套件没有「项目」功能，只能：
> SSH 执行 `docker-compose up -d`（老版本套件自带 docker-compose v1），
> 或在图形界面手工创建容器并逐项填端口/卷/环境变量（麻烦且易漏，不推荐）。

---

## 五、上线后验证清单

> 每项都要看到实际结果再打勾。

### 5.1 容器与接口
- [ ] `sudo docker compose ps` → 状态 `Up (healthy)`
- [ ] `curl http://127.0.0.1:8080/api/health` → 200，`storeType` 为 `LocalFileStore`
- [ ] `curl http://127.0.0.1:8080/api/system/config` → `mode` 为 `Local`，
      `clientDataRoot` 为 `\\<你的NAS>\OfficeDocs\Data`
- [ ] 浏览器打开 `http://<NAS_IP>:8080/`，控制台**无红色报错**

### 5.2 路径映射（最容易错的地方）
- [ ] 页面新建项目 `SMOKE01`
- [ ] 到 NAS 上看 `/volume1/OfficeDocs/Templates/SMOKE01` 与 `/volume1/OfficeDocs/Data/SMOKE01` **都出现了**
- [ ] 新建检项 `T1` → 对应子目录出现
- [ ] 上传一个真实 `.docx` → NAS 上对应目录出现该文件
- [ ] **界面上点文件 → 弹窗里的路径应为 `\\<NAS>\OfficeDocs\...` 形式**（不是 `/data/...`）
      - 若显示 `/data/...` → `Storage:ClientTemplatesRoot` / `ClientDataRoot` 没配好

### 5.3 从 Windows 客户端实测（关键）
- [ ] 在 Windows 资源管理器地址栏粘贴界面复制出的路径 → **能打开该文件**
      （这一步验证了「客户端视角」配置正确、共享文件夹对用户可访问）
- [ ] 用 Word/Excel 打开、编辑、保存 → 回到网页刷新，能看到更新后的修改时间

### 5.4 核心业务
- [ ] 连续新建文档 3 次 → 依次得到 `yyyyMMdd.docx`、`yyyyMMddA.docx`、`yyyyMMddB.docx`
- [ ] 生成的文档能正常打开（尤其 `.xlsm`/`.docm` 的宏是否保留）
- [ ] 上传 `.exe` → 被拒并提示中文原因
- [ ] 文件名/项目名传 `../` → 400，且 NAS 上无越界文件

### 5.5 时区（易忽略）
- [ ] 在 NAS 本地时间**凌晨 0-8 点之间**新建一个文档（或临时把 `TZ` 改错再改回来对比）
- [ ] 确认文件名日期 = **本地日期**，不是 UTC 日期
- [ ] `sudo docker exec officetool date` → 显示的应是本地时间

### 5.6 持久化（决定会不会丢数据）
- [ ] 记下当前项目/文档数量
- [ ] `sudo docker compose down && sudo docker compose up -d`
- [ ] 刷新页面 → **数据仍在**（验证 SQLite 在 `/app/state` 卷上）
- [ ] 若之前配过 `Storage:EncryptedPassword`，重启后确认仍能正常读写（验证密钥环在 `/app/keys` 卷上）

### 5.7 备份
- [ ] 文件部分：`OfficeDocs` 共享文件夹纳入 Hyper Backup / Snapshot Replication
- [ ] 数据库：备份卷 `officetool-state`（或 `docker exec` 里 `sqlite3 /app/state/officetool.db ".backup /app/state/backup.db"`）
- [ ] 密钥环：备份卷 `officetool-keys`（丢了会导致已加密凭据解不开）
- [ ] 日志卷 `officetool-logs`：可选。丢了只是没有历史日志文件，不影响业务
- [ ] **恢复演练**：恢复后执行一次 `POST /api/system/sync`，确认索引与文件一致

> 三个命名卷的物理位置在 NAS 的 Docker 数据目录下（通常
> `/volume1/@docker/volumes/`）。要迁机或手工备份可以用：
> ```bash
> sudo docker run --rm -v officetool-state:/src -v "$PWD":/dst alpine \
>     tar czf /dst/officetool-state.tar.gz -C /src .
> ```

---

## 六、常见问题

### 1. 启动日志报「模板根 不可用」（群晖上最常见）

先看日志里那行 `当前运行用户 uid=xxxx`，它会直接告诉你容器是以哪个 uid 跑的。

**原因**：DSM 共享文件夹显示 `drwxrwxrwx`，但真正生效的是 **Synology ACL**
（`ls -l` 末尾那个 `+`）。容器内置的 uid 1654 在 ACL 里没有任何身份，于是被拒。

**解决**：让容器以「ACL 里被授权的那个身份」运行。分两步。

**第一步：看 ACL 到底授给了谁**

```bash
sudo synoacltool -get /volume1/OfficeDocs
```

典型输出：

```
ACL version: 1
Owner: [root(user)]
---------------------
     [0] group:administrators:allow:rwxpdDaARWc--:fd--
```

这行的意思是：**只有 `administrators` 组有权限**，没有 `users` 组、也没有 everyone。
（`ls -l` 显示的 `drwxrwxrwx` 是装饰性的，真正的判定走这套 ACL。）

**第二步：把自己账号的身份照抄进 `.env`**

```bash
id 你的用户名
# uid=1026(koliol) gid=100(users) groups=100(users),101(administrators)
```

```ini
PUID=1026
PGID=100
SUP_GIDS=100,101        # ← 关键：照抄 groups 列表
```

```bash
sudo docker compose up -d
```

> ⚠️ **`SUP_GIDS` 不能省。** 上面的例子中，`koliol` 对共享文件夹的权限是
> 通过**附加组** `administrators(101)` 拿到的。只设 `PGID=100` 会让进程只剩
> `users` 组，于是「明明这个账号能访问，容器却报 Permission denied」。
> 这是本项目早期版本的一个真实缺陷（入口脚本曾用 `--clear-groups` 清空附加组），已修复。

验证：

```bash
sudo docker compose logs officetool | grep -E "运行用户|校验通过"
# ==> 运行用户 uid=1026 gid=100 附加组=[100,101]；内部状态目录归属已同步
# 模板根 校验通过（可读写）：/data/Templates
```

**如果填了仍不可写**，退到 root（能绕过 ACL，但生成的文件属主是 root，
Windows 用户可能改不动）：

```ini
PUID=0
PGID=0
```

**其他可选做法**（不改运行用户）：

```bash
# 给共享文件夹放宽权限
sudo chmod 775 /volume1/OfficeDocs
# 或在 DSM 控制面板 → 共享文件夹 → 编辑 → 权限，
#   给对应用户/用户组「读写」，并勾选「应用到子文件夹」
```

> 注：`docker compose exec officetool id` 会显示 `uid=0(root)` —— 这是正常的，
> 因为镜像以 root 进入（为了做上面那次 chown），**应用进程本身**才运行在 PUID/PGID。
> 想确认应用进程的身份，看启动日志里那行 `==> 运行用户 uid=...`。

### 2. 弹窗里的路径不对（显示 `/data/...` 或找不到文件）
`Storage:Client*Root` 没配置或写错。检查：

```bash
sudo docker exec officetool printenv | grep -i storage
curl -s http://127.0.0.1:8080/api/system/config
```
`.env` 里 `NAS_HOST` / `SHARE_NAME` 要填**客户端访问 NAS 时用的名字**，
比如 Windows 上 `\\NAS\OfficeDocs` 能打开，那 `NAS_HOST` 就填 `NAS`。

### 3. 端口被占用
群晖自身占用 5000/5001/80/443。改 `.env` 里的 `HOST_PORT`（如 8081），
`docker compose up -d` 重建容器即可。

### 4. 上传大文件失败
默认上限 100MB，配置项 `Upload:MaxSizeMB`。
注意 Kestrel 的请求体上限由程序按该值自动放大，无需额外配置。
若通过群晖反向代理访问，还需在 控制面板 → 登录门户 → 反向代理 → 高级 里放宽「最大请求体大小」。

### 5. 文档日期差一天
容器时区问题。确认 `.env` 里 `TZ=Asia/Shanghai`，且重建后生效：
```bash
sudo docker exec officetool date
```

### 6. `docker compose` 命令不存在（DSM 7.0/7.1）
老套件用 v1 语法：`sudo docker-compose up -d`（中间是短横线）。

### 7. 升级到新版本
```bash
cd /volume1/docker/officetool
git pull                      # 或重新上传代码
sudo docker compose up -d --build
```
数据在卷上，不会丢。**但注意**：数据库当前使用 `EnsureCreated()`，
若新版本改了表结构不会自动迁移，升级前请先备份 `officetool-state` 卷。

### 8. `Bind mount failed: '...' does not exist`
compose 启动时报某个宿主机路径不存在。原因是**绑定的目录必须先存在**，
Docker 不会为 bind mount 自动建目录（命名卷才会）。

- 报 `/volume1/docker/officetool/logs` → 旧版 compose 的问题，新版已改用命名卷
  `officetool-logs`，重新解压部署包即可。
- 报 `SHARE_PATH` 对应的路径 → 该共享文件夹还没在 DSM 里建好，或路径写错了。
  确认：`ls -d /volume1/OfficeDocs`

### 9. 「不安全」告警（浏览器提示协议不受信任）
本方案只有 HTTP。内网使用可接受；若要 HTTPS，
在 控制面板 → 登录门户 → 反向代理 里加一条 HTTPS 反代指向 `http://127.0.0.1:8080`，
再用群晖自带证书或 Let's Encrypt。

---

## 七、安全提醒

1. **本应用当前没有登录鉴权**（设计文档 §11 把身份验证列为后续项）。
   任何能访问 `http://<NAS_IP>:8080` 的人都能增删文件。
   **务必**限制为内网可达，不要做端口转发暴露到公网。
2. 端口绑定可进一步收紧——把 compose 里改成 `"127.0.0.1:8080:8080"` 只允许本机，
   再通过群晖反向代理对外提供（可叠加群晖的访问控制）。
3. 定期备份 `officetool-state` 与 `officetool-keys` 两个卷。
4. 关注镜像基础版本更新（`mcr.microsoft.com/dotnet/aspnet:8.0` 的安全补丁）。

---

## 八、验证记录（部署时回填）

| 日期 | NAS 型号 / DSM 版本 | 执行人 | 结果 | 备注 |
|---|---|---|---|---|
| | | | | |
