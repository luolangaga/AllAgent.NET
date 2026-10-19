import { beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params: any }
type HostWindow = Window & { chrome?: { webview: { postMessage: (json: string) => void } }; __bridgeCallback: (json: string) => void }
const host = window as unknown as HostWindow
let handle: (request: Request) => void
const reply = (request: Request, result: unknown, error?: string) => host.__bridgeCallback(JSON.stringify({ id: request.id, result, error }))
const event = (eventName: string, data: unknown, streamId = 'stream-1', sequence?: number, timestamp?: string) => host.__bridgeCallback(JSON.stringify({ type: 'event', eventName, data, streamId, sequence, timestamp }))
const createdConversation = { id: 'conv-1', title: 'New Conversation', modelId: '', providerId: '', createdAt: '', updatedAt: '' }

beforeEach(() => {
  vi.resetModules()
  host.chrome = { webview: { postMessage: (json: string) => handle(JSON.parse(json)) } }
  handle = request => {
    if (request.method === 'conversations.create') reply(request, createdConversation)
    else if (request.method === 'chat.startStream') reply(request, 'stream-1')
    else reply(request, true)
  }
})

describe('conversation streaming', () => {
  it('keeps separate timed reasoning phases in each iteration and closes them on text, tools or cancellation', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.sendMessage('task', 'config-1')
    event('agent.iteration', { current: 1, maximum: 0, toolCalls: 0 })
    event('agent.thinking', { text: '第一轮' }, 'stream-1', undefined, '2026-10-18T02:00:00Z')
    event('agent.thinking', { text: '思考' })
    event('agent.token', { text: '先读取' }, 'stream-1', undefined, '2026-10-18T02:00:03Z')
    event('agent.toolCallStarted', { id: 't', name: 'file_read', args: '{}' })
    event('agent.iteration', { current: 2, maximum: 0, toolCalls: 1 })
    event('agent.thinking', { text: '第二轮思考' })
    const parts = store.messages[1].parts!
    expect(parts[0]).toMatchObject({ type: 'reasoning', text: '第一轮思考', iteration: 1, status: 'done' })
    if (parts[0].type === 'reasoning') expect(parts[0].completedAt! - parts[0].startedAt!).toBe(3000)
    expect(parts.map(part => part.type)).toEqual(['reasoning', 'text', 'tool', 'reasoning'])
    expect(parts[3]).toMatchObject({ iteration: 2, status: 'streaming' })
    event('agent.done', { reason: 'cancelled' })
    expect(store.messages[1].parts?.[3]).toMatchObject({ status: 'interrupted' })
  })
  it('keeps text and tools in arrival order, updates message identity, and deduplicates tool starts', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.sendMessage('task', 'config-1')
    const before = store.messages[1]
    event('agent.token', { text: '先读取。' })
    expect(store.messages[1]).not.toBe(before)
    event('agent.toolCallStarted', { id: 't', name: 'file_read', args: '{}' })
    event('agent.toolCallStarted', { id: 't', name: 'file_read', args: '{}' })
    event('agent.toolCallCompleted', { id: 't', name: 'file_read', result: '{"ok":true}' })
    event('agent.token', { text: '读取完成。' })
    expect(store.messages[1].parts).toEqual([{ type: 'text', text: '先读取。' }, { type: 'tool', id: 't' }, { type: 'text', text: '读取完成。' }])
    expect(store.messages[1].toolCalls).toHaveLength(1)
    event('agent.toolCallCompleted', { id: 'orphan', name: 'todo_read', result: '{"ok":true}' })
    expect(store.messages[1].toolCalls?.[1]).toMatchObject({ id: 'orphan', status: 'done' })
    event('agent.done', { reason: 'stop' })
  })

  it('keeps cancellation retryable on RPC failure and settles unfinished tools after acknowledgement', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.sendMessage('task', 'config-1')
    event('agent.toolCallStarted', { id: 't', name: 'file_read', args: '{}' })
    handle = request => reply(request, null, 'offline')
    await expect(store.cancelStream()).rejects.toThrow('offline')
    expect(store.activeStreamId).toBe('stream-1')
    event('agent.token', { text: 'still attached' })
    expect(store.messages[1].content).toBe('still attached')
    handle = request => reply(request, true)
    await store.cancelStream()
    expect(store.messages[1].toolCalls?.[0]).toMatchObject({ status: 'cancelled' })
    expect(store.messages[1].finishReason).toBe('cancelled')
  })

  it('marks uncompleted tool rows as interrupted when the run ends', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.sendMessage('task', 'config-1')
    event('agent.toolCallStarted', { id: 't', name: 'file_read', args: '{}' })
    event('agent.done', { reason: 'tool_limit' })
    expect(store.messages[1].toolCalls?.[0].status).toBe('error')
    expect(store.messages[1].finishReason).toBe('tool_limit')
  })

  it('surfaces continuation notices and treats a timeout as interruption', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.sendMessage('task', 'config-1')
    event('agent.notice', { message: '正在自动继续', resetResponse: false })
    expect(store.messages[1].parts?.at(-1)).toMatchObject({ type: 'notice', text: '正在自动继续', kind: 'info', status: 'settled' })
    event('agent.token', { text: 'partial' })
    expect(store.messages[1].content).toBe('partial')
    event('agent.done', { reason: 'timeout' })
    expect(store.messages[1]).toMatchObject({ status: 'error', finishReason: 'timeout', content: 'partial' })
    expect(store.messages[1].error).toContain('超时')
  })
  it('replays events received before the stream response', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    handle = request => {
      if (request.method === 'conversations.create') reply(request, createdConversation)
      else {
        expect(request.params).toMatchObject({ conversationId: 'conv-1', apiConfigId: 'config-1', thinkingEnabled: true, thinkingBudget: 4000 })
        event('agent.token', { text: 'Hello' })
        event('agent.done', { reason: 'stop' })
        reply(request, 'stream-1')
      }
    }
    await store.sendMessage('hi', 'config-1', true, 4000)
    expect(store.messages[1].content).toBe('Hello')
    expect(store.messages[1].status).toBe('done')
    expect(store.streamingMessageId).toBeNull()
  })

  it('passes the per-message model and thinking level to the backend', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    handle = request => {
      if (request.method === 'conversations.create') return reply(request, createdConversation)
      expect(request.method).toBe('chat.startStream')
      expect(request.params).toMatchObject({ model: 'deepseek-v4-pro', reasoningEffort: 'max' })
      reply(request, 'stream-1')
    }
    await store.sendMessage('hi', 'config-1', true, 8000, 'deepseek-v4-pro', 'max')
    event('agent.done', { reason: 'stop' })
    expect(store.streamingMessageId).toBeNull()
  })

  it('streams reasoning, nested tool calls, context usage and failures', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.sendMessage('hi', 'config-1')
    event('agent.token', { text: 'ignored' }, 'other-stream')
    event('agent.thinking', { text: 'Plan' })
    event('agent.iteration', { current: 2, maximum: 8, toolCalls: 1 })
    event('agent.toolCallStarted', { id: 'parent', name: 'execute_code', args: '{}' })
    event('agent.toolCallStarted', { id: 'child', parentId: 'parent', name: 'file_read', args: '{}' })
    event('agent.toolCallOutput', { id: 'child', line: 'line 1', isError: false })
    event('agent.toolCallCompleted', { id: 'child', name: 'file_read', result: '{"ok":false}' })
    event('agent.contextUsage', { usedTokens: 1200, capacityTokens: 128000, estimated: false })
    expect(store.messages[1].content).toBe('')
    expect(store.messages[1].reasoningContent).toBe('Plan')
    expect(store.messages[1].iteration).toEqual({ current: 2, maximum: 8, toolCalls: 1 })
    expect(store.messages[1].toolCalls?.[1]).toMatchObject({ parentId: 'parent', status: 'error', outputLines: ['line 1'] })
    expect(store.contextUsage).toEqual({ usedTokens: 1200, capacityTokens: 128000, estimated: false })
    event('agent.error', { message: 'failed' })
    expect(store.messages[1].status).toBe('error')
    expect(store.messages[1].error).toBe('failed')
    expect(store.streamingMessageId).toBeNull()
  })

  it('preserves completed iterations when a retry notice resets partial output', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.sendMessage('hi', 'config-1')
    event('agent.token', { text: 'completed' })
    event('agent.iteration', { current: 2 })
    event('agent.token', { text: 'partial' })
    event('agent.notice', { message: '模型连接暂时失败，2 秒后重试（1/5）', resetResponse: true })
    expect(store.messages[1].content).toBe('completed')
    expect(store.messages[1].parts?.at(-1)).toMatchObject({ type: 'notice', kind: 'retry', status: 'pending' })
    event('agent.done', { reason: 'stop' })
  })

  it('folds repeated retries into one line and settles it once output resumes', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.sendMessage('hi', 'config-1')
    event('agent.notice', { message: '模型连接暂时失败，2 秒后重试（1/5）', resetResponse: true })
    event('agent.notice', { message: '模型连接暂时失败，4 秒后重试（2/5）', resetResponse: true })
    const notices = store.messages[1].parts?.filter(part => part.type === 'notice') ?? []
    expect(notices).toHaveLength(1)
    expect(notices[0]).toMatchObject({ kind: 'retry', status: 'pending', attempts: 2, text: '模型连接暂时失败，4 秒后重试（2/5）' })
    event('agent.token', { text: 'recovered' })
    expect(notices[0]).toMatchObject({ status: 'settled' })
    expect(store.messages[1].content).toBe('recovered')
  })

  it('keeps a retry line as failed when the run gives up', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.sendMessage('hi', 'config-1')
    event('agent.notice', { message: '模型连接暂时失败，8 秒后重试（3/5）', resetResponse: true })
    event('agent.done', { reason: 'error' })
    expect(store.messages[1].parts?.[0]).toMatchObject({ type: 'notice', status: 'failed', text: '模型连接暂时失败，8 秒后重试（3/5）' })
  })

  it('cancels the native stream and detaches late events', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.sendMessage('hi', 'config-1')
    handle = request => {
      expect(request.method).toBe('chat.cancelStream')
      expect(request.params.streamId).toBe('stream-1')
      reply(request, true)
    }
    await store.cancelStream()
    event('agent.token', { text: 'late' })
    expect(store.messages[1].content).toBe('')
    expect(store.streamingMessageId).toBeNull()
    expect(store.activeStreamId).toBeNull()
  })

  it('prevents overlapping sends and releases the lock after a start failure', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.createConversation()
    let pending: Request | undefined
    handle = request => { pending = request }
    const sending = store.sendMessage('hi', 'config-1')
    await expect(store.sendMessage('duplicate', 'config-1')).rejects.toThrow('正在生成')
    reply(pending!, null, 'cannot start')
    await expect(sending).rejects.toThrow('cannot start')
    expect(store.streamingMessageId).toBeNull()
    expect(store.sending).toBe(false)
    // The optimistic bubbles are dropped because nothing was persisted.
    expect(store.messages).toHaveLength(0)
    handle = request => reply(request, 'stream-1')
    await store.sendMessage('retry', 'config-1')
    expect(store.messages).toHaveLength(2)
    event('agent.done', { reason: 'stop' })
  })
})

describe('conversation history races', () => {
  it('reattaches to a running backend after reload and deduplicates snapshot/live overlap', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    handle = request => {
      if (request.method === 'conversations.getMessages') return reply(request, [{ id: 'partial', role: 'assistant', content: 'already persisted partial iteration' }])
      if (request.method === 'todos.get') return reply(request, [])
      if (request.method === 'chat.getStreamState') {
        event('agent.token', { text: '前言' }, 'stream-1', 1)
        reply(request, { streamId: 'stream-1', history: [], input: 'task', createdAt: '', events: [
          { eventName: 'agent.token', data: { text: '前言' }, sequence: 1 },
          { eventName: 'agent.toolCallStarted', data: { id: 't', name: 'file_read', args: '{}' }, sequence: 2 },
        ] })
        event('agent.token', { text: '后续' }, 'stream-1', 3)
        return
      }
      reply(request, true)
    }
    await store.selectConversation('conv-1')
    expect(store.messages).toHaveLength(2)
    expect(store.messages[1].content).toBe('前言后续')
    expect(store.messages[1].parts).toEqual([{ type: 'text', text: '前言' }, { type: 'tool', id: 't' }, { type: 'text', text: '后续' }])
    expect(store.activeStreamId).toBe('stream-1')
    event('agent.toolCallCompleted', { id: 't', name: 'file_read', result: '{"ok":true}' }, 'stream-1', 4)
    event('agent.done', { reason: 'stop' }, 'stream-1', 5)
    expect(store.messages[1].toolCalls?.[0].status).toBe('done')
    expect(store.activeStreamId).toBeNull()
  })
  it('a late history load never wipes an in-flight send', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.createConversation()
    const pending: Request[] = []
    handle = request => request.method === 'chat.startStream' ? reply(request, 'stream-1') : request.method === 'chat.getStreamState' ? reply(request, null) : pending.push(request)
    const loading = store.selectConversation('conv-1')
    await Promise.resolve()
    await store.sendMessage('new input', 'config-1')
    event('agent.token', { text: 'already generated' })
    expect(store.messages).toHaveLength(2)
    pending.forEach(request => reply(request, []))
    await vi.waitFor(() => expect(pending.some(request => request.method === 'conversations.getMessages')).toBe(true))
    reply(pending.find(request => request.method === 'conversations.getMessages')!, [])
    await loading
    expect(store.messages).toHaveLength(2)
    expect(store.messages[1].content).toBe('already generated')
    expect(store.streamingMessageId).not.toBeNull()
    expect(store.isLoading).toBe(false)
    event('agent.done', { reason: 'stop' })
  })

  it('a recoverable tool rejection is non-fatal and keeps the stream attached', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    await store.sendMessage('task', 'config-1')
    event('agent.toolCallStarted', { id: 't1', name: 'file_read', args: '{}' })
    event('agent.toolCallRejected', { id: 't1', tool: 'file_read', reason: 'protocol violation', message: '未执行 file_read: protocol violation' })
    expect(store.streamingMessageId).not.toBeNull()
    expect(store.messages[1].toolCalls?.[0]).toMatchObject({ status: 'error', rejected: true })
    expect(store.messages[1].toolCalls?.[0].note).toContain('未执行 file_read')
    event('agent.token', { text: 'backend continues working' })
    expect(store.messages[1].content).toContain('backend continues working')
    event('agent.done', { reason: 'stop' })
    expect(store.streamingMessageId).toBeNull()
  })

  it('creating a conversation during an initial send is rejected without discarding the UI', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    let createRequest: Request | undefined
    handle = request => {
      if (request.method === 'conversations.create') { createRequest = request; return }
      if (request.method === 'chat.startStream') reply(request, 'stream-1')
      else reply(request, true)
    }
    const sending = store.sendMessage('first input', 'config-1')
    // Only the submit window itself still owns the optimistic bubbles.
    await expect(store.createConversation()).rejects.toThrow()
    expect(store.sending).toBe(true)
    expect(createRequest).toBeDefined()
    reply(createRequest!, createdConversation)
    await sending
    expect(store.activeId).toBe('conv-1')
    expect(store.messages).toHaveLength(2)
    expect(store.streamingMessageId).not.toBeNull()
    event('agent.done', { reason: 'stop' })
  })

  it('starts another conversation while a run in flight keeps streaming in the background', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    let creates = 0
    handle = request => {
      if (request.method === 'conversations.create') reply(request, { ...createdConversation, id: `conv-${++creates}` })
      else if (request.method === 'chat.startStream') reply(request, 'stream-1')
      else reply(request, true)
    }
    await store.sendMessage('long task', 'config-1')
    event('agent.token', { text: 'working' })
    expect(store.activeId).toBe('conv-1')
    expect(store.streamingMessageId).not.toBeNull()

    // Nothing here needs the run to stop: the view moves on, the run does not.
    const next = await store.createConversation()
    expect(next.id).toBe('conv-2')
    expect(store.activeId).toBe('conv-2')
    expect(store.messages).toEqual([])
    // The host reports the session that is still working, so the list can mark it.
    host.__bridgeCallback(JSON.stringify({ type: 'runs.changed', conversations: [{ conversationId: 'conv-1', title: 'New Conversation', workspace: 'C:/work', workspaceName: 'work', dataDirectory: 'C:/work/.allagent/data', state: 'running' }] }))
    expect(store.isRunning('conv-1')).toBe(true)
    expect(store.runState('conv-1')).toBe('running')
    expect(store.isRunning('conv-2')).toBe(false)
    // The detached stream neither leaks into the visible conversation nor gets cancelled.
    event('agent.token', { text: ' still running' })
    expect(store.messages).toEqual([])
    event('agent.done', { reason: 'stop' })
    host.__bridgeCallback(JSON.stringify({ type: 'runs.changed', conversations: [] }))
    expect(store.isRunning('conv-1')).toBe(false)
  })

  it('ignores stale conversation reads', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    const requests: Request[] = []
    handle = request => { requests.push(request) }
    const first = store.selectConversation('first')
    const second = store.selectConversation('second')
    const requestFor = (id: string, method: string) => requests.find(request => request.method === method && request.params.conversationId === id)!
    reply(requestFor('second', 'chat.getStreamState'), null)
    reply(requestFor('second', 'todos.get'), [{ id: 'todo-new', content: '新清单', status: 'pending' }])
    await vi.waitFor(() => expect(requestFor('second', 'conversations.getMessages')).toBeDefined())
    reply(requestFor('second', 'conversations.getMessages'), [{ id: 'new', role: 'user', content: 'new' }])
    await second
    reply(requestFor('first', 'todos.get'), [{ id: 'todo-old', content: '旧清单', status: 'pending' }])
    reply(requestFor('first', 'chat.getStreamState'), null)
    await vi.waitFor(() => expect(requestFor('first', 'conversations.getMessages')).toBeDefined())
    reply(requestFor('first', 'conversations.getMessages'), [{ id: 'old', role: 'user', content: 'old' }])
    await first
    expect(store.activeId).toBe('second')
    expect(store.messages[0].content).toBe('new')
    expect(store.todos[0].id).toBe('todo-new')
  })
})

describe('TODO tracking', () => {
  it('loads, edits and streams TODO progress without accepting late stream updates', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    const pending = [{ id: '1', content: '验证', status: 'pending' as const }]
    handle = request => {
      if (request.method === 'conversations.getMessages') reply(request, [])
      else if (request.method === 'todos.get') reply(request, pending)
      else if (request.method === 'todos.set') reply(request, request.params.todos)
      else if (request.method === 'chat.startStream') reply(request, 'stream-1')
      else reply(request, true)
    }
    await store.selectConversation('conv-1')
    expect(store.todos).toEqual(pending)
    await store.setTodos([{ ...pending[0], status: 'in_progress' }])
    expect(store.todos[0].status).toBe('in_progress')
    await store.sendMessage('开始', 'config-1')
    await expect(store.setTodos([])).rejects.toThrow('请先停止')
    const completed = [{ ...pending[0], status: 'completed' }]
    event('agent.toolCallCompleted', { id: 'todo', name: 'todo_write', result: JSON.stringify({ ok: true, data: { todos: completed } }) })
    expect(store.todos[0].status).toBe('completed')
    event('agent.toolCallCompleted', { id: 'bad', name: 'todo_write', result: '{"ok":false,"error":"invalid"}' })
    expect(store.todos[0].status).toBe('completed')
    event('agent.done', { reason: 'stop' })
    event('agent.toolCallCompleted', { id: 'late', name: 'todo_write', result: '{"ok":true,"data":{"todos":[]}}' })
    expect(store.todos).toHaveLength(1)
  })

  it('persists an automatic title after the first completed exchange', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const store = useConversationsStore()
    const renames: Request[] = []
    handle = request => {
      if (request.method === 'conversations.create') return reply(request, createdConversation)
      if (request.method === 'chat.startStream') return reply(request, 'stream-1')
      if (request.method === 'conversations.rename') { renames.push(request); return reply(request, true) }
      reply(request, true)
    }
    await store.sendMessage('帮我整理一份很长的发布说明，包含所有变更点与升级步骤', 'config-1')
    event('agent.done', { reason: 'stop' })
    await Promise.resolve()
    expect(renames).toHaveLength(1)
    expect(renames[0].params.title.length).toBeLessThanOrEqual(41)
    expect(store.conversations[0].title).toBe(renames[0].params.title)
  })
})
