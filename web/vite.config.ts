import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  server: {
    // host: true = 监听 0.0.0.0，局域网设备可直接访问开发服务器；
    // API 与 SignalR 经下方代理转发，局域网侧只需开放 5173 一个端口
    host: true,
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5085',
      '/hubs': {
        target: 'http://localhost:5085',
        ws: true,
      },
    },
  },
})

