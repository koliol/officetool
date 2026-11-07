# =====================================================================
# OfficeTool Windows 卸载
#
# 停止并删除 Windows 服务、删除防火墙规则、删除程序目录。
# **数据目录默认保留**（共享盘、数据库、日志）——卸载程序不等于销毁记录；
# 确认不要数据时加 -RemoveData。
# =====================================================================
param(
    [string]$InstallDir  = "C:\OfficeTool",
    [string]$DataDir     = "C:\OfficeToolData",
    [int]   $Port        = 8080,
    [string]$ServiceName = "OfficeTool",
    [switch]$RemoveData
)

$ErrorActionPreference = "Continue"

Write-Host "== OfficeTool 卸载 ==" -ForegroundColor Cyan

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "停止并删除服务 $ServiceName ..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
} else {
    Write-Host "服务 $ServiceName 不存在，跳过。"
}

$ruleName = "OfficeTool-$Port"
Write-Host "删除防火墙规则 $ruleName ..."
& netsh advfirewall firewall delete rule name=$ruleName | Out-Null

$appDir = Join-Path $InstallDir "app"
if (Test-Path $appDir) {
    Write-Host "删除程序目录 $appDir ..."
    Remove-Item $appDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($RemoveData) {
    Write-Warning "删除数据目录 $DataDir（含全部记录文件、数据库与日志）！"
    Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "数据目录 $DataDir 已保留。确认不再需要时手工删除，或加 -RemoveData 重新运行。"
}

Write-Host "`n== 卸载完成 ==" -ForegroundColor Green
