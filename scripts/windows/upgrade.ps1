# =====================================================================
# OfficeTool Windows 升级 —— 停服务 → 备份数据库 → 重新发布 → 沿用安装参数重启
#
# 用法（管理员 PowerShell）：
#   .\upgrade.ps1                  # 读取 install-settings.json 里保存的安装参数
#   .\upgrade.ps1 -SkipWebBuild    # 直接用 wwwroot 现有产物，跳过 npm 构建
#
# 只替换 <安装目录>\app 下的程序文件；
# 数据（共享盘、SQLite、密钥环、日志）一律不动。
# =====================================================================
param(
    [string]$InstallDir  = "",
    [string]$DataDir     = "",
    [int]   $Port        = 0,
    [string]$ServiceName = "",
    [switch]$SkipWebBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

# ── 0. 恢复安装参数 ──────────────────────────────────────────────────
# 安装时 install.ps1 把参数存到了数据目录；没有就用默认值（并提示）
$defaultDataDir = "C:\OfficeToolData"
$settingsPath = Join-Path $defaultDataDir "install-settings.json"

if (Test-Path $settingsPath) {
    $saved = Get-Content $settingsPath -Raw | ConvertFrom-Json

    if (-not $InstallDir)  { $InstallDir  = $saved.InstallDir }
    if (-not $DataDir)     { $DataDir     = $saved.DataDir }
    if (-not $Port)        { $Port        = [int]$saved.Port }
    if (-not $ServiceName) { $ServiceName = $saved.ServiceName }
} else {
    Write-Warning "未找到 $settingsPath，使用默认参数（若安装时改过目录/端口/服务名，请手工传入）。"
    if (-not $InstallDir)  { $InstallDir  = "C:\OfficeTool" }
    if (-not $DataDir)     { $DataDir     = $defaultDataDir }
    if (-not $Port)        { $Port        = 8080 }
    if (-not $ServiceName) { $ServiceName = "OfficeTool" }
}

Write-Host "== OfficeTool 升级 ==" -ForegroundColor Cyan
Write-Host "程序：$InstallDir    数据：$DataDir    服务：$ServiceName:$Port"

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    throw "未找到服务 $ServiceName。请先运行 install.ps1。"
}

# ── 1. 停服务 ────────────────────────────────────────────────────────
Write-Host "`n== 1. 停止服务 ==" -ForegroundColor Cyan
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

$deadline = (Get-Date).AddSeconds(30)
while ((Get-Service -Name $ServiceName).Status -ne "Stopped" -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
if ((Get-Service -Name $ServiceName).Status -ne "Stopped") {
    throw "服务未能停止，请手工检查（可能被其他进程占用）。"
}
Write-Host "已停止。"

# ── 2. 备份 SQLite ──────────────────────────────────────────────────
Write-Host "`n== 2. 备份数据库 ==" -ForegroundColor Cyan
$dbPath = Join-Path $DataDir "state\officetool.db"
if (Test-Path $dbPath) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $backup = Join-Path $DataDir "state\officetool.$stamp.bak"
    Copy-Item $dbPath $backup
    Write-Host "已备份：$backup"

    # 只保留最近 5 份，避免备份目录无限膨胀
    Get-ChildItem (Join-Path $DataDir "state") -Filter "officetool.*.bak" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip 5 |
        Remove-Item -Force -ErrorAction SilentlyContinue
} else {
    Write-Warning "未找到数据库 $dbPath（可能从未启动过）。"
}

# ── 3. 调用安装脚本 ─────────────────────────────────────────────────
# install.ps1 是幂等的：重新发布、重建目录、重写配置、重注册服务并启动。
# 复用它而不是复制逻辑，保证两条路径永远不会漂移。
Write-Host "`n== 3. 重新发布并启动 ==" -ForegroundColor Cyan
$installArgs = @{
    InstallDir  = $InstallDir
    DataDir     = $DataDir
    Port        = $Port
    ServiceName = $ServiceName
}
if ($SkipWebBuild) { $installArgs.SkipWebBuild = $true }
if ($saved -and $saved.SelfContained) { $installArgs.SelfContained = $true }

& (Join-Path $PSScriptRoot "install.ps1") @installArgs

Write-Host "`n== 升级完成 ==" -ForegroundColor Green
Write-Host "如需回滚：停止服务后，用 $DataDir\state 里最新的 .bak 覆盖 officetool.db，再启动服务。"
