#!/bin/sh
#
# 进入 → 修正内部目录归属 → 降权到 PUID:PGID（含附加组）运行应用。
#
# 为什么不用 compose 的 user: 直接指定运行用户：
#   /app/state、/app/keys、/app/logs 是数据卷，Docker **只在首次创建卷时**
#   沿用镜像里的归属。之后若改了运行用户，旧卷仍然归原来的 uid，
#   应用会因为写不进 SQLite 目录而启动即失败（端口都不监听）。
#
#   群晖上又几乎一定要改运行用户（共享文件夹走 Synology ACL），
#   所以「运行用户会变」是常态。每次启动都按当前 PUID/PGID 修正一次归属，
#   无论卷是新建的还是别人 uid 建的，都能自愈。
#
set -eu

PUID="${PUID:-1654}"
PGID="${PGID:-1654}"

# 附加组。对应 `id 你的用户名` 输出里的 groups 列表（逗号分隔的 gid）。
#
# 为什么必须支持：群晖共享文件夹的 ACL 常常**只授给某个组**，例如
#       [0] group:administrators:allow:rwx...:fd--
#   此时账号可能有权限，但那是通过**附加组**获得的。
#   若降权时把附加组清空（只剩 PGID），就会出现
#   「明明这个账号能访问，容器却报 Permission denied」。
#   照抄 `id` 的 groups 即可，例如：SUP_GIDS=100,101
SUP_GIDS="${SUP_GIDS:-}"

# 去掉空格，拼成 setpriv --groups 需要的列表；PGID 一并带上，顺带去重
# （日志里出现 [100,100,101] 会让人怀疑是不是配错了）
SUP_GIDS="$(printf '%s' "$SUP_GIDS" | tr -d ' ')"
GROUP_LIST=""
for g in $(printf '%s' "${PGID},${SUP_GIDS}" | tr ',' ' '); do
    case ",${GROUP_LIST}," in
        *",${g},"*) ;;                                        # 已存在，跳过
        *) GROUP_LIST="${GROUP_LIST:+${GROUP_LIST},}${g}" ;;
    esac
done

for dir in /app/state /app/keys /app/logs; do
    mkdir -p "$dir"
    chown -R "$PUID:$PGID" "$dir" 2>/dev/null || true
done

echo "==> 运行用户 uid=$PUID gid=$PGID 附加组=[$GROUP_LIST]；内部状态目录归属已同步"

exec setpriv --reuid="$PUID" --regid="$PGID" --groups="$GROUP_LIST" -- \
    dotnet /app/OfficeTool.Api.dll
