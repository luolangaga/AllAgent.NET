// AllAgent.NET plugin web SDK — the frame side of the host protocol.
//
// A plugin page loads this script (served by the host at /__sdk/allagent-plugin-sdk.js from the
// plugin asset origin), calls connect(), and talks to the host only through the returned client:
// invoke() reaches the plugin's own backend bridge methods (plugin.<packageId>.*) and the
// capability-gated host API; on() subscribes to host and plugin-backend events. The page runs in
// a sandboxed iframe on its own origin — it can never touch the host DOM or the /bridge socket.

export const PROTOCOL = 1
export const SDK_VERSION = 1

export interface HostTheme {
  mode: 'light' | 'dark'
  vars: Record<string, string>
}

export interface PluginView {
  kind: string
  id: string
  params?: Record<string, unknown>
}

export interface PluginContextInfo {
  workspace?: string
  conversationId?: string | null
  [key: string]: unknown
}

export interface PluginDescriptor {
  packageId: string
  name: string
  version: string
}

export interface InvokeError {
  code: string
  message: string
}

interface Envelope {
  allagent: number
  type: string
  id?: string
  payload?: unknown
}

export interface PluginClient {
  readonly plugin: PluginDescriptor
  readonly view: PluginView
  readonly capabilities: string[]
  readonly theme: HostTheme
  readonly context: PluginContextInfo
  /** Resolves when the host handshake completed. */
  readonly ready: Promise<void>
  invoke<T = unknown>(method: string, params?: unknown): Promise<T>
  on<T = unknown>(name: string, handler: (data: T) => void): () => void
  /** Reports the frame's content height so panels and tool cards can size themselves. */
  resize(height?: number): void
  /** Keeps the host informed of the frame height; returns a stop function. */
  autoResize(): () => void
  openExternal(url: string): void
}

export class PluginClientError extends Error {
  constructor(message: string, readonly code: string = 'failed') {
    super(message)
    this.name = 'PluginClientError'
  }
}

const HANDSHAKE_TIMEOUT_MS = 15000
const INVOKE_TIMEOUT_MS = 60000

function randomId(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `id-${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`
}

let connected: PluginClient | null = null

export function connect(): PluginClient {
  if (connected) return connected
  const query = new URLSearchParams(location.search)
  const token = query.get('token') ?? ''
  const parentOrigin = query.get('parent') || '*'
  const parent = window.parent

  const state = {
    plugin: { packageId: '', name: '', version: '' } as PluginDescriptor,
    view: { kind: 'page', id: '' } as PluginView,
    capabilities: [] as string[],
    theme: { mode: 'light', vars: {} } as HostTheme,
    context: {} as PluginContextInfo,
  }
  const handlers = new Map<string, Set<(data: unknown) => void>>()
  const pending = new Map<string, { resolve: (value: unknown) => void; reject: (error: Error) => void; timer: number }>()
  let initialized = false
  let helloTimer = 0
  let resolveReady!: () => void
  let rejectReady!: (error: Error) => void
  const ready = new Promise<void>((resolve, reject) => { resolveReady = resolve; rejectReady = reject })

  function send(type: string, payload?: unknown, id?: string) {
    try { parent.postMessage({ allagent: PROTOCOL, type, id, payload } satisfies Envelope, parentOrigin) }
    catch (error) { console.error('[AllAgentPlugin] postMessage failed:', error) }
  }

  function emit(name: string, data: unknown) {
    const set = handlers.get(name)
    if (set) for (const handler of [...set]) { try { handler(data) } catch (error) { console.error('[AllAgentPlugin] handler failed:', name, error) } }
  }

  function applyTheme(theme: HostTheme) {
    if (!theme) return
    const root = document.documentElement
    root.dataset.theme = theme.mode ?? 'light'
    root.style.colorScheme = theme.mode === 'dark' ? 'dark' : 'light'
    for (const [name, value] of Object.entries(theme.vars ?? {})) {
      const short = name.startsWith('--') ? name.slice(2) : name
      root.style.setProperty(`--ag-${short}`, value)
    }
  }

  window.addEventListener('message', (message: MessageEvent) => {
    if (parentOrigin !== '*' && message.origin !== parentOrigin) return
    if (message.source !== parent) return
    const data = message.data as Envelope | undefined
    if (!data || typeof data !== 'object' || data.allagent !== PROTOCOL) return
    switch (data.type) {
      case 'init': {
        const payload = data.payload as {
          plugin?: PluginDescriptor; view?: PluginView; capabilities?: string[]
          theme?: HostTheme; context?: PluginContextInfo
        }
        state.plugin = payload.plugin ?? state.plugin
        state.view = payload.view ?? state.view
        state.capabilities = payload.capabilities ?? []
        state.theme = payload.theme ?? state.theme
        state.context = payload.context ?? {}
        applyTheme(state.theme)
        initialized = true
        window.clearTimeout(helloTimer)
        helloTimer = 0
        resolveReady()
        break
      }
      case 'theme': {
        state.theme = (data.payload as HostTheme) ?? state.theme
        applyTheme(state.theme)
        emit('theme', state.theme)
        break
      }
      case 'context': {
        state.context = (data.payload as PluginContextInfo) ?? {}
        emit('context', state.context)
        break
      }
      case 'event': {
        const payload = (data.payload ?? {}) as { name?: string; data?: unknown }
        if (payload.name) emit(payload.name, payload.data)
        break
      }
      case 'invokeResult': {
        const payload = (data.payload ?? {}) as { id?: string; result?: unknown; error?: InvokeError }
        if (!payload.id) break
        const entry = pending.get(payload.id)
        if (!entry) break
        pending.delete(payload.id)
        window.clearTimeout(entry.timer)
        if (payload.error) entry.reject(new PluginClientError(payload.error.message, payload.error.code))
        else entry.resolve(payload.result)
        break
      }
      default:
        break
    }
  })

  window.setTimeout(() => {
    if (!initialized) {
      window.clearTimeout(helloTimer)
      rejectReady(new PluginClientError('与宿主握手超时；请确认页面通过插件清单的 web 入口加载。', 'handshake_timeout'))
    }
  }, HANDSHAKE_TIMEOUT_MS)

  // The host registers its message listener in a React effect, so the first hello can race
  // frame load. Repeat until init arrives; the host answers every valid hello idempotently.
  function hello() {
    if (initialized) return
    send('hello', { token, sdkVersion: SDK_VERSION })
    helloTimer = window.setTimeout(hello, 500)
  }
  hello()

  const client: PluginClient = {
    get plugin() { return state.plugin },
    get view() { return state.view },
    get capabilities() { return state.capabilities },
    get theme() { return state.theme },
    get context() { return state.context },
    ready,
    invoke<T = unknown>(method: string, params?: unknown): Promise<T> {
      return new Promise<T>((resolve, reject) => {
        const id = randomId()
        const packageId = state.plugin.packageId.toLowerCase()
        // Short names reach this plugin's own bridge namespace; host.* and an explicit
        // plugin.* method pass through unchanged.
        const qualified = method.startsWith('plugin.') || method.startsWith('host.') || !packageId
          ? method
          : `plugin.${packageId}.${method}`
        const timer = window.setTimeout(() => {
          pending.delete(id)
          reject(new PluginClientError(`调用超时：${method}`, 'timeout'))
        }, INVOKE_TIMEOUT_MS)
        pending.set(id, { resolve: value => resolve(value as T), reject, timer })
        send('invoke', { method: qualified, params }, id)
      })
    },
    on<T = unknown>(name: string, handler: (data: T) => void): () => void {
      let set = handlers.get(name)
      if (!set) {
        set = new Set()
        handlers.set(name, set)
        send('subscribe', { names: [name] })
      }
      const entry = handler as (data: unknown) => void
      set.add(entry)
      return () => {
        set?.delete(entry)
        if (set && set.size === 0) {
          handlers.delete(name)
          send('unsubscribe', { names: [name] })
        }
      }
    },
    resize(height?: number) {
      send('resize', { height: Math.ceil(height ?? document.documentElement.scrollHeight) })
    },
    autoResize(): () => void {
      let frame = 0
      const report = () => {
        window.cancelAnimationFrame(frame)
        frame = window.requestAnimationFrame(() => client.resize())
      }
      report()
      const observer = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(report) : null
      observer?.observe(document.documentElement)
      window.addEventListener('load', report)
      return () => {
        window.cancelAnimationFrame(frame)
        observer?.disconnect()
        window.removeEventListener('load', report)
      }
    },
    openExternal(url: string) {
      try { window.open(url, '_blank', 'noopener') } catch (error) { console.error('[AllAgentPlugin] openExternal failed:', error) }
    },
  }
  connected = client
  return client
}

/** Convenience for pages that only want the client after the handshake. */
export async function connectReady(): Promise<PluginClient> {
  const client = connect()
  await client.ready
  return client
}
