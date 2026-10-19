import { beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params: any }
type HostWindow = Window & { chrome?: { webview: { postMessage: (json: string) => void } }; __bridgeCallback: (json: string) => void }
const host = window as unknown as HostWindow
let handle: (request: Request) => void
const reply = (request: Request, result: unknown, error?: string) => host.__bridgeCallback(JSON.stringify({ id: request.id, result, error }))

const status = (modules: unknown[] = [], workerAvailable = true) => ({
  workerAvailable,
  workerPath: 'C:\\app\\AllAgent.NET.PowerShell.exe',
  root: 'C:\\Users\\demo\\AppData\\Roaming\\AllAgent.NET\\powershell\\Modules',
  modules,
})

beforeEach(() => {
  vi.resetModules()
  host.chrome = { webview: { postMessage: (json: string) => handle(JSON.parse(json)) } }
  handle = request => {
    switch (request.method) {
      case 'powershell.status': return reply(request, status([]))
      default: return reply(request, true)
    }
  }
})

describe('PowerShell 能力商店', () => {
  it('loads the worker path and the verified module catalog', async () => {
    handle = request => request.method === 'powershell.status'
      ? reply(request, status([
          { name: 'Pester', version: '5.7.1', commandCount: 12, error: null },
          { name: 'Contoso.Broken', version: '1.0.4', commandCount: 0, error: '无法加载模块' },
        ]))
      : reply(request, true)
    const { usePowerShellStore } = await import('../src/stores/powershell')
    const store = usePowerShellStore()
    await store.load()
    expect(store.available).toBe(true)
    expect(store.loaded).toBe(true)
    expect(store.workerAvailable).toBe(true)
    expect(store.workerPath).toContain('AllAgent.NET.PowerShell.exe')
    expect(store.root).toContain('powershell\\Modules')
    expect(store.modules.map(module => module.name)).toEqual(['Pester', 'Contoso.Broken'])
    // An import failure is reported per module instead of hiding the entry.
    expect(store.modules[1]?.error).toContain('无法加载模块')
  })

  it('marks component management unavailable when the host does not serve it', async () => {
    handle = request => reply(request, null, 'Unknown method: powershell.status')
    const { usePowerShellStore } = await import('../src/stores/powershell')
    const store = usePowerShellStore()
    await store.load()
    expect(store.available).toBe(false)
    expect(store.loaded).toBe(true)
  })

  it('reports a missing embedded SDK worker without failing the page', async () => {
    handle = request => request.method === 'powershell.status'
      ? reply(request, status([], false))
      : reply(request, true)
    const { usePowerShellStore } = await import('../src/stores/powershell')
    const store = usePowerShellStore()
    await store.load()
    expect(store.available).toBe(true)
    expect(store.workerAvailable).toBe(false)
  })

  it('searches PowerShell Gallery and installs an exact version', async () => {
    const requests: Request[] = []
    handle = request => {
      requests.push(request)
      switch (request.method) {
        case 'powershell.search': return reply(request, [
          { name: 'Pester', version: '5.7.1', author: 'Pester Team', description: '测试框架' },
        ])
        case 'powershell.install': return reply(request, status([{ name: 'Pester', version: '5.7.1', commandCount: 12, error: null }]))
        default: return reply(request, status([]))
      }
    }
    const { usePowerShellStore } = await import('../src/stores/powershell')
    const store = usePowerShellStore()
    await store.load()
    await store.search('Pester')
    expect(store.searched).toBe(true)
    expect(store.packages[0]).toMatchObject({ name: 'Pester', version: '5.7.1', author: 'Pester Team' })
    expect(requests.find(request => request.method === 'powershell.search')?.params).toMatchObject({ query: 'Pester' })

    await store.install('Pester', '5.7.1')
    expect(store.installing).toBe(false)
    expect(store.modules).toHaveLength(1)
    expect(requests.find(request => request.method === 'powershell.install')?.params).toMatchObject({ name: 'Pester', version: '5.7.1' })
  })

  it('installs the current stable version when no version is given', async () => {
    const requests: Request[] = []
    handle = request => {
      requests.push(request)
      return reply(request, request.method === 'powershell.install' ? status([{ name: 'PSReadLine', version: '2.3.6', commandCount: 9, error: null }]) : status([]))
    }
    const { usePowerShellStore } = await import('../src/stores/powershell')
    const store = usePowerShellStore()
    await store.install('PSReadLine', '')
    expect(requests[0]?.params).toMatchObject({ name: 'PSReadLine', version: null })
    expect(store.modules[0]?.version).toBe('2.3.6')
  })

  it('surfaces an install failure and keeps the previous catalog', async () => {
    handle = request => request.method === 'powershell.install'
      ? reply(request, null, '模块已下载，但导入验证失败：Contoso.Broken 无法在当前 PowerShell 7 运行时中导入，或缺少依赖。')
      : reply(request, status([{ name: 'Pester', version: '5.7.1', commandCount: 12, error: null }]))
    const { usePowerShellStore } = await import('../src/stores/powershell')
    const store = usePowerShellStore()
    await store.load()
    await expect(store.install('Contoso.Broken', '1.0.4')).rejects.toThrow('导入验证失败')
    expect(store.installing).toBe(false)
    expect(store.modules.map(module => module.name)).toEqual(['Pester'])
  })

  it('reads the exported commands of one module and reports an unknown module', async () => {
    handle = request => {
      if (request.method !== 'powershell.commands') return reply(request, status([]))
      if ((request.params as { name: string }).name === 'Pester')
        return reply(request, { name: 'Pester', version: '5.7.1', total: 12, commands: ['Invoke-Pester', 'New-PesterConfiguration'], error: null })
      return reply(request, null, '模块不在能力清单中：Contoso。请先安装它，或刷新清单。')
    }
    const { usePowerShellStore } = await import('../src/stores/powershell')
    const store = usePowerShellStore()
    await store.readCommands('Pester', '5.7.1')
    expect(store.commands?.commands).toEqual(['Invoke-Pester', 'New-PesterConfiguration'])
    expect(store.commands?.total).toBe(12)
    expect(store.commandsBusy).toBe(false)
    // A module outside the verified catalog reports inline in the modal instead of a bare spinner.
    await store.readCommands('Contoso', '')
    expect(store.commands).toBeNull()
    expect(store.commandsError).toContain('能力清单')
    expect(store.commandsBusy).toBe(false)
  })

  it('refreshes the catalog after the terminal installs a module', async () => {
    let modules: unknown[] = []
    handle = request => request.method === 'powershell.refresh'
      ? reply(request, status(modules))
      : reply(request, status([]))
    const { usePowerShellStore } = await import('../src/stores/powershell')
    const store = usePowerShellStore()
    await store.load()
    expect(store.modules).toEqual([])
    // The module directory is shared with the terminal's /powershell panel.
    modules = [{ name: 'Az', version: '12.0.0', commandCount: 480, error: null }]
    await store.refresh()
    expect(store.modules[0]?.name).toBe('Az')
    expect(store.loading).toBe(false)
  })
})
