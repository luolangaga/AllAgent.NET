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
    port: 5188,
    strictPort: false,
  },
  build: {
    target: 'es2020',
    // 像素资源不做内联，避免 base64 破坏硬边像素渲染
    assetsInlineLimit: 0,
  },
})
