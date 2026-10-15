import { beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params: any }
type HostWindow = Window & { chrome?: { webview: { postMessage: (json: string) => void } }; __bridgeCallback: (json: string) => void }
const host = window as unknown as HostWindow
let handle: (request: Request) => void
const reply = (request: Request, result: unknown, error?: string) => host.__bridgeCallback(JSON.stringify({ id: request.id, result, error }))
const event = (eventName: string, data: unknown, streamId = 'stream-1') => host.__bridgeCallback(JSON.stringify({ type: 'event', eventName, data, streamId }))
beforeEach(() => {
  vi.resetModules()
  host.chrome = { webview: { postMessage: json => handle(JSON.parse(json)) } }
  handle = request => {
    if (request.method === 'conversations.create') reply(request, { id: 'conv-1', title: 'New Conversation' })
    else if (request.method === 'chat.startStream') reply(request, 'stream-1')
    else reply(request, true)
  }
})
describe('native bridge contract', () => {
  it('posts parameters to native and propagates backend errors', async () => {
    const { invoke, isNativeHost } = await import('../src/bridge')
    expect(isNativeHost()).toBe(true)
    handle = request => { expect(request.params).toEqual({ id: 'config-1' }); reply(request, null, 'connection refused') }
    await expect(invoke('apiConfigs.test', { id: 'config-1' })).rejects.toThrow('connection refused')
  })
  it('replays events received before the stream response', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    handle = request => {
      if (request.method === 'conversations.create') reply(request, { id: 'conv-1', title: 'New Conversation' })
      else { expect(request.params).toMatchObject({ conversationId: 'conv-1', apiConfigId: 'config-1', thinkingEnabled: true, thinkingBudget: 4000 }); event('agent.token', { text: 'Hello' }); event('agent.done', { reason: 'stop' }); reply(request, 'stream-1') }
    }
    await store.sendMessage('hi', 'config-1', true, 4000)
    expect(store.messages[1].content).toBe('Hello')
    expect(store.messages[1].status).toBe('done')
    expect(store.streamingMessageId).toBeNull()
  })
  it('streams reasoning, nested tool calls, output and failures', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.sendMessage('hi', 'config-1')
    event('agent.token', { text: 'ignored' }, 'other-stream')
    event('agent.thinking', { text: 'Plan' })
    event('agent.toolCallStarted', { id: 'parent', name: 'execute_code', args: '{}' })
    event('agent.toolCallStarted', { id: 'child', parentId: 'parent', name: 'file_read', args: '{}' })
    event('agent.toolCallOutput', { id: 'child', line: 'line 1', isError: false })
    event('agent.toolCallCompleted', { id: 'child', name: 'file_read', result: '{"ok":false}' })
    expect(store.messages[1].content).toBe('')
    expect(store.messages[1].reasoningContent).toBe('Plan')
    expect(store.messages[1].toolCalls?.[1]).toMatchObject({ parentId: 'parent', status: 'error', outputLines: ['line 1'] })
    event('agent.error', { message: 'failed' })
    expect(store.messages[1].status).toBe('error')
    expect(store.streamingMessageId).toBeNull()
  })
  it('preserves completed iterations when retry notice resets partial output', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore(); await store.sendMessage('hi', 'config-1')
    event('agent.token', { text: 'completed' }); event('agent.iteration', { current: 2 }); event('agent.token', { text: 'partial' }); event('agent.notice', { resetResponse: true })
    expect(store.messages[1].content).toBe('completed')
    event('agent.done', { reason: 'stop' })
  })
  it('cancels native stream and removes listeners for late events', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore(); await store.sendMessage('hi', 'config-1')
    handle = request => { expect(request.method).toBe('chat.cancelStream'); expect(request.params.streamId).toBe('stream-1'); reply(request, true) }
    await store.cancelStream(); event('agent.token', { text: 'late' })
    expect(store.messages[1].content).toBe(''); expect(store.streamingMessageId).toBeNull()
  })
  it('prevents overlapping sends and releases lock after start failure', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore(); await store.createConversation()
    let pending: Request | undefined
    handle = request => { pending = request }
    const sending = store.sendMessage('hi', 'config-1')
    await expect(store.sendMessage('duplicate', 'config-1')).rejects.toThrow('正在生成')
    reply(pending!, null, 'cannot start'); await expect(sending).rejects.toThrow('cannot start')
    expect(store.streamingMessageId).toBeNull()
    handle = request => reply(request, 'stream-1')
    await store.sendMessage('retry', 'config-1'); event('agent.done', { reason: 'stop' })
  })
  it('ignores stale conversation reads', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore(); const requests: Request[] = []; handle = request => { requests.push(request) }
    const first = store.selectConversation('first'); const second = store.selectConversation('second')
    reply(requests[1], [{ id: 'new', role: 'user', content: 'new' }]); await second
    reply(requests[0], [{ id: 'old', role: 'user', content: 'old' }]); await first
    expect(store.activeId).toBe('second'); expect(store.messages[0].content).toBe('new')
  })
  it('restores persisted host tool traces', async () => {
    const { restoreMessages } = await import('../src/utils/execution')
    const messages = restoreMessages([
      { id: 'a', role: 'assistant', content: '', createdAt: '', toolCallsJson: '[{"id":"parent","function":{"name":"execute_code","arguments":"{}"}}]' },
      { id: 'b', role: 'tool', toolCallId: 'parent', content: '{"ok":true}', createdAt: '', hostToolTraceJson: JSON.stringify([{ id: 'child', toolName: 'file_read', argsJson: '{}', resultJson: '{"ok":true}' }]) }
    ])
    expect(messages).toHaveLength(1); expect(messages[0].toolCalls?.[0].status).toBe('done')
    expect(messages[0].toolCalls?.[1].parentId).toBe('parent')
  })
})
