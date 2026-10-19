import { defineConfig } from 'vite'
import { resolve } from 'path'

// The plugin web SDK is a separate, dependency-free bundle: plugin pages load it as a classic
// script from the plugin asset origin (/__sdk/allagent-plugin-sdk.js). Built after the app build
// so `emptyOutDir` here (dist/sdk) never wipes dist/index.html.
export default defineConfig({
  build: {
    outDir: resolve(__dirname, 'dist/sdk'),
    emptyOutDir: true,
    minify: false,
    lib: {
      entry: resolve(__dirname, 'src/plugin-sdk/index.ts'),
      name: 'AllAgentPlugin',
      formats: ['iife', 'es'],
      fileName: format => (format === 'es' ? 'allagent-plugin-sdk.mjs' : 'allagent-plugin-sdk.js'),
    },
  },
})
