import { beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params: any }
type HostWindow = Window & { chrome?: { webview: { postMessage: (json: string) => void } }; __bridgeCallback: (json: string) => void }
const host = window as unknown as HostWindow
let handle: (request: Request) => void
let current = 'C:\\a'
const reply = (request: Request, result: unknown, error?: string) => host.__bridgeCallback(JSON.stringify({ id: request.id, result, error }))
const info = (workspace: string) => ({ workspace, dataDirectory: `${workspace}\\.allagent\\data`, sharedDirectory: 'C:\\shared\\AllAgent.NET' })
const entry = (workspace: string, isCurrent: boolean) => ({ ...info(workspace), name: workspace.slice(-1), sessionCount: 4, isCurrent })
const controls = (permissionMode = 'restricted') => ({ permissionMode, permissionLabel: '限制模式', sandboxMode: 'auto', sandboxEffective: '首次任务时验证', running: false })

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
    expect(store.controls?.permissionMode).toBe('restricted')
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

  it('refreshes the live control state when a run starts or finishes', async () => {
    let running = true
    handle = request => {
      switch (request.method) {
        case 'workspace.get': return reply(request, info(current))
        case 'workspaces.list': return reply(request, [entry(current, true)])
        case 'session.controls': return reply(request, { ...controls(), running })
        default: return reply(request, true)
      }
    }
    const { useWorkspaceStore } = await import('../src/stores/workspace')
    const store = useWorkspaceStore()
    await store.load()
    expect(store.controls?.running).toBe(true)
    // The stale flag was the bug: a finished task never unlocked the sandbox switch.
    running = false
    host.__bridgeCallback(JSON.stringify({ type: 'runs.changed', conversations: [] }))
    await vi.waitFor(() => expect(store.controls?.running).toBe(false))
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
        case 'plugins.search': return reply(request, { totalHits: 12, packages: [{ id: 'AllAgent.NET.WorkBuddy', version: '1.0.2', description: 'work buddy', authors: 'AllAgent.NET', downloads: 42 }] })
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

  it('searches the NuGet store with pagination and toggles enablement', async () => {
    const requests: Request[] = []
    const base = handle
    handle = request => { requests.push(request); return base(request) }
    const { usePluginsStore } = await import('../src/stores/plugins')
    const store = usePluginsStore()
    await store.loadAll()
    await store.search('workbuddy')
    expect(store.searchResults[0]?.id).toBe('AllAgent.NET.WorkBuddy')
    expect(store.totalHits).toBe(12)
    expect(store.page).toBe(0)
    const first = requests.find(request => request.method === 'plugins.search')
    expect(first?.params).toMatchObject({ query: 'workbuddy', skip: 0, take: 10 })
    await store.search('workbuddy', 1)
    const second = requests.filter(request => request.method === 'plugins.search').at(-1)
    expect(second?.params).toMatchObject({ skip: 10, take: 10 })
    expect(store.page).toBe(1)
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

describe('tool catalog store', () => {
  beforeEach(() => {
    handle = request => {
      switch (request.method) {
        case 'tools.list': return reply(request, {
          errors: [],
          tools: [
            { name: 'execute_code', description: '运行代码', risk: 'execute', parametersJson: '{"type":"object"}', source: 'host', owner: '' },
            { name: 'sample_clock', description: '示例插件工具', risk: 'read', parametersJson: '{"type":"object"}', source: 'plugin', owner: 'AllAgent.NET.SamplePlugin' },
          ],
        })
        default: return reply(request, true)
      }
    }
  })

  it('loads host and plugin tools with source attribution', async () => {
    const { useToolsStore } = await import('../src/stores/tools')
    const store = useToolsStore()
    await store.loadAll()
    expect(store.available).toBe(true)
    expect(store.tools).toHaveLength(2)
    expect(store.tools[1]).toMatchObject({ name: 'sample_clock', source: 'plugin', owner: 'AllAgent.NET.SamplePlugin' })
    expect(store.errors).toEqual([])
  })

  it('surfaces host-side catalog errors without failing the page', async () => {
    handle = request => reply(request, { tools: [], errors: ['工具目录暂不可用：python 缺失'] })
    const { useToolsStore } = await import('../src/stores/tools')
    const store = useToolsStore()
    await store.loadAll()
    expect(store.available).toBe(true)
    expect(store.errors[0]).toContain('工具目录暂不可用')
  })
})

describe('command catalog store', () => {
  it('loads the terminal command list and hides on unavailability', async () => {
    handle = request => request.method === 'commands.list'
      ? reply(request, [
          { text: '/help', description: '打开命令面板' },
          { text: '/permissions', description: '切换权限模式', usage: '[restricted|ask|full|plan]' },
          { text: '/session', description: '选择或恢复会话', aliases: '/sessions /resume' },
        ])
      : reply(request, true)
    const { useCommandsStore } = await import('../src/stores/commands')
    const store = useCommandsStore()
    await store.loadAll()
    expect(store.available).toBe(true)
    expect(store.commands.map(item => item.text)).toEqual(['/help', '/permissions', '/session'])
    expect(store.commands[2]?.aliases).toBe('/sessions /resume')

    handle = request => reply(request, null, 'Unknown method: commands.list')
    vi.resetModules()
    host.chrome = { webview: { postMessage: (json: string) => handle(JSON.parse(json)) } }
    const { useCommandsStore: reload } = await import('../src/stores/commands')
    const fallback = reload()
    await fallback.loadAll()
    expect(fallback.available).toBe(false)
    expect(fallback.commands).toEqual([])
  })
})

describe('account login store', () => {
  beforeEach(() => {
    handle = request => {
      switch (request.method) {
        case 'auth.providers': return reply(request, [
          { id: 'codex', name: 'ChatGPT 账号', kind: 'oauth', loggedIn: false, account: '', message: '未登录', modelConnectionAvailable: true, pending: false },
          { id: 'openai', name: 'OpenAI', kind: 'apikey', loggedIn: false, account: '', message: '尚未配置', modelConnectionAvailable: true, providerId: 'openai' },
        ])
        case 'auth.login': return reply(request, true)
        case 'auth.logout': return reply(request, { id: 'codex', name: 'ChatGPT 账号', kind: 'oauth', loggedIn: false, account: '', message: '', modelConnectionAvailable: true })
        default: return reply(request, true)
      }
    }
  })

  it('lists login providers with status and starts browser login', async () => {
    const requests: Request[] = []
    const base = handle
    handle = request => { requests.push(request); return base(request) }
    const { useAuthStore } = await import('../src/stores/auth')
    const store = useAuthStore()
    await store.loadAll()
    expect(store.available).toBe(true)
    expect(store.providers).toHaveLength(2)
    expect(store.providers[0]).toMatchObject({ id: 'codex', kind: 'oauth', loggedIn: false })
    await store.login('codex')
    const login = requests.find(request => request.method === 'auth.login')
    expect(login?.params).toMatchObject({ id: 'codex', newAccount: false })
    await store.logout('codex')
    expect(store.busyId).toBeNull()
  })

  it('refreshes the provider list when the host reports a settled login', async () => {
    let calls = 0
    handle = request => {
      if (request.method === 'auth.providers') {
        calls += 1
        return reply(request, [{ id: 'codex', name: 'ChatGPT 账号', kind: 'oauth', loggedIn: calls > 1, account: calls > 1 ? 'me@example.com' : '', message: '', modelConnectionAvailable: true }])
      }
      return reply(request, true)
    }
    const { useAuthStore } = await import('../src/stores/auth')
    const store = useAuthStore()
    await store.loadAll()
    host.__bridgeCallback(JSON.stringify({ type: 'auth.changed', id: 'codex', error: null }))
    await vi.waitFor(() => expect(store.providers[0]?.loggedIn).toBe(true))
    expect(store.providers[0]?.account).toBe('me@example.com')
  })
})
