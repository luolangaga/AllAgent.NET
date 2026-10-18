import { defineStore, ref, computed } from '@/lib/store'
import type { ContextUsage, Conversation, ImageAttachment, Message, TodoItem, ToolCall } from '@/types'
import { bridgeEvents, invoke, type BridgeEvents } from '@/bridge'
import { parseObject, restoreMessages, toolFailed } from '@/utils/execution'

export const useConversationsStore = defineStore('conversations', () => {
  const conversations = ref<Conversation[]>([])
  const activeId = ref<string | null>(null)
  const messages = ref<Message[]>([])
  const todos = ref<TodoItem[]>([])
  const sending = ref(false)
  const streamingMessageId = ref<string | null>(null)
  const activeStreamId = ref<string | null>(null)
  const contextUsage = ref<ContextUsage | null>(null)
  const isLoading = ref(false)

  // Every mutation that replaces the message list bumps this counter; late
  // history loads may only apply when their version is still current.
  let selectionVersion = 0
  let detachStream: (() => void) | null = null

  const active = computed(() => conversations.value.find(item => item.id === activeId.value) ?? null)

  function findMessage(id: string): Message | undefined {
    return messages.value.find(message => message.id === id)
  }

  function settleTools(message: Message | undefined, reason: string) {
    for (const call of message?.toolCalls ?? []) {
      if (call.status !== 'running') continue
      call.status = reason === 'cancelled' ? 'cancelled' : 'error'
      call.note = reason === 'cancelled' ? '执行已停止，请检查操作是否已经生效。' : '运行已结束，未收到此工具的完成结果。'
    }
  }

  function settleReasoning(message: Message | undefined, completedAt = Date.now(), interrupted = false) {
    for (const part of message?.parts ?? []) {
      if (part.type !== 'reasoning' || part.status !== 'streaming') continue
      part.status = interrupted ? 'interrupted' : 'done'
      part.completedAt = Math.max(part.startedAt ?? completedAt, completedAt)
    }
  }

  async function loadAll() {
    conversations.value = await invoke<Conversation[]>('conversations.list')
  }

  async function selectConversation(id: string) {
    if (sending.value || streamingMessageId.value) throw new Error('请先停止当前生成')
    const version = ++selectionVersion
    activeId.value = id
    messages.value = []
    todos.value = []
    contextUsage.value = null
    isLoading.value = true
    await loadConversationView(id, version)
  }

  interface StreamSnapshot {
    streamId: string
    history: Message[]
    input: string
    createdAt: string
    events: { eventName: keyof BridgeEvents; data: Record<string, unknown>; sequence: number; timestamp?: string }[]
  }

  async function loadConversationView(id: string, version: number) {
    const early: StreamEvent[] = []
    const buffer = (type: keyof BridgeEvents, event: BridgeEvents[keyof BridgeEvents]) => { early.push([type, event]) }
    bridgeEvents.on('*', buffer)
    try {
      const [checklist, state] = await Promise.all([
        invoke<TodoItem[]>('todos.get', { conversationId: id }),
        invoke<StreamSnapshot | null>('chat.getStreamState', { conversationId: id }),
      ])
      // Read persisted history only after observing no active journal. Reading
      // both concurrently could capture partial history just before run end.
      const loaded = state?.streamId ? state.history : await invoke<Message[]>('conversations.getMessages', { conversationId: id })
      // A send that started while history was loading owns the view now.
      if (version === selectionVersion && activeId.value === id && !sending.value) {
        detachStream?.()
        streamingMessageId.value = null
        activeStreamId.value = null
        todos.value = checklist
        if (state?.streamId) {
          const assistantId = `stream-${state.streamId}`
          messages.value = [...restoreMessages(state.history),
            { id: `input-${state.streamId}`, role: 'user', content: state.input, createdAt: state.createdAt, status: 'done' },
            { id: assistantId, role: 'assistant', content: '', createdAt: state.createdAt, status: 'streaming', toolCalls: [] }]
          streamingMessageId.value = assistantId
          activeStreamId.value = state.streamId
          const replay: StreamEvent[] = state.events.map(item => [item.eventName, { ...item.data, streamId: state.streamId, sequence: item.sequence, timestamp: item.timestamp } as BridgeEvents[keyof BridgeEvents]])
          attachStream(state.streamId, id, assistantId, state.input, [...replay, ...early])
        } else {
          messages.value = restoreMessages(loaded)
        }
      }
    } finally {
      bridgeEvents.off('*', buffer)
      if (version === selectionVersion) isLoading.value = false
    }
  }

  function resetView(conversation: Conversation) {
    selectionVersion++
    conversations.value = [conversation, ...conversations.value.filter(item => item.id !== conversation.id)]
    activeId.value = conversation.id
    messages.value = []
    todos.value = []
    contextUsage.value = null
    isLoading.value = false
  }

  async function createConversation() {
    if (sending.value || streamingMessageId.value) throw new Error('请先停止当前生成，再新建会话')
    const created = await invoke<Conversation>('conversations.create', { title: 'New Conversation' })
    resetView(created)
    return created
  }

  /** Internal helper for sendMessage: creating a conversation is part of the send. */
  async function ensureConversation(): Promise<string> {
    if (activeId.value) return activeId.value
    const created = await invoke<Conversation>('conversations.create', { title: 'New Conversation' })
    resetView(created)
    return created.id
  }

  async function renameConversation(id: string, title: string) {
    await invoke('conversations.rename', { id, title })
    conversations.value = conversations.value.map(item => (item.id === id ? { ...item, title } : item))
  }

  async function deleteConversation(id: string) {
    if (sending.value || streamingMessageId.value) throw new Error('请先停止当前生成')
    await invoke('conversations.delete', { id })
    conversations.value = conversations.value.filter(item => item.id !== id)
    if (activeId.value !== id) return
    selectionVersion++
    activeId.value = null
    messages.value = []
    todos.value = []
    contextUsage.value = null
    isLoading.value = false
    const next = conversations.value[0]?.id
    if (next) await selectConversation(next)
  }

  async function setTodos(items: TodoItem[]) {
    if (sending.value || streamingMessageId.value) throw new Error('请先停止当前生成再编辑 TODO')
    if (!activeId.value) throw new Error('请先选择会话')
    const id = activeId.value
    const saved = await invoke<TodoItem[]>('todos.set', { conversationId: id, todos: items })
    if (activeId.value === id) todos.value = saved
  }

  function applyTodoResult(toolName: string, result: string) {
    if (toolName !== 'todo_write' && toolName !== 'todo_read') return
    const payload = parseObject(result)
    if (payload.ok === true && Array.isArray(payload.data?.todos)) todos.value = payload.data.todos as TodoItem[]
  }

  type StreamEvent = [keyof BridgeEvents, BridgeEvents[keyof BridgeEvents]]

  function attachStream(streamId: string, conversationId: string, assistantId: string, content: string, early: StreamEvent[]) {
    let lastSequence = 0
    const current = (eventStreamId?: string, sequence?: number) => {
      if (eventStreamId !== streamId) return false
      if (sequence !== undefined) {
        if (sequence <= lastSequence) return false
        lastSequence = sequence
      }
      // A new message reference lets React memoize historical bubbles while
      // this one continues to stream. Nested tool state stays observable.
      messages.value = messages.value.map(message => message.id === assistantId ? { ...message } : message)
      return true
    }
    let completedText = ''
    let completedThinking = ''
    let completedParts: NonNullable<Message['parts']> = []
    const eventTime = (data: { timestamp?: string }) => {
      const parsed = data.timestamp ? Date.parse(data.timestamp) : NaN
      return Number.isFinite(parsed) ? parsed : Date.now()
    }

    const onToken = (data: BridgeEvents['agent.token']) => {
      if (!current(data.streamId, data.sequence)) return
      if (!data.text) return
      const message = findMessage(assistantId)
      if (message) {
        settleReasoning(message, eventTime(data))
        message.content += data.text
        message.notice = undefined
        const parts = message.parts ?? (message.parts = [])
        const last = parts.at(-1)
        if (last?.type === 'text') last.text += data.text
        else parts.push({ type: 'text', text: data.text })
      }
    }
    const onThinking = (data: BridgeEvents['agent.thinking']) => {
      if (!current(data.streamId, data.sequence)) return
      if (!data.text) return
      const message = findMessage(assistantId)
      if (message) {
        message.reasoningContent = (message.reasoningContent ?? '') + data.text
        const parts = message.parts ?? (message.parts = [])
        const previous = parts.at(-1)
        if (previous?.type === 'reasoning' && previous.status === 'streaming') previous.text += data.text
        else parts.push({ type: 'reasoning', text: data.text, iteration: message.iteration?.current ?? 1, status: 'streaming', startedAt: eventTime(data) })
      }
    }
    const onIteration = (data: BridgeEvents['agent.iteration']) => {
      if (!current(data.streamId, data.sequence)) return
      const message = findMessage(assistantId)
      if (message) {
        settleReasoning(message, eventTime(data))
        message.iteration = { current: data.current, maximum: data.maximum, toolCalls: data.toolCalls }
        completedText = message.content
        completedThinking = message.reasoningContent ?? ''
        completedParts = message.parts?.map(part => ({ ...part })) ?? []
      }
    }
    const onNotice = (data: BridgeEvents['agent.notice']) => {
      if (!current(data.streamId, data.sequence)) return
      const message = findMessage(assistantId)
      if (message) {
        message.notice = data.message
        if (data.resetResponse) {
          message.content = completedText
          message.reasoningContent = completedThinking
          message.parts = completedParts.map(part => ({ ...part }))
        }
      }
    }
    function ensureTool(id: string, name = 'tool'): ToolCall | undefined {
      const message = findMessage(assistantId)
      if (!message) return
      const calls = message.toolCalls ?? (message.toolCalls = [])
      let call = calls.find(item => item.id === id)
      if (!call) {
        calls.push({ id, name, args: '{}', status: 'running', outputLines: [] })
        const parts = message.parts ?? (message.parts = [])
        parts.push({ type: 'tool', id })
        call = calls.find(item => item.id === id)
      }
      return call
    }
    const onToolCallStarted = (data: BridgeEvents['agent.toolCallStarted']) => {
      if (!current(data.streamId, data.sequence)) return
      settleReasoning(findMessage(assistantId), eventTime(data))
      const call = ensureTool(data.id, data.name)
      if (call) { call.name = data.name; call.args = data.args; call.parentId = data.parentId }
    }
    const onToolCallOutput = (data: BridgeEvents['agent.toolCallOutput']) => {
      if (!current(data.streamId, data.sequence)) return
      settleReasoning(findMessage(assistantId), eventTime(data))
      const call = ensureTool(data.id)
      if (call) call.outputLines = [...(call.outputLines ?? []), data.isError ? `[stderr] ${data.line}` : data.line].slice(-400)
    }
    const onToolCallCompleted = (data: BridgeEvents['agent.toolCallCompleted']) => {
      if (!current(data.streamId, data.sequence)) return
      settleReasoning(findMessage(assistantId), eventTime(data))
      applyTodoResult(data.name, data.result)
      const call = ensureTool(data.id, data.name)
      if (call) {
        call.name = data.name
        call.status = call.rejected || toolFailed(data.result) ? 'error' : 'done'
        call.output = data.result
      }
    }
    // Policy rejections are recoverable: the tool row turns red, the run continues.
    const onToolCallRejected = (data: BridgeEvents['agent.toolCallRejected']) => {
      if (!current(data.streamId, data.sequence)) return
      const message = findMessage(assistantId)
      if (!message) return
      settleReasoning(message, eventTime(data))
      const calls = message.toolCalls ?? (message.toolCalls = [])
      let call = calls.find(item => item.id === data.id)
      if (!call) {
        call = { id: data.id, name: data.tool ?? 'tool', args: '{}', status: 'error', outputLines: [] }
        calls.push(call)
        const parts = message.parts ?? (message.parts = [])
        parts.push({ type: 'tool', id: data.id })
      }
      call.status = 'error'
      call.rejected = true
      call.rejectionKind = /direct model tool calls are forbidden|protocol violation/i.test(data.reason) ? 'protocol' : 'policy'
      call.note = data.message
    }
    const onContextUsage = (data: BridgeEvents['agent.contextUsage']) => {
      if (!current(data.streamId, data.sequence)) return
      contextUsage.value = { usedTokens: data.usedTokens, capacityTokens: data.capacityTokens, estimated: data.estimated }
    }
    const onDone = (data: BridgeEvents['agent.done']) => {
      if (!current(data.streamId, data.sequence)) return
      const message = findMessage(assistantId)
      if (message) {
        message.finishReason = data.reason
        if (data.reason === 'error' || data.reason === 'protocol_error' || data.reason === 'timeout') message.status = 'error'
        if (data.reason === 'timeout') message.error = '执行已超时，已完成的结果保留，可输入“继续”。'
      }
      settleTools(message, data.reason)
      settleReasoning(message, eventTime(data), ['cancelled', 'error', 'timeout', 'protocol_error'].includes(data.reason))
      finish('done')
    }
    const onError = (data: BridgeEvents['agent.error']) => {
      if (!current(data.streamId, data.sequence)) return
      const message = findMessage(assistantId)
      if (message) {
        message.status = 'error'
        message.error = data.message
      }
      settleTools(message, 'error')
      settleReasoning(message, eventTime(data), true)
      finish(null)
    }

    function detach() {
      bridgeEvents.off('agent.token', onToken)
      bridgeEvents.off('agent.thinking', onThinking)
      bridgeEvents.off('agent.iteration', onIteration)
      bridgeEvents.off('agent.notice', onNotice)
      bridgeEvents.off('agent.toolCallStarted', onToolCallStarted)
      bridgeEvents.off('agent.toolCallOutput', onToolCallOutput)
      bridgeEvents.off('agent.toolCallCompleted', onToolCallCompleted)
      bridgeEvents.off('agent.toolCallRejected', onToolCallRejected)
      bridgeEvents.off('agent.contextUsage', onContextUsage)
      bridgeEvents.off('agent.done', onDone)
      bridgeEvents.off('agent.error', onError)
      if (detachStream === detach) detachStream = null
    }

    function finish(status: 'done' | null) {
      detach()
      if (status) {
        const message = findMessage(assistantId)
        if (message && message.status === 'streaming') message.status = 'done'
      }
      if (streamingMessageId.value === assistantId) {
        streamingMessageId.value = null
        activeStreamId.value = null
      }
      const conversation = conversations.value.find(item => item.id === conversationId)
      if (conversation && conversation.title === 'New Conversation' && content.trim()) {
        const title = content.trim()
        conversation.title = title.length > 40 ? `${title.slice(0, 40)}…` : title
        void invoke('conversations.rename', { id: conversation.id, title: conversation.title }).catch(() => {})
      }
    }

    bridgeEvents.on('agent.token', onToken)
    bridgeEvents.on('agent.thinking', onThinking)
    bridgeEvents.on('agent.iteration', onIteration)
    bridgeEvents.on('agent.notice', onNotice)
    bridgeEvents.on('agent.toolCallStarted', onToolCallStarted)
    bridgeEvents.on('agent.toolCallOutput', onToolCallOutput)
    bridgeEvents.on('agent.toolCallCompleted', onToolCallCompleted)
    bridgeEvents.on('agent.toolCallRejected', onToolCallRejected)
    bridgeEvents.on('agent.contextUsage', onContextUsage)
    bridgeEvents.on('agent.done', onDone)
    bridgeEvents.on('agent.error', onError)
    const handlers: Partial<{ [K in keyof BridgeEvents]: (data: BridgeEvents[K]) => void }> = {
      'agent.token': onToken, 'agent.thinking': onThinking, 'agent.iteration': onIteration,
      'agent.notice': onNotice, 'agent.toolCallStarted': onToolCallStarted,
      'agent.toolCallOutput': onToolCallOutput, 'agent.toolCallCompleted': onToolCallCompleted,
      'agent.toolCallRejected': onToolCallRejected, 'agent.contextUsage': onContextUsage,
      'agent.done': onDone, 'agent.error': onError,
    }
    for (const [type, event] of early) {
      if (streamingMessageId.value !== assistantId) break
      const handler = handlers[type] as ((data: BridgeEvents[keyof BridgeEvents]) => void) | undefined
      handler?.(event)
    }
    if (streamingMessageId.value === assistantId) detachStream = detach
  }

  async function sendMessage(content: string, apiConfigId: string, thinkingEnabled = false, thinkingBudget = 8000, model?: string, reasoningEffort?: string, images?: ImageAttachment[], prompt?: string) {
    if (sending.value || streamingMessageId.value) throw new Error('已有对话正在生成')
    if (!content.trim()) throw new Error('消息不能为空')
    const attachments = images?.length ? images : undefined
    sending.value = true
    let conversationId = ''
    let assistantId = ''
    try {
      conversationId = await ensureConversation()
      const userMessage: Message = { id: crypto.randomUUID(), role: 'user', content, createdAt: new Date().toISOString(), status: 'done', images: attachments }
      assistantId = crypto.randomUUID()
      const assistantMessage: Message = { id: assistantId, role: 'assistant', content: '', createdAt: new Date().toISOString(), status: 'streaming', toolCalls: [] }
      selectionVersion++
      isLoading.value = false
      messages.value = [...messages.value, userMessage, assistantMessage]
      streamingMessageId.value = assistantId

      // Backend events can arrive before startStream resolves; replay them after listeners attach.
      const early: Array<[keyof BridgeEvents, BridgeEvents[keyof BridgeEvents]]> = []
      const buffer = (type: keyof BridgeEvents, event: BridgeEvents[keyof BridgeEvents]) => { early.push([type, event]) }
      bridgeEvents.on('*', buffer)
      let streamId: string
      try {
        streamId = await invoke<string>('chat.startStream', {
          conversationId,
          message: content,
          prompt: prompt ?? null,
          apiConfigId,
          thinkingEnabled,
          thinkingBudget,
          model: model ?? null,
          reasoningEffort: reasoningEffort ?? null,
          images: attachments ?? null,
        })
      } catch (error) {
        bridgeEvents.off('*', buffer)
        // Nothing was persisted server-side; drop the optimistic bubbles.
        messages.value = messages.value.filter(message => message.id !== userMessage.id && message.id !== assistantId)
        streamingMessageId.value = null
        throw error
      }
      bridgeEvents.off('*', buffer)
      activeStreamId.value = streamId

      attachStream(streamId, conversationId, assistantId, content, early)
    } finally {
      sending.value = false
    }
  }

  async function cancelStream() {
    const streamId = activeStreamId.value
    const messageId = streamingMessageId.value
    if (!streamId) return
    try {
      await invoke('chat.cancelStream', { streamId })
    } catch (error) {
      // Keep the stream attached when cancellation was not acknowledged.
      throw error
    }
    if (activeStreamId.value && activeStreamId.value !== streamId) return
    detachStream?.()
    const message = findMessage(messageId ?? '')
    if (message && message.status === 'streaming') message.status = 'done'
    if (message) message.finishReason = 'cancelled'
    settleTools(message, 'cancelled')
    settleReasoning(message, Date.now(), true)
    messages.value = messages.value.map(item => item.id === messageId ? { ...item } : item)
    streamingMessageId.value = null
    activeStreamId.value = null
  }

  // A background child finished or a wake-up turn ended without a user-initiated stream.
  bridgeEvents.on('tasks.updated', async data => {
    if (!data.conversationId || data.conversationId !== activeId.value || sending.value || activeStreamId.value) return
    const target = data.conversationId
    const version = selectionVersion
    try {
      const loaded = await invoke<Message[]>('conversations.getMessages', { conversationId: target })
      if (version === selectionVersion && target === activeId.value && !sending.value && !activeStreamId.value) messages.value = restoreMessages(loaded)
    } catch {
      // Transient read failure; the next refresh will pick it up.
    }
  })

  /** The backend restarted or switched workspace: reload the list and keep the open view consistent. */
  async function refreshAfterBackendChange() {
    if (sending.value) return
    const version = ++selectionVersion
    const list = await invoke<Conversation[]>('conversations.list')
    if (version !== selectionVersion || sending.value) return
    conversations.value = list
    const id = activeId.value
    if (!id || !list.some(conversation => conversation.id === id)) {
      detachStream?.()
      activeStreamId.value = null
      streamingMessageId.value = null
      activeId.value = null
      messages.value = []
      todos.value = []
      contextUsage.value = null
      isLoading.value = false
      return
    }
    await loadConversationView(id, version)
  }

  return {
    conversations, activeId, active, messages, todos, contextUsage,
    sending, streamingMessageId, activeStreamId, isLoading,
    loadAll, selectConversation, createConversation, renameConversation,
    deleteConversation, setTodos, sendMessage, cancelStream, refreshAfterBackendChange,
  }
})
