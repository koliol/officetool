#!/usr/bin/env bash
# 容器形态验证：镜像构建产物是否真的能跑起来、映射对不对、数据会不会丢。
# 用法： sudo bash scripts/docker-verify.sh
# 前置： 已执行 sudo docker build -t officetool:1.0.0 .
set -u

IMAGE=officetool:1.0.0
NAME=officetool-verify
PORT=8080
SHARE=/tmp/officetool-verify-share
STATE_VOL=officetool-verify-state
KEYS_VOL=officetool-verify-keys

B="http://127.0.0.1:$PORT"
PASS=0; FAIL=0
chk() {
  if [ "$2" = "$3" ]; then echo "  ✓ $1 ($3)"; PASS=$((PASS+1));
  else echo "  ✗ $1 — 期望 $2 实得 $3"; FAIL=$((FAIL+1)); fi
}

cleanup() {
  docker rm -f "$NAME" >/dev/null 2>&1 || true
}

echo "═══ 0. 准备 ═══"
cleanup
docker volume rm "$STATE_VOL" "$KEYS_VOL" >/dev/null 2>&1 || true
rm -rf "$SHARE" && mkdir -p "$SHARE"
# 容器内以非 root 的 app 用户(uid 1654)运行；宿主机挂载目录需对其可写。
# 群晖上等价操作 = 给共享文件夹配 ACL 或改用 compose 的 user: 映射。
chmod 777 "$SHARE"
echo "  测试共享目录: $SHARE"

echo "═══ 1. 启动容器 ═══"
docker run -d --name "$NAME" \
  -p "$PORT:8080" \
  -e TZ=Asia/Shanghai \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e 'Storage__ClientTemplatesRoot=\\NAS\OfficeDocs\Templates' \
  -e 'Storage__ClientDataRoot=\\NAS\OfficeDocs\Data' \
  -v "$SHARE:/data" \
  -v "$STATE_VOL:/app/state" \
  -v "$KEYS_VOL:/app/keys" \
  "$IMAGE" >/dev/null || { echo "容器启动失败"; exit 1; }

echo "  等待健康检查..."
ready=0
for i in $(seq 1 60); do
  if curl -sS --max-time 2 "$B/api/health" >/dev/null 2>&1; then ready=1; echo "  ready after ${i}s"; break; fi
  sleep 1
done
[ "$ready" = 1 ] || { echo "✗ 容器未在 60s 内就绪"; docker logs "$NAME" | tail -30; exit 1; }
chk "容器状态为 running" "true" "$(docker inspect -f '{{.State.Running}}' "$NAME")"

echo "═══ 2. 启动日志（存储根探测） ═══"
docker logs "$NAME" 2>&1 | grep -E "校验通过|不可用|存储模式|Now listening" | sed 's/^/  /'
if docker logs "$NAME" 2>&1 | grep -q "不可用"; then
  echo "  ✗ 存储根探测失败"; FAIL=$((FAIL+1));
else
  echo "  ✓ 存储根可读写"; PASS=$((PASS+1));
fi

echo "═══ 3. 接口冒烟（复用 api-smoke.sh 的 78 项） ═══"
# 只 tail 末尾会把失败项截掉、只留一行汇总，排查时看不到是哪条挂了 —— 直接把失败项打出来
BASE_URL="$B" SHARE_ROOT="$SHARE" bash scripts/api-smoke.sh 2>&1 | grep -E "✗|═══ 结果"
smoke_rc=${PIPESTATUS[0]}
chk "冒烟脚本全部通过" "0" "$smoke_rc"

echo "═══ 4. 客户端视角路径（群晖部署核心） ═══"
cfg=$(curl -sS "$B/api/system/config")
echo "  $cfg" | python3 -c 'import sys,json;d=json.load(sys.stdin);print("  clientTemplatesRoot =",d["clientTemplatesRoot"]);print("  clientDataRoot      =",d["clientDataRoot"]);print("  mode/templatesRoot  =",d["mode"],d["templatesRoot"])'
chk "clientDataRoot 为 UNC 形式" "\\\\NAS\\OfficeDocs\\Data" \
  "$(echo "$cfg" | python3 -c 'import sys,json;print(json.load(sys.stdin)["clientDataRoot"])')"

echo "  列表接口返回的 accessPath："
curl -sS "$B/api/documents" | python3 -c '
import sys,json
d=json.load(sys.stdin)
for i in d["items"][:3]:
    print("   ", i["accessPath"])
' || true

echo "═══ 5. 文件真的落在挂载卷上（而非容器内层） ═══"
echo "  宿主机 $SHARE 内容："
find "$SHARE" -type f | sed "s|^$SHARE|  <share>|" | head -10
n=$(find "$SHARE" -type f | wc -l)
if [ "$n" -ge 2 ]; then echo "  ✓ 文件写到了 bind mount 上（$n 个）"; PASS=$((PASS+1));
else echo "  ✗ 文件未落盘"; FAIL=$((FAIL+1)); fi

echo "═══ 6. 持久化：重启容器后数据仍在 ═══"
before=$(curl -sS "$B/api/documents" | python3 -c 'import sys,json;print(json.load(sys.stdin)["total"])')
before_p=$(curl -sS "$B/api/projects" | python3 -c 'import sys,json;print(len(json.load(sys.stdin)))')
docker restart "$NAME" >/dev/null
for i in $(seq 1 60); do curl -sS --max-time 2 "$B/api/health" >/dev/null 2>&1 && break; sleep 1; done
after=$(curl -sS "$B/api/documents" | python3 -c 'import sys,json;print(json.load(sys.stdin)["total"])')
after_p=$(curl -sS "$B/api/projects" | python3 -c 'import sys,json;print(len(json.load(sys.stdin)))')
chk "重启后文档数不变" "$before" "$after"
chk "重启后项目数不变" "$before_p" "$after_p"

echo "═══ 7. 重建容器（数据卷保留）后数据仍在 ═══"
docker rm -f "$NAME" >/dev/null 2>&1
docker run -d --name "$NAME" \
  -p "$PORT:8080" \
  -e TZ=Asia/Shanghai \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e 'Storage__ClientTemplatesRoot=\\NAS\OfficeDocs\Templates' \
  -e 'Storage__ClientDataRoot=\\NAS\OfficeDocs\Data' \
  -v "$SHARE:/data" \
  -v "$STATE_VOL:/app/state" \
  -v "$KEYS_VOL:/app/keys" \
  "$IMAGE" >/dev/null
for i in $(seq 1 60); do curl -sS --max-time 2 "$B/api/health" >/dev/null 2>&1 && break; sleep 1; done
rebuilt=$(curl -sS "$B/api/documents" | python3 -c 'import sys,json;print(json.load(sys.stdin)["total"])')
chk "重建容器后文档数不变" "$before" "$rebuilt"

echo
echo "═══ 结果：通过 $PASS / 失败 $FAIL ═══"
echo "（容器 $NAME 仍在运行，便于手工浏览器验证；清理： docker rm -f $NAME）"
[ "$FAIL" -eq 0 ] || exit 1
