import mitt from 'mitt'

// Bridge event types pushed from C# backend
export type BridgeEvents = {
  'agent.token': { text: string; streamId: string }
  'agent.thinking': { text: string; streamId: string }
  'agent.toolCallStarted': { id: string; name: string; args: string; parentId?: string; streamId: string }
  'agent.toolCallOutput': { id: string; line: string; isError: boolean; streamId: string }
  'agent.toolCallCompleted': { id: string; name: string; result: string; streamId: string }
  'agent.done': { reason: string; streamId: string }
  'agent.error': { message: string; streamId: string }
}

export const bridgeEvents = mitt<BridgeEvents>()

interface BridgeRequest {
  id: string
  method: string
  params?: unknown
}

interface BridgeResponse {
  id: string
  type?: 'response' | 'event'
  eventName?: string
  streamId?: string
  data?: unknown
  result?: unknown
  error?: string
}

const pending = new Map<string, (data: unknown, error?: string) => void>()

// Receive messages from C# (via ExecuteScriptAsync calling __bridgeCallback)
;(window as any).__bridgeCallback = (json: string) => {
  try {
    const msg = JSON.parse(json) as BridgeResponse
    if (msg.type === 'event' && msg.eventName) {
      // Merge top-level streamId into data so listeners get everything in one object
      const eventData = { ...(msg.data as any), streamId: msg.streamId }
      bridgeEvents.emit(msg.eventName as keyof BridgeEvents, eventData as any)
    } else if (msg.id) {
      const resolve = pending.get(msg.id)
      if (resolve) {
        resolve(msg.result ?? msg.data, msg.error)
        pending.delete(msg.id)
      }
    }
  } catch (e) {
    console.error('Bridge message parse error:', e)
  }
}

// Also wire up the event bus for the injected bridge shim
;(window as any).__bridgeEventBus = (msg: BridgeResponse) => {
  if (msg.type === 'event' && msg.eventName) {
    const eventData = { ...(msg.data as any), streamId: msg.streamId }
    bridgeEvents.emit(msg.eventName as keyof BridgeEvents, eventData as any)
  }
}

function postToNative(json: string) {
  // WebView2 (Windows)
  if ((window as any).chrome?.webview) {
    ;(window as any).chrome.webview.postMessage(json)
    return true
  }
  // WebKit (macOS/Linux)
  if ((window as any).webkit?.messageHandlers?.bridge) {
    ;(window as any).webkit.messageHandlers.bridge.postMessage(json)
    return true
  }
  return false
}

export async function invoke<T = unknown>(method: string, params?: unknown): Promise<T> {
  return new Promise((resolve, reject) => {
    const id = crypto.randomUUID()
    pending.set(id, (data, error) => {
      if (error) reject(new Error(error))
      else resolve(data as T)
    })

    const request: BridgeRequest = { id, method, params }
    const json = JSON.stringify(request)

    console.log(`[Bridge] Invoke: ${method} (id=${id})`)

    if (!postToNative(json)) {
      // Dev mode fallback: mock responses
      console.warn(`[Bridge] Native bridge not available, method: ${method}`, params)
      pending.delete(id)
      resolve(getMockResponse(method, params) as T)
    }
  })
}

// Mock data for development without the C# backend
function getMockResponse(method: string, _params?: unknown): unknown {
  switch (method) {
    case 'conversations.list': return []
    case 'conversations.create': return { id: crypto.randomUUID(), title: 'New Conversation', modelId: '', providerId: '', createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() }
    case 'conversations.getMessages': return []
    case 'skills.list': return []
    case 'apiConfigs.list': return []
    case 'settings.get': return { theme: 'system', language: 'zh', defaultModel: '', streamingEnabled: true, defaultProviderId: '' }
    default: return null
  }
}
