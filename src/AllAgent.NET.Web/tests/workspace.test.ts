import { beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params: any }
type HostWindow = Window & { chrome?: { webview: { postMessage: (json: string) => void } }; __bridgeCallback: (json: string) => void }
const host = window as unknown as HostWindow
let handle: (request: Request) => void
let current = 'C:\\a'
const reply = (request: Request, result: unknown, error?: string) => host.__bridgeCallback(JSON.stringify({ id: request.id, result, error }))
const info = (workspace: string) => ({ workspace, dataDirectory: `${workspace}\\.allagent\\data`, sharedDirectory: 'C:\\shared\\AllAgent.NET' })
const entry = (workspace: string, isCurrent: boolean) => ({ ...info(workspace), name: workspace.slice(-1), sessionCount: 4, isCurrent })
const controls = (permissionMode = 'full') => ({ permissionMode, permissionLabel: '完全访问', sandboxMode: 'auto', sandboxEffective: '首次任务时验证', running: false })

beforeEach(() => {
  vi.resetModules()
  current = 'C:\\a'
  host.chrome = { webview: { postMessage: (json: string) => handle(JSON.parse(json)) } }
  handle = request => {
    switch (request.method) {
      case 'workspace.get': return reply(request, info(current))
      case 'workspaces.list': return reply(request, [entry(current, true), entry('C:\\b', false)])
      case 'session.controls': return reply(request, controls())
      case 'workspace.switch': { current = request.params.path; return reply(request, info(current)) }
      case 'session.setPermission': return reply(request, controls(request.params.mode))
      case 'session.setSandbox': return reply(request, controls())
      default: return reply(request, true)
    }
  }
})

describe('workspace control', () => {
  it('loads the active workspace, recent entries and live controls', async () => {
    const { useWorkspaceStore } = await import('../src/stores/workspace')
    const store = useWorkspaceStore()
    await store.load()
    expect(store.available).toBe(true)
    expect(store.workspace).toBe('C:\\a')
    expect(store.dataDirectory).toContain('.allagent')
    expect(store.list.map(item => item.workspace)).toEqual(['C:\\a', 'C:\\b'])
    expect(store.list[0]).toMatchObject({ isCurrent: true, sessionCount: 4 })
    expect(store.controls?.permissionMode).toBe('full')
  })

  it('switches workspaces and applies the reply', async () => {
    const { useWorkspaceStore } = await import('../src/stores/workspace')
    const store = useWorkspaceStore()
    await store.load()
    await store.switchTo('C:\\b')
    expect(store.workspace).toBe('C:\\b')
    expect(store.switching).toBe(false)
  })

  it('asks the host to create a missing workspace when requested', async () => {
    const requests: Request[] = []
    handle = request => {
      requests.push(request)
      if (request.method === 'workspace.switch') { current = request.params.path; return reply(request, info(current)) }
      if (request.method === 'workspace.get') return reply(request, info(current))
      if (request.method === 'workspaces.list') return reply(request, [entry(current, true)])
      if (request.method === 'session.controls') return reply(request, controls())
      return reply(request, true)
    }
    const { useWorkspaceStore } = await import('../src/stores/workspace')
    const store = useWorkspaceStore()
    await store.load()
    await store.switchTo('C:\\brand-new', true)
    const switchCall = requests.find(request => request.method === 'workspace.switch')
    expect(switchCall?.params).toMatchObject({ path: 'C:\\brand-new', create: true })
    expect(store.workspace).toBe('C:\\brand-new')
  })

  it('drives permission and sandbox changes through the shared terminal controls', async () => {
    const { useWorkspaceStore } = await import('../src/stores/workspace')
    const store = useWorkspaceStore()
    await store.load()
    await store.setPermission('plan')
    expect(store.controls?.permissionMode).toBe('plan')
    await store.setSandbox('monty')
    expect(store.controls?.sandboxMode).toBe('auto')
  })

  it('marks workspace control unavailable when the host does not serve it', async () => {
    handle = request => reply(request, null, 'Unknown method: workspace.get')
    const { useWorkspaceStore } = await import('../src/stores/workspace')
    const store = useWorkspaceStore()
    await store.load()
    expect(store.available).toBe(false)
  })

  it('follows a workspace change pushed by another tab', async () => {
    const { useWorkspaceStore } = await import('../src/stores/workspace')
    const store = useWorkspaceStore()
    await store.load()
    host.__bridgeCallback(JSON.stringify({ type: 'workspace.changed', workspace: 'C:\\b', dataDirectory: 'C:\\b\\.allagent\\data' }))
    expect(store.workspace).toBe('C:\\b')
  })
})

describe('plugins store', () => {
  beforeEach(() => {
    handle = request => {
      switch (request.method) {
        case 'plugins.list': return reply(request, {
          plugins: [{ id: 'commandcode', name: 'CommandCode', packageId: 'AllAgent.NET.CommandCode', version: '1.0.0', enabled: true, error: null }],
          providers: [{ providerId: 'commandcode' }],
          authentication: [{ id: 'codex', name: 'ChatGPT 账号' }],
          hooks: [{ name: 'turn-start', enabled: true }],
        })
        case 'plugins.search': return reply(request, [{ id: 'AllAgent.NET.WorkBuddy', version: '1.0.2', description: 'work buddy', authors: 'AllAgent.NET', downloads: 42 }])
        case 'plugins.setEnabled': return reply(request, true)
        default: return reply(request, true)
      }
    }
  })

  it('lists plugins, providers, authentication and hooks', async () => {
    const { usePluginsStore } = await import('../src/stores/plugins')
    const store = usePluginsStore()
    await store.loadAll()
    expect(store.available).toBe(true)
    expect(store.plugins[0]?.packageId).toBe('AllAgent.NET.CommandCode')
    expect(store.providers).toEqual(['commandcode'])
    expect(store.authentication[0]?.name).toBe('ChatGPT 账号')
    expect(store.hooks[0]?.name).toBe('turn-start')
  })

  it('searches the NuGet store and toggles enablement', async () => {
    const { usePluginsStore } = await import('../src/stores/plugins')
    const store = usePluginsStore()
    await store.loadAll()
    await store.search('workbuddy')
    expect(store.searchResults[0]?.id).toBe('AllAgent.NET.WorkBuddy')
    await store.setEnabled('AllAgent.NET.CommandCode', false)
    expect(store.busy).toBe(false)
    expect(store.plugins[0]?.enabled).toBe(true)
  })

  it('marks plugin management unavailable when the host does not serve it', async () => {
    handle = request => reply(request, null, 'Unknown method: plugins.list')
    const { usePluginsStore } = await import('../src/stores/plugins')
    const store = usePluginsStore()
    await store.loadAll()
    expect(store.available).toBe(false)
    expect(store.loaded).toBe(true)
  })
})
