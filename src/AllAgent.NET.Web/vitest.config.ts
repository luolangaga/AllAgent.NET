import { defineConfig } from 'vitest/config'
import { resolve } from 'path'
export default defineConfig({ resolve: { alias: { '@': resolve(__dirname, 'src') } }, test: { environment: 'jsdom', env: { NODE_ENV: 'test' }, include: ['tests/**/*.test.{ts,tsx}'], setupFiles: ['tests/setup.ts'] } })
