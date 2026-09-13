# prebuilt/ — 预构建前端产物（可选）

本目录用于**受限网络 / 离线构建**。

- 仓库里本目录是空的（仅 `.gitkeep`）。
- **官方部署包（zip）中会附带 `prebuilt/webdist/`**，即已构建好的前端产物。
- 若 `prebuilt/webdist/index.html` 存在，`Dockerfile` 会**完全跳过 `npm ci` 与 `vite build`**，
  构建过程不访问任何 npm 源 —— 适用于 NAS 无法访问 npm 镜像的场景。

## 自己生成

```bash
cd web
npm ci
npx vite build --outDir ../prebuilt/webdist --emptyOutDir
```

## 想强制走「容器内构建前端」的正常路径

删掉本目录下的 `webdist/` 再构建即可：

```bash
rm -rf prebuilt/webdist
docker compose up -d --build
```
