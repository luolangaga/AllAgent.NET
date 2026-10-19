import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { resolve } from 'path'

export default defineConfig({
  define: { 'process.env.NODE_ENV': '"production"' },
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': resolve(__dirname, 'src') } },
  build: {
    outDir: resolve(__dirname, 'dist/sdk'),
    emptyOutDir: false,
    lib: {
      entry: resolve(__dirname, 'src/plugin-sdk/ui.tsx'),
      formats: ['es'],
      fileName: () => 'allagent-plugin-ui.mjs',
    },
    rollupOptions: { output: { inlineDynamicImports: true } },
  },
})
