import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 後端位置。開發機與後端通常不在同一台——後端跑在 10.102.197.193 的容器裡，
// 前端 dev server 跑在本機——所以預設值不能寫死成 localhost。
// 以 VITE_BACKEND 覆寫，例如：
//   VITE_BACKEND=http://10.102.197.193:8080 npm run dev
const backend = process.env.VITE_BACKEND || 'http://10.102.197.193:8080'

export default defineConfig({
  plugins: [vue()],
  server: {
    host: '0.0.0.0',
    port: 5173,
    proxy: {
      '/api': {
        target: backend,
        changeOrigin: true
      }
    }
  }
})
