import { bridgeEvents, invoke } from '@/bridge'
import { invokeHostApi } from '@/lib/hostApi'
import type { PluginWebInfo } from '@/types'

// The host side of the frame protocol: one sandboxed iframe per plugin view, loaded from the
// plugin asset origin. Every message is validated (source identity + origin + handshake token),
// RPC is limited to the plugin's own bridge namespace and the capability-gated host API, and
// plugin backend pushes arrive as plugin.<packageId>.<event> bridge events.

export const FRAME_PROTOCOL = 1
export const FRAME_SDK_VERSION = 1

export interface FrameView {
  kind: 'page' | 'panel' | 'toolcard' | 'settings'
  id: string
  params?: Record<string, unknown>
}

export interface FrameContext {
  workspace?: string
  conversationId?: string | null
}

export interface HostTheme {
  mode: 'light' | 'dark'
  vars: Record<string, string>
}

export interface FrameTarget {
  src: string
  token: string
  /** null for passive targets (data: URLs used by the dev-preview mock). */
  origin: string | null
}

export interface FrameOptions {
  plugin: PluginWebInfo
  view: FrameView
  entry?: string
  assetBaseUrl?: string | null
  context?: FrameContext
  onResize?: (height: number) => void
  onReady?: () => void
}

export interface FrameHandle {
  readonly token: string
  post(type: string, payload?: unknown): void
  event(name: string, data: unknown): void
  setContext(context: FrameContext): void
  sendTheme(): void
  dispose(): void
}

interface FrameEnvelope {
  allagent: number
  type: string
  payload?: unknown
}

interface FrameRecord {
  element: HTMLIFrameElement
  plugin: PluginWebInfo
  view: FrameView
  token: string
  origin: string
  authed: boolean
  subscriptions: Set<string>
  onResize?: (height: number) => void
  onReady?: () => void
  context: FrameContext
}

const THEME_VARS = [
  '--background', '--foreground', '--surface', '--muted', '--muted-foreground',
  '--border', '--accent', '--brand', '--destructive', '--ring-color', '--sidebar',
]

const frames = new Set<FrameRecord>()
let listenersInstalled = false

export function themeSnapshot(): HostTheme {
  const mode = document.documentElement.classList.contains('dark') ? 'dark' : 'light'
  const style = getComputedStyle(document.documentElement)
  const vars: Record<string, string> = {}
  for (const name of THEME_VARS) {
    const value = style.getPropertyValue(name).trim()
    if (value) vars[name] = value
  }
  return { mode, vars }
}

function randomId(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `id-${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`
}

/** Builds the frame URL (or returns null when the host serves no assets) plus its handshake token. */
export function createFrameTarget(options: FrameOptions): FrameTarget | null {
  const { plugin, entry, assetBaseUrl } = options
  const token = randomId()
  const base = plugin.devUrl ?? assetBaseUrl
  if (!base) {
    // The dev-preview mock loads a self-contained data: page; no host connection is possible.
    return entry?.startsWith('data:') ? { src: entry, token, origin: null } : null
  }
  let url: URL
  try { url = new URL(base) } catch { return null }
  if (!plugin.devUrl) {
    if (!entry) return null
    const path = entry.split('/').filter(Boolean).map(encodeURIComponent).join('/')
    url = new URL(`/p/${encodeURIComponent(plugin.packageId)}/${path}`, base)
  }
  url.searchParams.set('token', token)
  url.searchParams.set('parent', window.location.origin)
  url.searchParams.set('theme', document.documentElement.classList.contains('dark') ? 'dark' : 'light')
  return { src: url.toString(), token, origin: new URL(base).origin }
}

export function registerFrame(element: HTMLIFrameElement, options: FrameOptions, target: FrameTarget): FrameHandle {
  ensureListeners()
  const record: FrameRecord = {
    element,
    plugin: options.plugin,
    view: options.view,
    token: target.token,
    origin: target.origin ?? '',
    authed: false,
    subscriptions: new Set(options.view.kind === 'toolcard' ? ['tool.update'] : []),
    onResize: options.onResize,
    onReady: options.onReady,
    context: options.context ?? {},
  }
  frames.add(record)
  return {
    token: record.token,
    post: (type, payload) => send(record, { allagent: FRAME_PROTOCOL, type, payload }),
    event: (name, data) => send(record, { allagent: FRAME_PROTOCOL, type: 'event', payload: { name, data } }),
    setContext: context => {
      const previous = record.context
      record.context = { ...record.context, ...context }
      send(record, { allagent: FRAME_PROTOCOL, type: 'context', payload: record.context })
      if (context.conversationId !== undefined && context.conversationId !== previous.conversationId && record.subscriptions.has('conversation.changed')) {
        send(record, { allagent: FRAME_PROTOCOL, type: 'event', payload: { name: 'conversation.changed', data: record.context } })
      }
    },
    sendTheme: () => send(record, { allagent: FRAME_PROTOCOL, type: 'theme', payload: themeSnapshot() }),
    dispose: () => { frames.delete(record) },
  }
}

function send(record: FrameRecord, envelope: FrameEnvelope) {
  try { record.element.contentWindow?.postMessage(envelope, record.origin || '*') } catch { /* frame is gone */ }
}

function initPayload(record: FrameRecord) {
  return {
    sdkVersion: FRAME_SDK_VERSION,
    plugin: { packageId: record.plugin.packageId, name: record.plugin.name, version: record.plugin.version },
    view: record.view,
    theme: themeSnapshot(),
    context: record.context,
    capabilities: record.plugin.capabilities ?? [],
  }
}

function ensureListeners() {
  if (listenersInstalled) return
  listenersInstalled = true
  window.addEventListener('message', onMessage)
  // Plugin backend pushes are broadcast as plugin.<packageId>.<event>; the wildcard listener
  // catches every event name because plugin event names are open-ended.
  bridgeEvents.on('*', (type, event) => {
    if (typeof type !== 'string') return
    const lower = type.toLowerCase()
    if (lower.startsWith('plugin.')) {
      // Package ids contain dots, so match each frame's own namespace prefix instead of
      // splitting the event name at the first separator.
      for (const record of frames) {
        const prefix = `plugin.${record.plugin.packageId.toLowerCase()}.`
        if (!lower.startsWith(prefix)) continue
        const name = type.slice(prefix.length)
        if (record.subscriptions.has(name)) {
          send(record, { allagent: FRAME_PROTOCOL, type: 'event', payload: { name, data: event } })
        }
      }
      return
    }
    if (type === 'workspace.changed') {
      for (const record of frames) {
        if (record.subscriptions.has('workspace.changed')) {
          send(record, { allagent: FRAME_PROTOCOL, type: 'event', payload: { name: 'workspace.changed', data: event } })
        }
      }
    }
  })
}

function onMessage(event: MessageEvent) {
  let record: FrameRecord | undefined
  for (const candidate of frames) {
    if (candidate.element.contentWindow === event.source) { record = candidate; break }
  }
  if (!record) return
  if (record.origin && event.origin !== record.origin) return
  const data = event.data as FrameEnvelope | undefined
  if (!data || typeof data !== 'object' || data.allagent !== FRAME_PROTOCOL || typeof data.type !== 'string') return
  if (!record.authed) {
    if (data.type !== 'hello') return
    const hello = (data.payload ?? {}) as { token?: string }
    if (hello.token !== record.token) return
    record.authed = true
    record.onReady?.()
    send(record, { allagent: FRAME_PROTOCOL, type: 'init', payload: initPayload(record) })
    return
  }
  switch (data.type) {
    case 'invoke': {
      const payload = (data.payload ?? {}) as { id?: string; method?: string; params?: unknown }
      if (typeof payload.id !== 'string' || typeof payload.method !== 'string') return
      void handleInvoke(record, payload.id, payload.method, payload.params)
      break
    }
    case 'subscribe': {
      const names = ((data.payload ?? {}) as { names?: unknown }).names
      if (Array.isArray(names)) for (const name of names) if (typeof name === 'string' && name) record.subscriptions.add(name)
      break
    }
    case 'unsubscribe': {
      const names = ((data.payload ?? {}) as { names?: unknown }).names
      if (Array.isArray(names)) for (const name of names) if (typeof name === 'string') record.subscriptions.delete(name)
      break
    }
    case 'resize': {
      const height = Number(((data.payload ?? {}) as { height?: unknown }).height)
      if (Number.isFinite(height) && height > 0) record.onResize?.(height)
      break
    }
    default:
      break
  }
}

async function handleInvoke(record: FrameRecord, id: string, method: string, params: unknown) {
  try {
    const result = await route(record, method, params)
    send(record, { allagent: FRAME_PROTOCOL, type: 'invokeResult', payload: { id, result: result ?? null } })
  } catch (error) {
    const code = (error as { code?: string } | undefined)?.code ?? 'failed'
    const message = error instanceof Error ? error.message : String(error)
    send(record, { allagent: FRAME_PROTOCOL, type: 'invokeResult', payload: { id, error: { code, message } } })
  }
}

function route(record: FrameRecord, method: string, params: unknown): Promise<unknown> {
  const own = `plugin.${record.plugin.packageId.toLowerCase()}.`
  if (method.toLowerCase().startsWith(own)) return invoke(method, params)
  if (method.startsWith('host.')) return invokeHostApi(record.plugin, method, params)
  return Promise.reject(Object.assign(new Error('未允许的调用：' + method), { code: 'denied' }))
}
