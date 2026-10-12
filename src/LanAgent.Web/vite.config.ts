import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { resolve } from 'path'

export default defineConfig(({ mode }) => ({
  plugins: [
    vue({
      template: {
        compilerOptions: {
          // Treat all fluent-* tags as custom elements
          isCustomElement: (tag) => tag.startsWith('fluent-')
        }
      }
    })
  ],
  resolve: {
    alias: { '@': resolve(__dirname, 'src') }
  },
  build: {
    outDir: resolve(__dirname, '../LanAgent.Desktop/Assets/web'),
    emptyOutDir: true,
    rollupOptions: {
      output: {
        entryFileNames: 'assets/[name].js',
        chunkFileNames: 'assets/[name]-[hash].js',
        assetFileNames: 'assets/[name]-[hash][extname]',
        manualChunks: {
          'vendor-vue': ['vue', 'vue-router', 'pinia'],
          'vendor-hljs': ['highlight.js'],
          'vendor-marked': ['marked', 'marked-highlight'],
        }
      }
    },
    chunkSizeWarningLimit: 800
  },
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true
  },
  base: mode === 'production' ? './' : '/'
}))
