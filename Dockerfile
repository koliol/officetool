# syntax=docker/dockerfile:1

# ════════════════════════════════════════════════════════════════════════
# 阶段 1：构建前端（Vue 3 + Vite）
# ════════════════════════════════════════════════════════════════════════
FROM node:22-alpine AS web-build

WORKDIR /src/web

# 预构建前端产物（可选）。
# 若 prebuilt/webdist/index.html 存在，则完全跳过 npm ci + vite build，
# 用于 npm 源不可达的受限网络 / 离线环境（部署包中已附带该产物）。
COPY prebuilt/ /src/prebuilt/

# 先装依赖，利用层缓存：仅当 package*.json 变化时才重新 npm ci
COPY web/package.json web/package-lock.json ./

# NPM_REGISTRY 默认官方源，保持镜像可移植；国内构建务必换镜像源——
# npmjs.org 在国内 NAS 网络上极易超时，典型症状就是
#   npm error Exit handler never called!
ARG NPM_REGISTRY=https://registry.npmjs.org
RUN if [ -f /src/prebuilt/webdist/index.html ]; then \
        echo "==> 检测到 prebuilt/webdist，跳过 npm ci"; \
    else \
        npm ci \
            --registry="${NPM_REGISTRY}" \
            --no-audit \
            --no-fund \
            --fetch-retries=5 \
            --fetch-retry-mintimeout=20000 \
            --fetch-retry-maxtimeout=180000 \
            --fetch-timeout=600000; \
    fi

COPY web/ ./

# 显式指定输出目录，避免依赖 vite.config.js 里相对仓库结构的 outDir
RUN if [ -f /src/prebuilt/webdist/index.html ]; then \
        echo "==> 使用预构建前端产物，跳过 vite build"; \
        mkdir -p /src/webdist; \
        cp -a /src/prebuilt/webdist/. /src/webdist/; \
    else \
        npx vite build --outDir /src/webdist --emptyOutDir; \
    fi

# ════════════════════════════════════════════════════════════════════════
# 阶段 2：构建并发布后端（ASP.NET Core 8）
# ════════════════════════════════════════════════════════════════════════
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api-build

WORKDIR /src

# 先还原依赖（同样利用缓存）
COPY src/OfficeTool.Core/OfficeTool.Core.csproj src/OfficeTool.Core/
COPY src/OfficeTool.Api/OfficeTool.Api.csproj src/OfficeTool.Api/
RUN dotnet restore src/OfficeTool.Api/OfficeTool.Api.csproj

COPY src/ src/

# 把前端产物放进 wwwroot，实现前后端同源单容器
COPY --from=web-build /src/webdist/ src/OfficeTool.Api/wwwroot/

RUN dotnet publish src/OfficeTool.Api/OfficeTool.Api.csproj \
        -c Release \
        -o /app/publish \
        /p:UseAppHost=false

# ════════════════════════════════════════════════════════════════════════
# 阶段 3：运行时
# ════════════════════════════════════════════════════════════════════════
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# 这里刻意不做任何 apt 安装。
# 基础镜像已自带 tzdata（已验证 2026b）与 /usr/bin/bash，够用了；
# 唯一的缺口是 curl，而它只被 HEALTHCHECK 用——改用 bash 直连端口即可，
# 于是整个构建不再依赖 Debian 源。国内/受限网络下 deb.debian.org 常常完全连不上，
# 少一个硬依赖就少一类「构建卡死在 apt」的故障。

ENV TZ=Asia/Shanghai \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

WORKDIR /app

# state=SQLite 数据库，keys=Data Protection 密钥环，logs=Serilog
# 这三个必须落在持久卷上，否则容器重建会丢数据/密钥
RUN mkdir -p /app/state /app/keys /app/logs /data \
    && chown -R app:app /app /data

# 入口脚本：以 root 修正内部目录归属后降权到 PUID:PGID 运行应用。
# 这样「换运行用户」不会因为旧数据卷归属不对而启动失败（详见脚本内注释）。
COPY --chown=root:root docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod 0755 /usr/local/bin/entrypoint.sh

COPY --from=api-build --chown=app:app /app/publish ./

# 以 root 进入只是为了上面那次 chown 与 setpriv 降权；
# 应用进程本身仍以 PUID:PGID 运行（默认 1654:1654）
USER root

EXPOSE 8080

# 不依赖 curl：用 bash 的 /dev/tcp 发一个真实的 HTTP 请求，检查是否返回 200。
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080; printf "GET /api/health HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3; head -c 64 <&3 | grep -q "200 OK"'

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
