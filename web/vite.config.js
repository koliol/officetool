import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    host: '127.0.0.1',
    proxy: {
      // 开发期把 /api 代理到后端，生产环境由 Kestrel/IIS 同源托管
      '/api': {
        target: 'http://127.0.0.1:5080',
        changeOrigin: true,
      },
    },
  },
  build: {
    // 直接产出到后端 wwwroot，单机部署时无需额外配置站点
    outDir: '../src/OfficeTool.Api/wwwroot',
    emptyOutDir: true,
  },
})
