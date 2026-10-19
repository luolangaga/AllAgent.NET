import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string }
type Item = { label: string; value: string }

const host = window as unknown as { chrome: { webview: { postMessage(json: string): void } }; __bridgeCallback(json: string): void }

const methods: string[] = []
let items: Item[] = []

beforeEach(() => {
  vi.resetModules()
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  methods.length = 0
  items = []
  host.chrome = { webview: { postMessage(json) {
    const request = JSON.parse(json) as Request
    methods.push(request.method)
    const data = request.method === 'session.footer' ? { refreshing: false, items } : null
    host.__bridgeCallback(JSON.stringify({ id: request.id, data }))
  } } }
})
afterEach(() => { vi.unstubAllGlobals() })

async function mount(element: HTMLDivElement) {
  const { SessionFooter } = await import('../src/components/SessionFooter')
  const root = createRoot(element)
  await act(async () => {
    root.render(<SessionFooter configId="cfg" model="gpt-5.1" />)
    // One microtask turn is not enough: the first poll resolves through the bridge callback.
    await new Promise(resolve => setTimeout(resolve, 30))
  })
  return root
}

describe('session footer', () => {
  it('renders the text providers registered for the bottom of the terminal', async () => {
    items = [
      { label: 'Codex 额度', value: '五小时剩余 92%' },
      { label: '每周剩余', value: '68%' },
    ]
    const element = document.createElement('div')
    document.body.appendChild(element)
    const root = await mount(element)
    try {
      expect(element.textContent).toContain('Codex 额度')
      expect(element.textContent).toContain('五小时剩余 92%')
      expect(element.textContent).toContain('每周剩余')
      expect(methods).toContain('session.footer')
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  })

  it('stays hidden when no provider matches the current model', async () => {
    const element = document.createElement('div')
    document.body.appendChild(element)
    const root = await mount(element)
    try {
      expect(element.textContent).toBe('')
      expect(element.querySelector('.session-footer')).toBeNull()
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  })
})
