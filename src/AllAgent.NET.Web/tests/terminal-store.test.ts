import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params?: any }
const host = window as unknown as { chrome: { webview: { postMessage(json: string): void } }; __bridgeCallback(json: string): void }
const calls: Request[] = []
let sessions: any[] = []

const session = (id: string, overrides: Record<string, unknown> = {}) => ({
  id, title: 'Windows PowerShell', shellId: 'powershell', shellLabel: 'Windows PowerShell',
  cwd: 'C:\\demo', cols: 120, rows: 30, alive: true, exitCode: null, sequence: 0, ...overrides,
})

beforeEach(() => {
  vi.resetModules()
  calls.length = 0
  sessions = []
  host.chrome = { webview: { postMessage(json) {
    const request = JSON.parse(json) as Request
    calls.push(request)
    const reply = (data: unknown, error?: string) => host.__bridgeCallback(JSON.stringify({ id: request.id, data, error }))
    switch (request.method) {
      case 'terminal.shells': return reply([{ id: 'powershell', label: 'Windows PowerShell' }, { id: 'cmd', label: '命令提示符' }])
      case 'terminal.list': return reply(sessions)
      case 'terminal.create': {
        const created = session('term-1', { shellId: request.params?.shell ?? 'powershell' })
        sessions = [created]
        return reply(created)
      }
      case 'terminal.attach': return reply({ session: sessions[0], replay: '', sequence: 4 })
      case 'terminal.close': {
        sessions = sessions.filter(item => item.id !== request.params?.id)
        return reply(true)
      }
      default: return reply(true)
    }
  } } }
})
afterEach(() => { vi.unstubAllGlobals() })

describe('终端 store', () => {
  it('loads shells, creates a session and keeps it as the active tab', async () => {
    const store = (await import('../src/stores/terminal')).useTerminalStore()
    await store.load()
    expect(store.shells.map(shell => shell.id)).toEqual(['powershell', 'cmd'])
    expect(store.defaultShell).toBe('powershell')

    const created = await store.create({})
    expect(created.id).toBe('term-1')
    expect(store.sessions).toHaveLength(1)
    expect(store.activeId).toBe('term-1')
    expect(store.focusTick).toBeGreaterThan(0)
  })

  it('marks a session dead when terminal.exit arrives and drops it on close', async () => {
    const store = (await import('../src/stores/terminal')).useTerminalStore()
    await store.load()
    await store.create({})

    host.__bridgeCallback(JSON.stringify({ type: 'event', eventName: 'terminal.exit', streamId: 'term-1', data: { exitCode: 1 } }))
    expect(store.sessions[0].alive).toBe(false)
    expect(store.sessions[0].exitCode).toBe(1)

    await store.close('term-1')
    expect(store.sessions).toHaveLength(0)
    expect(store.activeId).toBeNull()
  })

  it('re-syncs the host-side tab list on terminal.changed', async () => {
    const store = (await import('../src/stores/terminal')).useTerminalStore()
    await store.load()
    expect(store.sessions).toHaveLength(0)

    sessions = [session('term-9'), session('term-8', { alive: false, exitCode: 0 })]
    host.__bridgeCallback(JSON.stringify({ type: 'event', eventName: 'terminal.changed', streamId: '', data: {} }))
    await new Promise(resolve => setTimeout(resolve, 30))

    expect(store.sessions.map(item => item.id)).toEqual(['term-9', 'term-8'])
    expect(store.sessions[1].alive).toBe(false)
  })
})
