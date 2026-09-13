#!/usr/bin/env bash
#
# 生成部署包。
#
# 与「直接 git archive」的区别：会把 prebuilt/webdist（已构建好的前端产物）一起打进去，
# 这样在 npm 源不可达的 NAS / 离线环境上也能构建镜像 —— Dockerfile 检测到
# prebuilt/webdist/index.html 存在就会跳过 npm ci 与 vite build。
#
# 用法：
#   bash scripts/make-deploy-package.sh [输出路径]
#
# 关于桌面插件：默认只把**小体积**的框架依赖版（约 180KB）打进包里，
# 保持部署包可以直接通过聊天/邮件传。自包含版（约 65MB）默认**不打**进来，
# 需要的话加环境变量：
#   PLUGIN_INCLUDE_STANDALONE=1 bash scripts/make-deploy-package.sh
#
set -euo pipefail

cd "$(dirname "$0")/.."

OUT="${1:-/tmp/OfficeTool-deploy.zip}"
WEB_DIST="prebuilt/webdist"
PLUGIN_DIR="prebuilt/plugin"
INCLUDE_STANDALONE="${PLUGIN_INCLUDE_STANDALONE:-0}"

if [ ! -f "$WEB_DIST/index.html" ]; then
  echo "✗ 缺少 $WEB_DIST/index.html" >&2
  echo "  请先构建前端：" >&2
  echo "    cd web && npm ci && npm run build -- --outDir ../$WEB_DIST --emptyOutDir" >&2
  exit 1
fi

python3 - "$OUT" "$WEB_DIST" "$PLUGIN_DIR" "$INCLUDE_STANDALONE" <<'PY'
import io, pathlib, subprocess, sys, zipfile

out_path = sys.argv[1]
web_dist = pathlib.Path(sys.argv[2])
plugin_dir = pathlib.Path(sys.argv[3])
include_standalone = sys.argv[4] == "1"

# 1) 版本控制里的源码（天然排除 node_modules / bin / obj / 数据库 / 日志）
archive = subprocess.run(
    ["git", "archive", "--format=zip", "--prefix=officetool/", "HEAD"],
    capture_output=True, check=True,
).stdout
src = zipfile.ZipFile(io.BytesIO(archive))

web_added = 0
plugin_added = 0
skipped = []

with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as dst:
    for item in src.infolist():
        dst.writestr(item, src.read(item.filename))

    # 2) 预构建前端产物
    for path in sorted(web_dist.rglob("*")):
        if path.is_file():
            dst.write(path, f"officetool/{web_dist}/{path.relative_to(web_dist)}")
            web_added += 1

    # 3) 桌面插件产物
    if plugin_dir.is_dir():
        for path in sorted(plugin_dir.iterdir()):
            if not path.is_file():
                continue
            if "standalone" in path.name and not include_standalone:
                skipped.append(path.name)
                continue
            dst.write(path, f"officetool/{plugin_dir}/{path.name}")
            plugin_added += 1

print(f"源文件 {len(src.infolist())} 项 + 预构建前端 {web_added} 项 + 插件 {plugin_added} 项 → {out_path}")
if skipped:
    print(f"（跳过自包含版：{', '.join(skipped)}；需要请设 PLUGIN_INCLUDE_STANDALONE=1）")
PY

ls -lh "$OUT" | awk '{print "包大小:", $5}'
echo "校验预构建产物已入包："
unzip -l "$OUT" | grep -E "prebuilt/webdist/" | head -5
echo "校验插件已入包："
unzip -l "$OUT" | grep -E "prebuilt/plugin/" || echo "  （无插件产物，先跑 scripts/build-plugin.sh）"
