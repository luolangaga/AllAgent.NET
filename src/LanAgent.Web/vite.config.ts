import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { resolve } from 'path'
export default defineConfig(({ mode }) => ({
 plugins: [react(), tailwindcss()], resolve: { alias: { '@': resolve(__dirname, 'src') } },
 build: { outDir: resolve(__dirname, '../LanAgent.Desktop/Assets/web'), emptyOutDir: true, rollupOptions: { output: { entryFileNames: 'assets/[name].js', manualChunks: { 'vendor-ui': ['react', 'react-dom', 'react-dom/client', '@base-ui/react', 'motion/react'], 'vendor-highlight': ['highlight.js/lib/common'] } } }, chunkSizeWarningLimit: 800 },
 server: { host: '127.0.0.1', port: 5173, strictPort: true }, base: mode === 'production' ? './' : '/'
}))
