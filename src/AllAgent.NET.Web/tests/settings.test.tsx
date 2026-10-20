import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params?: unknown }
const host = window as unknown as { chrome: { webview: { postMessage(json: string): void } }; __bridgeCallback(json: string): void }

const usage = {
  models: [
    { providerId: 'openai', modelId: 'gpt-5.1', inputTokens: 1_800_000, outputTokens: 420_000, requests: 320, requestsWithUsage: 310 },
    { providerId: 'anthropic', modelId: 'claude-sonnet-4.5', inputTokens: 1_200_000, outputTokens: 280_000, requests: 210, requestsWithUsage: 200 },
    { providerId: 'google', modelId: 'gemini-2.5-pro', inputTokens: 620_000, outputTokens: 120_000, requests: 95, requestsWithUsage: 90 },
  ],
  days: Array.from({ length: 120 }, (_, index) => {
    const date = new Date()
    date.setDate(date.getDate() - (119 - index))
    const month = String(date.getMonth() + 1).padStart(2, '0')
    const day = String(date.getDate()).padStart(2, '0')
    return {
      date: `${date.getFullYear()}-${month}-${day}`,
      inputTokens: 40_000 + index * 100,
      outputTokens: 12_000 + index * 40,
      requests: 18 + (index % 7),
      requestsWithUsage: 18 + (index % 7),
    }
  }),
  totals: { inputTokens: 3_620_000, outputTokens: 820_000, requests: 625, requestsWithUsage: 600, activeDays: 120 },
}

beforeEach(() => {
  vi.resetModules()
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  host.chrome = { webview: { postMessage(json) {
    const request = JSON.parse(json) as Request
    const reply = (data: unknown) => host.__bridgeCallback(JSON.stringify({ id: request.id, data }))
    switch (request.method) {
      case 'usage.stats': return reply(usage)
      case 'webui.network.get': return reply({
        lan: false, port: 5173, url: 'http://127.0.0.1:5173/', localUrl: 'http://127.0.0.1:5173/',
        urls: [], authenticationRequired: false, passwordConfigured: false, accessUrl: null, qrDataUrl: null,
      })
      case 'update.status': return reply({ currentVersion: '2.0.0', latestVersion: '2.0.0', updateAvailable: false, installKind: 'NuGet', architecture: 'x64' })
      case 'update.settings.get': return reply({ source: 'auto', channel: 'stable', autoCheck: true, autoInstall: false, requireSignature: false, requireChecksum: true })
      default: return reply(true)
    }
  } } }
})

afterEach(() => { vi.unstubAllGlobals() })

async function settle(check: () => boolean, timeout = 3000) {
  const deadline = Date.now() + timeout
  while (!check() && Date.now() < deadline) {
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
  }
}

describe('分类设置页', () => {
  it('renders categories and the model usage charts', async () => {
    const { default: Settings } = await import('../src/views/Settings')
    const element = document.createElement('div')
    document.body.appendChild(element)
    const root = createRoot(element)
    try {
      await act(async () => { root.render(<Settings />) })
      await settle(() => element.textContent?.includes('外观与语言') === true)

      const usageTab = [...element.querySelectorAll<HTMLButtonElement>('button')]
        .find(button => button.textContent?.trim() === '模型用量')
      expect(usageTab).toBeTruthy()
      await act(async () => { usageTab!.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
      await settle(() => element.textContent?.includes('各模型用量') === true)

      expect(element.textContent).toContain('累计 Token')
      expect(element.textContent).toContain('gpt-5.1')
      expect(element.textContent).toContain('claude-sonnet-4.5')
      expect(element.textContent).toContain('使用活跃图')
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  }, 20000)
})
