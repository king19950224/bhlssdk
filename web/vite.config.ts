import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  server: {
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
