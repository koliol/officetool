# =====================================================================
# OfficeTool Windows 原生部署 —— 一键安装
#
# 用法（管理员 PowerShell）：
#   .\install.ps1 -ClientRoot "\\NAS\OfficeDocs"
#
# 默认布局：
#   程序     C:\OfficeTool\app        （dotnet publish 产物）
#   数据     C:\OfficeToolData        （共享盘 / 数据库 / 日志 / 密钥环）
#   服务名   OfficeTool               （Windows 服务，开机自启，端口 8080）
#
# 行为：
#   1. 构建前端（若 web/node_modules 存在）并发布后端
#   2. 创建数据目录树 + 生成 appsettings.Production.json（覆盖容器版配置）
#   3. 注册 Windows 服务（程序本体直接作为服务运行，见 Program.cs 的 UseWindowsService）
#   4. 开防火墙端口、启动服务
#
# 已安装过请用 upgrade.ps1；卸载用 uninstall.ps1。
# =====================================================================
param(
    [string]$InstallDir = "C:\OfficeTool",
    [string]$DataDir    = "C:\OfficeToolData",
    [int]   $Port       = 8080,
    [string]$ServiceName = "OfficeTool",

    # 客户端视角的共享盘根。留空则客户端路径与服务端路径一致（单机部署）。
    [string]$ClientRoot = "",

    # 使用自包含发布（不需要目标机装 .NET Runtime，体积约 +80MB）
    [switch]$SelfContained,

    # 跳过前端构建（使用仓库里已构建好的 wwwroot）
    [switch]$SkipWebBuild,

    # 仅生成配置，不注册/启动服务（用于先看一眼布局）
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot   # scripts/windows -> repo 根

Write-Host "== OfficeTool Windows 安装 ==" -ForegroundColor Cyan
Write-Host "程序目录：$InstallDir"
Write-Host "数据目录：$DataDir"
Write-Host "服务端口：$Port"

# ── 0. 前置检查 ──────────────────────────────────────────────────────
if (-not $SelfContained) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw "未找到 dotnet。请安装 .NET SDK/Runtime 8：https://dotnet.microsoft.com/download/dotnet/8.0，或改用 -SelfContained。"
    }

    $version = (& dotnet --version) 2>$null
    if (-not $version.StartsWith("8.")) {
        Write-Warning "检测到 dotnet $version，建议使用 8.x。"
    }
}

# ── 1. 构建 ──────────────────────────────────────────────────────────
$webDir = Join-Path $repo "web"
if (-not $SkipWebBuild -and (Test-Path (Join-Path $webDir "node_modules"))) {
    Write-Host "`n== 1.1 构建前端 ==" -ForegroundColor Cyan
    Push-Location $webDir
    try { npm run build; if ($LASTEXITCODE -ne 0) { throw "前端构建失败" } }
    finally { Pop-Location }
} elseif (-not $SkipWebBuild) {
    Write-Warning "web/node_modules 不存在，跳过前端构建；将使用 wwwroot 里已有的产物（必要时先在开发机执行 npm ci && npm run build）。"
}

Write-Host "`n== 1.2 发布后端 ==" -ForegroundColor Cyan
$appDir = Join-Path $InstallDir "app"
$publishArgs = @(
    "publish", (Join-Path $repo "src/OfficeTool.Api/OfficeTool.Api.csproj"),
    "-c", "Release",
    "-o", $appDir
)
if ($SelfContained) {
    $publishArgs += @("--self-contained", "true", "-r", "win-x64")
} else {
    $publishArgs += @("--self-contained", "false")
}
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "后端发布失败" }

# ── 2. 数据目录 ──────────────────────────────────────────────────────
Write-Host "`n== 2. 创建数据目录 ==" -ForegroundColor Cyan
foreach ($name in @("Templates", "Data", "Attachments", "ExtractionRules", "Records", "_trash")) {
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir $name) | Out-Null
}
foreach ($name in @("state", "keys", "logs")) {
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir $name) | Out-Null
}

# ── 3. 生成配置 ──────────────────────────────────────────────────────
Write-Host "`n== 3. 生成 appsettings.Production.json ==" -ForegroundColor Cyan

# 客户端视角：给了 ClientRoot 就拼共享盘路径；否则用本机路径
$clientTemplates = if ($ClientRoot) { Join-Path $ClientRoot "Templates" } else { Join-Path $DataDir "Templates" }
$clientData      = if ($ClientRoot) { Join-Path $ClientRoot "Data" }      else { Join-Path $DataDir "Data" }
$clientAttach    = if ($ClientRoot) { Join-Path $ClientRoot "Attachments" } else { Join-Path $DataDir "Attachments" }
$clientRules     = if ($ClientRoot) { Join-Path $ClientRoot "ExtractionRules" } else { Join-Path $DataDir "ExtractionRules" }
$clientRecords   = if ($ClientRoot) { Join-Path $ClientRoot "Records" }     else { Join-Path $DataDir "Records" }

# PowerShell 的 \ 转义无需处理，但 JSON 需要双写反斜杠
function JsonPath([string]$p) { return $p.Replace('\', '\\') }

$config = @"
{
  "_comment": "由 install.ps1 生成（Windows 原生部署）。重新安装/升级时会被覆盖，手工改动请放 appsettings.Overrides.json。",
  "ConnectionStrings": {
    "Default": "Data Source=$(JsonPath (Join-Path $DataDir 'state\officetool.db'))"
  },
  "Storage": {
    "Mode": "Local",
    "TemplatesRoot": "$(JsonPath (Join-Path $DataDir 'Templates'))",
    "DataRoot": "$(JsonPath (Join-Path $DataDir 'Data'))",
    "AttachmentsRoot": "$(JsonPath (Join-Path $DataDir 'Attachments'))",
    "RulesRoot": "$(JsonPath (Join-Path $DataDir 'ExtractionRules'))",
    "RecordsRoot": "$(JsonPath (Join-Path $DataDir 'Records'))",
    "TrashRoot": "$(JsonPath (Join-Path $DataDir '_trash'))",
    "ClientTemplatesRoot": "$(JsonPath $clientTemplates)",
    "ClientDataRoot": "$(JsonPath $clientData)",
    "ClientAttachmentsRoot": "$(JsonPath $clientAttach)",
    "ClientRulesRoot": "$(JsonPath $clientRules)",
    "ClientRecordsRoot": "$(JsonPath $clientRecords)"
  },
  "DataProtection": {
    "KeysPath": "$(JsonPath (Join-Path $DataDir 'keys'))"
  },
  "Serilog": {
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "File",
        "Args": { "path": "$(JsonPath (Join-Path $DataDir 'logs\officetool-.log'))", "rollingInterval": "Day", "retainedFileCountLimit": 31 }
      }
    ]
  },
  "AllowedHosts": "*"
}
"@

$configPath = Join-Path $appDir "appsettings.Production.json"
Set-Content -Path $configPath -Value $config -Encoding UTF8

# 把安装参数落盘：upgrade.ps1 升级时沿用同一套参数，避免"装的时候改了目录，升级又回到默认"
$settings = @{
    InstallDir  = $InstallDir
    DataDir     = $DataDir
    Port        = $Port
    ServiceName = $ServiceName
    ClientRoot  = $ClientRoot
    SelfContained = [bool]$SelfContained
}
$settings | ConvertTo-Json | Set-Content -Path (Join-Path $DataDir "install-settings.json") -Encoding UTF8

Write-Host "已写入 $configPath"
Write-Host "安装参数已记录到 $(Join-Path $DataDir 'install-settings.json')（升级时自动沿用）"
Write-Host "注意：ASPNETCORE_ENVIRONMENT 必须为 Production 才会加载它（服务注册时已设置）。"

# ── 4. 注册服务 ──────────────────────────────────────────────────────
if (-not $WhatIf) {
    Write-Host "`n== 4. 注册 Windows 服务 ==" -ForegroundColor Cyan

    $exe = Join-Path $appDir "OfficeTool.Api.exe"

    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "服务已存在，先停止并删除旧实例（数据不受影响）"
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 2
    }

    # ASPNETCORE_ENVIRONMENT=Production 使其加载上面生成的配置文件
    & sc.exe create $ServiceName `
        "binPath= ""$exe"" --urls http://0.0.0.0:$Port" `
        "start= auto" `
        "DisplayName= OfficeTool 检验记录（ELN）" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "注册服务失败（需要管理员权限运行本脚本）" }

    & sc.exe description $ServiceName "Office 检验记录与文档管理（ELN）。数据目录：$DataDir" | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null

    # ── 5. 防火墙 ────────────────────────────────────────────────────
    Write-Host "`n== 5. 防火墙 ==" -ForegroundColor Cyan
    $ruleName = "OfficeTool-$Port"
    & netsh advfirewall firewall delete rule name=$ruleName | Out-Null
    & netsh advfirewall firewall add rule name=$ruleName dir=in action=allow protocol=TCP localport=$port | Out-Null

    # ── 6. 启动 ──────────────────────────────────────────────────────
    Write-Host "`n== 6. 启动服务 ==" -ForegroundColor Cyan
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 5

    $svc = Get-Service -Name $ServiceName
    Write-Host "服务状态：$($svc.Status)"

    if ($svc.Status -eq "Running") {
        try {
            $health = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/health" -UseBasicParsing -TimeoutSec 10
            Write-Host "健康检查：HTTP $($health.StatusCode)" -ForegroundColor Green
        } catch {
            Write-Warning "健康检查未通过（服务可能仍在启动），可稍后访问 http://127.0.0.1:$Port"
        }
    }

    Write-Host ""
    Write-Host "== 安装完成 ==" -ForegroundColor Green
    Write-Host "访问 http://127.0.0.1:$Port"
    Write-Host "首次启动会创建本地管理员，初始密码在日志里（只出现一次）："
    Write-Host "  Select-String -Path '$DataDir\logs\officetool-*.log' -Pattern '初始密码'"
} else {
    Write-Host "`n== WhatIf：未注册服务。检查以上布局无误后，去掉 -WhatIf 重新执行即可。=="
}
