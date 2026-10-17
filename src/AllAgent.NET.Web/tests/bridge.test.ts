import { beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params?: unknown }
type HostWindow = Window & {
  chrome?: { webview: { postMessage: (json: string) => void } }
  __bridgeCallback: (json: string) => void
}
const host = window as unknown as HostWindow
let handle: (request: Request) => void = () => {}

class FakeWebSocket {
  static OPEN = 1
  static CONNECTING = 0
  static instances: FakeWebSocket[] = []
  url: string
  readyState = FakeWebSocket.CONNECTING
  sent: string[] = []
  onopen: (() => void) | null = null
  onmessage: ((event: { data: string }) => void) | null = null
  onclose: (() => void) | null = null

  constructor(url: string) {
    this.url = url
    FakeWebSocket.instances.push(this)
  }
  send(json: string) { this.sent.push(json) }
  close() { this.readyState = 3; this.onclose?.() }
  open() { this.readyState = FakeWebSocket.OPEN; this.onopen?.() }
  deliver(json: string) { this.onmessage?.({ data: json }) }
}

beforeEach(() => {
  vi.resetModules()
  FakeWebSocket.instances = []
  window.history.replaceState({}, '', '/')
  handle = () => {}
  host.chrome = { webview: { postMessage: (json: string) => handle(JSON.parse(json)) } }
  vi.stubGlobal('WebSocket', FakeWebSocket)
})

describe('native WebView bridge', () => {
  it('delivers broker notifications even when a question has an id', async () => {
    const { bridgeEvents } = await import('../src/bridge')
    const approval = vi.fn()
    const question = vi.fn()
    const resolved = vi.fn()
    bridgeEvents.on('agent.approvalRequest', approval)
    bridgeEvents.on('agent.question', question)
    bridgeEvents.on('interactions.resolved', resolved)
    host.__bridgeCallback(JSON.stringify({ type: 'agent.approvalRequest', requestId: 'a', name: 'file_write', args: '{}' }))
    host.__bridgeCallback(JSON.stringify({ type: 'agent.question', id: 'q', question: '继续吗？', choices: [], multiple: true }))
    host.__bridgeCallback(JSON.stringify({ type: 'interactions.resolved', id: 'q' }))
    expect(approval).toHaveBeenCalledWith(expect.objectContaining({ requestId: 'a' }))
    expect(question).toHaveBeenCalledWith(expect.objectContaining({ id: 'q', multiple: true }))
    expect(resolved).toHaveBeenCalledWith(expect.objectContaining({ id: 'q' }))
    host.__bridgeCallback('null')
  })

  it('times out requests whose native response never arrives', async () => {
    vi.useFakeTimers()
    try {
      const { invoke } = await import('../src/bridge')
      const request = invoke('settings.get', undefined, 50)
      const assertion = expect(request).rejects.toThrow('请求超时')
      await vi.advanceTimersByTimeAsync(51)
      await assertion
    } finally { vi.useRealTimers() }
  })
  it('posts parameters to the host and propagates backend errors', async () => {
    const { invoke, isNativeHost, connectionState } = await import('../src/bridge')
    expect(isNativeHost()).toBe(true)
    expect(connectionState()).toBe('native')
    handle = request => {
      expect(request.params).toEqual({ id: 'config-1' })
      host.__bridgeCallback(JSON.stringify({ id: request.id, result: null, error: 'connection refused' }))
    }
    await expect(invoke('apiConfigs.test', { id: 'config-1' })).rejects.toThrow('connection refused')
  })

  it('routes pushed events to the event bus with the stream id merged', async () => {
    const { bridgeEvents } = await import('../src/bridge')
    const received: Array<{ text: string; streamId: string }> = []
    bridgeEvents.on('agent.token', data => received.push(data))
    host.__bridgeCallback(JSON.stringify({ type: 'event', eventName: 'agent.token', streamId: 'stream-7', data: { text: 'hi' } }))
    expect(received).toEqual([{ text: 'hi', streamId: 'stream-7' }])
  })
})

describe('WebSocket bridge', () => {
  it('reloads initial data if the first connection fails before ever opening', async () => {
    window.history.replaceState({}, '', '/?bridge=ws%3A%2F%2F127.0.0.1%3A4404%2Fbridge')
    delete host.chrome
    vi.useFakeTimers()
    try {
      const { onReconnect } = await import('../src/bridge')
      const reload = vi.fn()
      onReconnect(reload)
      FakeWebSocket.instances.at(-1)!.close()
      await vi.advanceTimersByTimeAsync(1001)
      FakeWebSocket.instances.at(-1)!.open()
      expect(reload).toHaveBeenCalledTimes(1)
    } finally { vi.useRealTimers() }
  })
  it('never replays a rejected queued mutation after reconnect', async () => {
    window.history.replaceState({}, '', '/?bridge=ws%3A%2F%2F127.0.0.1%3A4402%2Fbridge')
    delete host.chrome
    vi.useFakeTimers()
    try {
      const { invoke } = await import('../src/bridge')
      const first = FakeWebSocket.instances.at(-1)!
      const mutation = invoke('chat.startStream', { message: 'run once' })
      const assertion = expect(mutation).rejects.toThrow('连接已断开')
      first.close()
      await assertion
      await vi.advanceTimersByTimeAsync(1001)
      const second = FakeWebSocket.instances.at(-1)!
      second.open()
      expect(second.sent).toHaveLength(0)
    } finally { vi.useRealTimers() }
  })

  it('removes timed-out requests from the connection queue', async () => {
    window.history.replaceState({}, '', '/?bridge=ws%3A%2F%2F127.0.0.1%3A4403%2Fbridge')
    delete host.chrome
    vi.useFakeTimers()
    try {
      const { invoke } = await import('../src/bridge')
      const mutation = invoke('conversations.delete', { id: 'old' }, 30)
      const assertion = expect(mutation).rejects.toThrow('请求超时')
      await vi.advanceTimersByTimeAsync(31)
      await assertion
      const socket = FakeWebSocket.instances.at(-1)!
      socket.open()
      expect(socket.sent).toHaveLength(0)
    } finally { vi.useRealTimers() }
  })
  it('connects to the requested endpoint, queues while connecting, and resolves responses', async () => {
    window.history.replaceState({}, '', '/?bridge=ws%3A%2F%2F127.0.0.1%3A4399%2Fbridge')
    delete host.chrome
    const { invoke, connectionState } = await import('../src/bridge')
    expect(connectionState()).toBe('connecting')
    const socket = FakeWebSocket.instances.at(-1)!
    expect(socket.url).toBe('ws://127.0.0.1:4399/bridge')

    const pending = invoke<string[]>('conversations.list')
    expect(socket.sent).toHaveLength(0)
    socket.open()
    expect(connectionState()).toBe('connected')
    expect(socket.sent).toHaveLength(1)

    const request = JSON.parse(socket.sent[0]) as Request
    expect(request.method).toBe('conversations.list')
    socket.deliver(JSON.stringify({ id: request.id, type: 'response', result: ['conv'] }))
    await expect(pending).resolves.toEqual(['conv'])
  })

  it('rejects pending requests and retries once the socket drops', async () => {
    window.history.replaceState({}, '', '/?bridge=ws%3A%2F%2F127.0.0.1%3A4400%2Fbridge')
    delete host.chrome
    const { invoke, connectionState } = await import('../src/bridge')
    const socket = FakeWebSocket.instances.at(-1)!
    socket.open()
    const pending = invoke('conversations.list')
    socket.close()
    await expect(pending).rejects.toThrow('连接已断开')
    expect(connectionState()).toBe('connecting')
  })

  it('notifies listeners when the socket reconnects after a drop', async () => {
    window.history.replaceState({}, '', '/?bridge=ws%3A%2F%2F127.0.0.1%3A4401%2Fbridge')
    delete host.chrome
    vi.useFakeTimers()
    const { onReconnect } = await import('../src/bridge')
    const reconnects: number[] = []
    onReconnect(() => reconnects.push(1))
    const first = FakeWebSocket.instances.at(-1)!
    first.open()
    expect(reconnects).toHaveLength(0)
    first.close()
    await vi.advanceTimersByTimeAsync(1500)
    const second = FakeWebSocket.instances.at(-1)!
    expect(second).not.toBe(first)
    second.open()
    expect(reconnects).toHaveLength(1)
    vi.useRealTimers()
  })
})
