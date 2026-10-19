import mitt from 'mitt'
import type { ContextUsage, RunningConversation } from '@/types'
import { createMockTransport } from './mock'

// Events pushed from the C# backend. Every agent event carries the streamId of
// the run that produced it so late events from a cancelled stream are ignored.
type BridgePayloads = {
  'agent.token': { text: string; streamId: string }
  'agent.thinking': { text: string; streamId: string }
  'agent.iteration': { current: number; maximum: number; toolCalls: number; streamId: string }
  'agent.toolCallStarted': { id: string; name: string; args: string; parentId?: string; streamId: string }
  'agent.toolCallOutput': { id: string; line: string; isError: boolean; streamId: string }
  'agent.toolCallCompleted': { id: string; name: string; result: string; streamId: string }
  'agent.toolCallRejected': { id: string; tool: string; reason: string; message: string; streamId: string }
  'agent.done': { reason: string; streamId: string }
  'agent.error': { message: string; streamId: string }
  'agent.notice': { message: string; resetResponse: boolean; streamId: string }
  'agent.contextUsage': ContextUsage & { streamId: string }
  'agent.approvalRequest': { requestId: string; conversationId?: string; name: string; risk: string; args: string; source?: string | null; requiresExplicitApproval?: boolean; streamId?: string }
  'agent.question': { id: string; conversationId?: string; question: string; choices: string[]; multiple: boolean; streamId?: string }
  'interactions.resolved': { id: string }
  'workspace.changed': { workspace: string; dataDirectory: string }
  'workspace.folderPicked': { path: string | null; error?: string | null }
  'tasks.updated': { conversationId: string }
  // Sessions with a run in flight, across every workspace the host has open.
  'runs.changed': { conversations: RunningConversation[] }
  'auth.changed': { id: string; error?: string | null }
  // Plugin web extensions changed (install / enable / web toggle): reload the contribution snapshot.
  'web.contributions.changed': Record<string, never>
  // Integrated terminal: base64 output frames with a per-session sequence, exit notices and
  // a tab-list change push (create/close/exit). streamId carries the terminal session id.
  'terminal.output': { base64: string; streamId: string }
  'terminal.exit': { exitCode: number | null; streamId: string }
  'terminal.changed': Record<string, never>
  // Git panel: line-streamed network operations (fetch/pull/push) and their completion.
  'git.output': { jobId: string; text: string; error: boolean }
  'git.done': { jobId: string; label: string; ok: boolean; message: string }
}
export type BridgeEvents = { [K in keyof BridgePayloads]: BridgePayloads[K] & { sequence?: number; timestamp?: string } }

export const bridgeEvents = mitt<BridgeEvents>()

export type ConnectionState = 'native' | 'connected' | 'connecting' | 'preview' | 'offline'

interface BridgeRequest {
  id: string
  method: string
  params?: unknown
}

interface BridgeResponse {
  id?: string
  type?: 'response' | 'event' | 'tasks.updated' | 'workspace.changed' | 'workspace.folderPicked' | 'runs.changed' | 'auth.changed' | 'web.contributions.changed'
  eventName?: string
  streamId?: string
  sequence?: number
  timestamp?: string
  conversationId?: string
  conversations?: RunningConversation[]
  workspace?: string
  dataDirectory?: string
  path?: string | null
  data?: unknown
  result?: unknown
  error?: string
  requestId?: string
}

interface Transport {
  kind: 'webview' | 'websocket' | 'mock'
  send(json: string): boolean
}

const pending = new Map<string, (data: unknown, error?: string) => void>()
let connection: ConnectionState = 'offline'
const connectionListeners = new Set<(state: ConnectionState) => void>()

function setConnection(next: ConnectionState) {
  if (connection === next) return
  connection = next
  connectionListeners.forEach(listener => listener(next))
}

export function connectionState(): ConnectionState {
  return connection
}

export function onConnectionChange(listener: (state: ConnectionState) => void): () => void {
  connectionListeners.add(listener)
  return () => { connectionListeners.delete(listener) }
}

// Fired when the socket comes back after a drop (backend restart, workspace
// switch, …) so the UI can reload everything the new backend serves.
const reconnectListeners = new Set<() => void>()
let connectedOnce = false
let needsRecovery = false

export function onReconnect(listener: () => void): () => void {
  reconnectListeners.add(listener)
  return () => { reconnectListeners.delete(listener) }
}

export function isNativeHost(): boolean {
  return connection === 'native'
}

function dispatch(json: string) {
  let msg: BridgeResponse
  try {
    msg = JSON.parse(json) as BridgeResponse
  } catch (error) {
    console.error('Bridge message parse error:', error)
    return
  }
  if (!msg || typeof msg !== 'object') return
  // The interaction broker also sends top-level notifications, without the
  // streaming event envelope. They must never be treated as RPC responses.
  const notification = msg as unknown as Record<string, unknown>
  if (notification.type === 'agent.approvalRequest' || notification.type === 'agent.question' || notification.type === 'interactions.resolved') {
    bridgeEvents.emit(notification.type, notification as never)
    return
  }
  if (msg.type === 'tasks.updated') {
    bridgeEvents.emit('tasks.updated', { conversationId: String(msg.conversationId ?? '') })
    return
  }
  if (msg.type === 'runs.changed') {
    bridgeEvents.emit('runs.changed', { conversations: Array.isArray(msg.conversations) ? msg.conversations : [] })
    return
  }
  if (msg.type === 'workspace.changed') {
    bridgeEvents.emit('workspace.changed', { workspace: String(msg.workspace ?? ''), dataDirectory: String(msg.dataDirectory ?? '') })
    return
  }
  if (msg.type === 'workspace.folderPicked') {
    bridgeEvents.emit('workspace.folderPicked', {
      path: msg.path ? String(msg.path) : null,
      error: msg.error ? String(msg.error) : null,
    })
    return
  }
  if (msg.type === 'auth.changed') {
    // Login flows settle outside the RPC that started them; the result arrives as this notification.
    bridgeEvents.emit('auth.changed', { id: String(msg.id ?? ''), error: msg.error ? String(msg.error) : null })
    return
  }
  if (msg.type === 'web.contributions.changed') {
    bridgeEvents.emit('web.contributions.changed', {})
    return
  }
  if (msg.type === 'event' && msg.eventName) {
    const eventData = { ...(msg.data as Record<string, unknown>), streamId: msg.streamId, sequence: msg.sequence, timestamp: msg.timestamp }
    bridgeEvents.emit(msg.eventName as keyof BridgeEvents, eventData as never)
    return
  }
  if (msg.id) {
    const settle = pending.get(msg.id)
    if (settle) {
      pending.delete(msg.id)
      settle(msg.result ?? msg.data, msg.error)
    }
  }
}

// The native WebView hosts call this global when pushing responses and events.
;(window as unknown as { __bridgeCallback: (json: string) => void }).__bridgeCallback = dispatch

function webViewTransport(): Transport | null {
  const host = window as {
    chrome?: { webview?: { postMessage(json: string): void } }
    webkit?: { messageHandlers?: { bridge?: { postMessage(json: string): void } } }
  }
  if (host.chrome?.webview?.postMessage) {
    const webview = host.chrome.webview
    return { kind: 'webview', send: json => { webview.postMessage(json); return true } }
  }
  if (host.webkit?.messageHandlers?.bridge?.postMessage) {
    const bridge = host.webkit.messageHandlers.bridge
    return { kind: 'webview', send: json => { bridge.postMessage(json); return true } }
  }
  return null
}

function bridgeSocketTarget(): { url: string; explicit: boolean } | null {
  const override = new URLSearchParams(window.location.search).get('bridge')
  if (override) return { url: override, explicit: true }
  if (window.location.protocol === 'http:' || window.location.protocol === 'https:') {
    const scheme = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
    return { url: `${scheme}//${window.location.host}/bridge`, explicit: false }
  }
  return null
}

let socket: WebSocket | null = null
let socketQueue: string[] = []
let reconnectAttempt = 0
let reconnectTimer: number | undefined
let socketTarget: { url: string; explicit: boolean } | null = null
let keepReconnecting = true

const wsTransport: Transport = {
  kind: 'websocket',
  send(json) {
    if (socket?.readyState === WebSocket.OPEN) {
      socket.send(json)
      return true
    }
    if (socket?.readyState === WebSocket.CONNECTING) {
      socketQueue.push(json)
      return true
    }
    return false
  },
}

function rejectPending(message: string) {
  const entries = [...pending.entries()]
  pending.clear()
  entries.forEach(([, settle]) => settle(undefined, message))
}

function connectSocket(url: string) {
  setConnection('connecting')
  let ws: WebSocket
  try { ws = new WebSocket(url) } catch {
    socketQueue = []
    rejectPending('桥接地址无效，无法连接本地后端。')
    setConnection('offline')
    return
  }
  socket = ws
  ws.onopen = () => {
    if (socket !== ws) return
    reconnectAttempt = 0
    setConnection('connected')
    const queued = socketQueue
    socketQueue = []
    queued.forEach(json => ws.send(json))
    if (connectedOnce || needsRecovery) reconnectListeners.forEach(listener => listener())
    needsRecovery = false
    connectedOnce = true
  }
  ws.onmessage = event => {
    if (socket !== ws) return
    if (typeof event.data === 'string') dispatch(event.data)
  }
  ws.onclose = () => {
    if (socket !== ws) return
    socket = null
    socketQueue = []
    needsRecovery = true
    rejectPending('与本地后端的连接已断开，正在重连。')
    if (!keepReconnecting || !socketTarget) {
      setConnection('offline')
      return
    }
    reconnectAttempt += 1
    setConnection('connecting')
    window.clearTimeout(reconnectTimer)
    reconnectTimer = window.setTimeout(() => connectSocket(socketTarget!.url), Math.min(1000 * reconnectAttempt, 8000))
  }
}

export function reconnect() {
  if (!socketTarget || !keepReconnecting) return
  window.clearTimeout(reconnectTimer)
  reconnectAttempt = 0
  needsRecovery = true
  const previous = socket
  socket = null
  socketQueue = []
  rejectPending('连接正在重新建立，请重试操作。')
  try { previous?.close() } catch { /* ignore */ }
  connectSocket(socketTarget.url)
}

function initTransport(): Transport | null {
  // An explicit ?bridge= address always wins. Windows that wrap this page in WebView2 (the
  // WPF GUI host) expose window.chrome.webview, but the backend lives in the terminal
  // process, so such a page must talk to the loopback socket instead of the native bridge.
  const target = bridgeSocketTarget()
  if (target?.explicit) {
    socketTarget = target
    connectSocket(target.url)
    window.addEventListener('beforeunload', () => { keepReconnecting = false })
    return wsTransport
  }
  const webview = webViewTransport()
  if (webview) {
    setConnection('native')
    return webview
  }
  if (target && !import.meta.env.DEV) {
    socketTarget = target
    connectSocket(target.url)
    window.addEventListener('beforeunload', () => { keepReconnecting = false })
    return wsTransport
  }
  if (import.meta.env.DEV) {
    setConnection('preview')
    return mockTransport
  }
  setConnection('offline')
  return null
}

const mock = createMockTransport(dispatch)
const mockTransport: Transport = { kind: 'mock', send: () => false }

const transport: Transport | null = initTransport()

export async function invoke<T = unknown>(method: string, params?: unknown, timeoutMs = 60000): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const id = crypto.randomUUID()
    const timer = window.setTimeout(() => {
      pending.delete(id)
      socketQueue = socketQueue.filter(json => (JSON.parse(json) as BridgeRequest).id !== id)
      reject(new Error(`请求超时：${method}。请检查后端连接后刷新状态。`))
    }, timeoutMs)
    pending.set(id, (data, error) => {
      window.clearTimeout(timer)
      if (error) reject(new Error(error))
      else resolve(data as T)
    })
    const request: BridgeRequest = { id, method, params }
    try {
      if (transport?.send(JSON.stringify(request))) return
    } catch (error) {
      window.clearTimeout(timer)
      pending.delete(id)
      reject(error)
      return
    }
    window.clearTimeout(timer)
    pending.delete(id)
    if (transport?.kind === 'mock') {
      try { resolve(mock.call(method, params) as T) } catch (error) { reject(error) }
      return
    }
    reject(new Error('本地后端未连接。请在终端运行 allagent 后使用 /web-ui，或运行 allagent -guimode 打开原生窗口。'))
  })
}
