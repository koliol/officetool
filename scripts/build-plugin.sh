#!/usr/bin/env bash
#
# 构建 Windows 桌面托盘插件的交付产物。
#
# 在 Linux 上交叉编译出 Windows exe：产物是真正的 PE32+ 可执行文件，
# 依赖的是 Microsoft.NET.Sdk.WindowsDesktop（见文件末尾说明）。
#
# 产出（均落在 prebuilt/plugin/）：
#   1. OfficeToolPlugin.exe            框架依赖单文件，体积小；目标机需装 .NET 8 桌面运行时
#   2. OfficeToolPlugin-standalone.exe 自包含单文件，体积大；无需任何前置依赖
#
# 脚本是幂等的：可以反复执行，每次都会先清理旧产物再重新生成。
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

PROJECT="$REPO_ROOT/src/OfficeTool.Desktop/OfficeTool.Desktop.csproj"
OUT_DIR="$REPO_ROOT/prebuilt/plugin"
STAGE_FDD="$OUT_DIR/.stage-fdd"
STAGE_SCD="$OUT_DIR/.stage-scd"

FDD_NAME="OfficeToolPlugin.exe"
SCD_NAME="OfficeToolPlugin-standalone.exe"

log() { printf '\n[build-plugin] %s\n' "$*"; }
fail() { printf '\n[build-plugin] 错误：%s\n' "$*" >&2; exit 1; }

[ -f "$PROJECT" ] || fail "找不到项目文件：$PROJECT"
command -v dotnet >/dev/null 2>&1 || fail "找不到 dotnet 命令，请先安装 .NET 8 SDK。"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

log "仓库根目录：$REPO_ROOT"
log "清理上一次的中间产物与旧交付物……"
rm -rf "$STAGE_FDD" "$STAGE_SCD"
mkdir -p "$OUT_DIR" "$STAGE_FDD" "$STAGE_SCD"
rm -f "$OUT_DIR/$FDD_NAME" "$OUT_DIR/$SCD_NAME"

log "① 发布「框架依赖」单文件（约 150KB，目标机需安装 .NET 8 桌面运行时）……"
dotnet publish "$PROJECT" -c Release -o "$STAGE_FDD" \
    --self-contained false \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=false \
    -p:DebugType=none

log "② 发布「自包含」单文件（约 65MB，无需任何前置依赖）……"
dotnet publish "$PROJECT" -c Release -o "$STAGE_SCD" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none

log "③ 收集产物……"
[ -f "$STAGE_FDD/OfficeToolPlugin.exe" ] || fail "框架依赖发布没有产出 OfficeToolPlugin.exe"
[ -f "$STAGE_SCD/OfficeToolPlugin.exe" ] || fail "自包含发布没有产出 OfficeToolPlugin.exe"

install -m 0755 "$STAGE_FDD/OfficeToolPlugin.exe" "$OUT_DIR/$FDD_NAME"
install -m 0755 "$STAGE_SCD/OfficeToolPlugin.exe" "$OUT_DIR/$SCD_NAME"

rm -rf "$STAGE_FDD" "$STAGE_SCD"

log "④ 校验产物……"
if command -v file >/dev/null 2>&1; then
    file "$OUT_DIR/$FDD_NAME" "$OUT_DIR/$SCD_NAME" || true
fi
ls -lh "$OUT_DIR/$FDD_NAME" "$OUT_DIR/$SCD_NAME"

log "完成。产物目录：$OUT_DIR"
printf '[build-plugin] 提示：Windows 上执行 "%s" 注册协议，执行 "... --uninstall" 清理。\n' "$SCD_NAME"
