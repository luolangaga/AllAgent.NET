import { beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params: any }
type HostWindow = Window & { chrome?: { webview: { postMessage: (json: string) => void } }; __bridgeCallback: (json: string) => void }
const host = window as unknown as HostWindow
let handle: (request: Request) => void
let webEnabled = true
const reply = (request: Request, result: unknown, error?: string) => host.__bridgeCallback(JSON.stringify({ id: request.id, result, error }))

const snapshot = () => ({
  sdkVersion: 1,
  assetBaseUrl: 'http://127.0.0.1:51234',
  plugins: [{
    packageId: 'AllAgent.NET.SamplePlugin', pluginId: 'sample', name: '示例插件', version: '1.0.0',
    enabled: true, webEnabled, active: webEnabled,
    capabilities: ['notifications', 'storage'],
    contributes: {
      pages: [{ id: 'dashboard', title: '示例仪表盘', entry: 'web/dashboard.html', icon: 'gauge', order: 40 }],
      toolCards: [{ tool: 'example_clock', entry: 'web/card.html' }],
      commands: [{ id: 'open', title: '打开示例仪表盘', page: 'dashboard' }],
    },
    devUrl: null, error: null,
  }],
})

beforeEach(() => {
  vi.resetModules()
  webEnabled = true
  host.chrome = { webview: { postMessage: (json: string) => handle(JSON.parse(json)) } }
  handle = request => {
    switch (request.method) {
      case 'web.contributions': return reply(request, snapshot())
      case 'web.setEnabled': { webEnabled = Boolean(request.params.enabled); return reply(request, true) }
      default: return reply(request, true)
    }
  }
})

describe('plugin contributions', () => {
  it('derives nav pages, palette commands and tool-card bindings from the snapshot', async () => {
    const { useContributionsStore } = await import('../src/stores/contributions')
    const store = useContributionsStore()
    await store.loadAll()
    expect(store.available).toBe(true)
    expect(store.assetBaseUrl).toBe('http://127.0.0.1:51234')
    expect(store.pages.map(entry => entry.route)).toEqual(['plugin:allagent.net.sampleplugin:dashboard'])
    expect(store.pageByRoute('plugin:allagent.net.sampleplugin:dashboard')?.entry).toBe('web/dashboard.html')
    expect(store.pageByRoute('plugin:allagent.net.sampleplugin:dashboard')?.title).toBe('示例仪表盘')
    expect(store.commands[0]).toMatchObject({ title: '打开示例仪表盘', route: 'plugin:allagent.net.sampleplugin:dashboard' })
    expect(store.toolCardFor('example_clock')?.entry).toBe('web/card.html')
    expect(store.toolCardFor('file_read')).toBeUndefined()
  })

  it('hides inactive plugins and refreshes when the host pushes contributions.changed', async () => {
    const { useContributionsStore } = await import('../src/stores/contributions')
    const store = useContributionsStore()
    await store.loadAll()
    expect(store.pages).toHaveLength(1)

    webEnabled = false
    host.__bridgeCallback(JSON.stringify({ type: 'web.contributions.changed' }))
    await new Promise(resolve => setTimeout(resolve, 30))

    expect(store.plugins).toHaveLength(1)
    expect(store.plugins[0].active).toBe(false)
    expect(store.pages).toHaveLength(0)
    expect(store.commands).toHaveLength(0)
  })

  it('toggles the per-plugin web switch through web.setEnabled', async () => {
    const requests: Request[] = []
    handle = request => {
      requests.push(request)
      return request.method === 'web.contributions' ? reply(request, snapshot()) : reply(request, true)
    }
    const { useContributionsStore } = await import('../src/stores/contributions')
    const store = useContributionsStore()
    await store.loadAll()
    await store.setWebEnabled('AllAgent.NET.SamplePlugin', false)
    expect(requests.some(request => request.method === 'web.setEnabled' && request.params.enabled === false)).toBe(true)
    expect(store.busy).toBe(false)
  })

  it('marks contributions unavailable when the host does not serve them', async () => {
    handle = request => reply(request, undefined, `Unknown method: ${request.method}`)
    const { useContributionsStore } = await import('../src/stores/contributions')
    const store = useContributionsStore()
    await store.loadAll()
    expect(store.available).toBe(false)
    expect(store.pages).toHaveLength(0)
    expect(store.assetBaseUrl).toBeNull()
  })
})
