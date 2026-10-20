import { defineStore, ref, computed } from '@/lib/store'
import type { ContextUsage, Conversation, ConversationUsage, ImageAttachment, InputDelivery, Message, NoticePart, PendingInput, PlanDocument, RunningConversation, TodoItem, ToolCall } from '@/types'
import { bridgeEvents, invoke, type BridgeEvents } from '@/bridge'
import { parseObject, restoreMessages, toolFailed } from '@/utils/execution'

export const useConversationsStore = defineStore('conversations', () => {
  const conversations = ref<Conversation[]>([])
  const activeId = ref<string | null>(null)
  const messages = ref<Message[]>([])
  const todos = ref<TodoItem[]>([])
  // The latest approved/proposed plan persisted with this conversation. Markdown is
  // rendered to HTML by the Markdown component, so the terminal and browser share one source.
  const plan = ref<PlanDocument | null>(null)
  // Prompts parked while this conversation is running: steers reach the next model step,
  // queued items start their own turn once the run finishes.
  const pendingInputs = ref<PendingInput[]>([])
  const sending = ref(false)
  const streamingMessageId = ref<string | null>(null)
  const activeStreamId = ref<string | null>(null)
  const contextUsage = ref<ContextUsage | null>(null)
  // Cache hit rate and the persisted context count. The live stream only reports context
  // usage, so this is what keeps the numbers visible after a reload or a conversation switch.
  const conversationUsage = ref<ConversationUsage | null>(null)
  const isLoading = ref(false)
  // Sessions running in the background, in this workspace or any other one the host has
  // open. Switching away never stops a run, so this is what the list and dock mark.
  const running = ref<RunningConversation[]>([])

  // Every mutation that replaces the message list bumps this counter; late
  // history loads may only apply when their version is still current.
  let selectionVersion = 0
  let detachStream: (() => void) | null = null

  const active = computed(() => conversations.value.find(item => item.id === activeId.value) ?? null)
  const runningIds = computed(() => new Set(running.value.map(run => run.conversationId)))

  function isRunning(id?: string | null) { return !!id && runningIds.value.has(id) }
  function runState(id: string) { return running.value.find(run => run.conversationId === id)?.state ?? null }
  function runsIn(dataDirectory: string) { return running.value.filter(run => run.dataDirectory === dataDirectory) }
  /** True while this conversation has a run in flight — whether this page is watching it or the host reports it. */
  function isBusy(id: string) { return isRunning(id) || (activeId.value === id && (streamingMessageId.value !== null || activeStreamId.value !== null)) }

  async function loadRunning() { running.value = await invoke<RunningConversation[]>('conversations.running') }

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

  // Retry notices describe a live connection problem; they settle the moment the
  // stream moves again, so the transcript never keeps claiming a failure.
  function settleNotices(message: Message | undefined, status: 'settled' | 'failed' = 'settled') {
    for (const part of message?.parts ?? []) {
      if (part.type !== 'notice' || part.kind !== 'retry' || part.status !== 'pending') continue
      part.status = status
    }
  }

  function lastPendingNotice(parts: Message['parts']): NoticePart | undefined {
    for (let index = (parts?.length ?? 0) - 1; index >= 0; index -= 1) {
      const part = parts![index]
      if (part.type === 'notice' && part.kind === 'retry' && part.status === 'pending') return part
    }
    return undefined
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

  /** Cache hit rate and stored context count; optional chrome, so a failure stays silent. */
  async function loadUsage(id: string) {
    try {
      const usage = await invoke<ConversationUsage>('conversations.usage', { conversationId: id })
      if (activeId.value === id) conversationUsage.value = usage
    } catch {
      // A deleted or still-unwritten conversation simply has no counters yet.
    }
  }

  /** Optional chrome: an older host may not serve plans, and the transcript must still load. */
  async function loadPlan(id: string) {
    try {
      const current = await invoke<PlanDocument | null>('plans.get', { conversationId: id })
      if (activeId.value === id) plan.value = current
    } catch {
      // The plan panel stays empty; the run itself is unaffected.
    }
  }

  async function selectConversation(id: string) {
    // Starting a run owns the optimistic bubbles for a few milliseconds; leaving the
    // stream behind (below) is what keeps the run itself alive.
    if (sending.value) throw new Error('消息提交中，请稍后再切换会话')
    const version = ++selectionVersion
    detachStream?.()
    streamingMessageId.value = null
    activeStreamId.value = null
    activeId.value = id
    messages.value = []
    todos.value = []
    plan.value = null
    pendingInputs.value = []
    contextUsage.value = null
    conversationUsage.value = null
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
    void loadUsage(id)
    void loadPlan(id)
    const early: StreamEvent[] = []
    const buffer = (type: keyof BridgeEvents, event: BridgeEvents[keyof BridgeEvents]) => { early.push([type, event]) }
    bridgeEvents.on('*', buffer)
    try {
      const [checklist, pending, state] = await Promise.all([
        invoke<TodoItem[]>('todos.get', { conversationId: id }),
        invoke<PendingInput[]>('chat.pendingInputs', { conversationId: id }),
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
        pendingInputs.value = pending
        if (state?.streamId) {
          const assistantId = `stream-${state.streamId}`
          messages.value = [...restoreMessages(state.history),
            { id: `input-${state.streamId}`, role: 'user', content: state.input, createdAt: state.createdAt, status: 'done' },
            { id: assistantId, role: 'assistant', content: '', createdAt: state.createdAt, status: 'streaming', toolCalls: [] }]
          streamingMessageId.value = assistantId
          activeStreamId.value = state.streamId
          const replay: StreamEvent[] = state.events.map(item => [item.eventName, { ...item.data, streamId: state.streamId, sequence: item.sequence, timestamp: item.timestamp } as BridgeEvents[keyof BridgeEvents]])
          attachStream(state.streamId, id, assistantId, [...replay, ...early])
        } else {
          messages.value = restoreMessages(loaded)
        }
      }
    } finally {
      bridgeEvents.off('*', buffer)
      if (version === selectionVersion) isLoading.value = false
    }
  }

  /** Swap the view; a null conversation is an unsaved draft that the first message creates. */
  function resetView(conversation: Conversation | null) {
    selectionVersion++
    detachStream?.()
    streamingMessageId.value = null
    activeStreamId.value = null
    if (conversation) conversations.value = [conversation, ...conversations.value.filter(item => item.id !== conversation.id)]
    activeId.value = conversation?.id ?? null
    messages.value = []
    todos.value = []
    plan.value = null
    pendingInputs.value = []
    contextUsage.value = null
    conversationUsage.value = null
    isLoading.value = false
  }

  // A new conversation never interrupts a run in progress: the previous session keeps
  // working in the background and is listed as running. Nothing is persisted here —
  // creating a session for a view the user may never use only litters the history.
  function newConversation() {
    if (sending.value) throw new Error('消息正在提交中，请稍候再新建会话。')
    resetView(null)
  }

  /** Internal helper for sendMessage: creating a conversation is part of the send. */
  async function ensureConversation(): Promise<string> {
    if (activeId.value) return activeId.value
    const created = await invoke<Conversation>('conversations.create', { title: 'New Conversation' })
    resetView(created)
    return created.id
  }

  /** Refresh the parked input list (reload, run end, or an input admitted into history). */
  async function loadPending(id = activeId.value) {
    if (!id) return
    try {
      const items = await invoke<PendingInput[]>('chat.pendingInputs', { conversationId: id })
      if (activeId.value === id) pendingInputs.value = items
    } catch {
      // Optional chrome: an offline host must not break the transcript.
    }
  }

  /**
   * Submit while a turn is running. `steer` interrupts the current plan at the next
   * model step; `queue` starts a fresh turn after the run ends. The draft stays in
   * the input box only if the host rejects it.
   */
  async function enqueueMessage(content: string, delivery: InputDelivery, images?: ImageAttachment[], prompt?: string) {
    if (!content.trim()) throw new Error('消息不能为空')
    const conversationId = await ensureConversation()
    const item = await invoke<PendingInput>('chat.enqueueInput', {
      conversationId,
      message: content,
      delivery,
      prompt: prompt ?? null,
      images: images?.length ? images : null,
    })
    if (activeId.value === conversationId) {
      pendingInputs.value = [...pendingInputs.value.filter(existing => existing.id !== item.id), item]
    }
    return item
  }

  async function cancelPending(id: string) {
    const conversationId = activeId.value
    if (!conversationId) return
    await invoke('chat.cancelPendingInput', { conversationId, id })
    pendingInputs.value = pendingInputs.value.filter(item => item.id !== id)
  }

  /**
   * Deliver the next parked prompt as its own turn. The engine promotes steers first
   * and otherwise takes one queued item; events arrive on a fresh stream.
   */
  async function runPending(apiConfigId: string, thinkingEnabled = false, thinkingBudget = 8000, model?: string, reasoningEffort?: string) {
    if (sending.value || streamingMessageId.value || !activeId.value) return
    const conversationId = activeId.value
    const assistantId = crypto.randomUUID()
    sending.value = true
    try {
      messages.value = [...messages.value, {
        id: assistantId, role: 'assistant', content: '', createdAt: new Date().toISOString(), status: 'streaming', toolCalls: [],
      }]
      streamingMessageId.value = assistantId
      const early: StreamEvent[] = []
      const buffer = (type: keyof BridgeEvents, event: BridgeEvents[keyof BridgeEvents]) => { early.push([type, event]) }
      bridgeEvents.on('*', buffer)
      let streamId: string | null
      try {
        streamId = await invoke<string | null>('chat.runPending', {
          conversationId, apiConfigId, thinkingEnabled, thinkingBudget,
          model: model ?? null, reasoningEffort: reasoningEffort ?? null,
        })
      } catch (error) {
        bridgeEvents.off('*', buffer)
        messages.value = messages.value.filter(message => message.id !== assistantId)
        if (streamingMessageId.value === assistantId) streamingMessageId.value = null
        throw error
      }
      bridgeEvents.off('*', buffer)
      if (!streamId) {
        messages.value = messages.value.filter(message => message.id !== assistantId)
        if (streamingMessageId.value === assistantId) streamingMessageId.value = null
        await loadPending(conversationId)
        return
      }
      activeStreamId.value = streamId
      attachStream(streamId, conversationId, assistantId, early)
    } finally {
      sending.value = false
    }
  }

  async function renameConversation(id: string, title: string) {
    await invoke('conversations.rename', { id, title })
    conversations.value = conversations.value.map(item => (item.id === id ? { ...item, title } : item))
  }

  // The host mirrors an untitled conversation as "新对话"/"New conversation" in
  // conversations.list, so both that fallback and the raw creation title count as untitled.
  const untitled = new Set(['', 'New Conversation', '新对话', 'New conversation'])

  /** Name a conversation from its first prompt at send time, not when the run ends. */
  function applyInitialTitle(id: string, content: string) {
    const conversation = conversations.value.find(item => item.id === id)
    const line = content.replace(/\n/g, ' ').trim()
    if (!conversation || !line || !untitled.has(conversation.title)) return
    const title = line.length > 40 ? `${line.slice(0, 40)}…` : line
    conversation.title = title
    void invoke('conversations.rename', { id, title }).catch(() => {})
  }

  async function deleteConversation(id: string) {
    if (isBusy(id)) throw new Error('该会话正在运行，请先停止再删除')
    await invoke('conversations.delete', { id })
    conversations.value = conversations.value.filter(item => item.id !== id)
    if (activeId.value !== id) return
    selectionVersion++
    activeId.value = null
    messages.value = []
    todos.value = []
    pendingInputs.value = []
    contextUsage.value = null
    conversationUsage.value = null
    isLoading.value = false
    const next = conversations.value[0]?.id
    if (next) await selectConversation(next)
  }

  async function setTodos(items: TodoItem[]) {
    if (!activeId.value) throw new Error('请先选择会话')
    if (isBusy(activeId.value)) throw new Error('该会话正在运行，请先停止再编辑 TODO')
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

  function attachStream(streamId: string, conversationId: string, assistantId: string, early: StreamEvent[]) {
    let lastSequence = 0
    // A steer admitted mid-run closes the current answer bubble and continues in a
    // fresh one, so the user's guidance stays at the point it took effect.
    let currentAssistantId = assistantId
    const current = (eventStreamId?: string, sequence?: number) => {
      if (eventStreamId !== streamId) return false
      if (sequence !== undefined) {
        if (sequence <= lastSequence) return false
        lastSequence = sequence
      }
      // A new message reference lets React memoize historical bubbles while
      // this one continues to stream. Nested tool state stays observable.
      messages.value = messages.value.map(message => message.id === currentAssistantId ? { ...message } : message)
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
      const message = findMessage(currentAssistantId)
      if (message) {
        settleReasoning(message, eventTime(data))
        message.content += data.text
        settleNotices(message)
        const parts = message.parts ?? (message.parts = [])
        const last = parts.at(-1)
        if (last?.type === 'text') last.text += data.text
        else parts.push({ type: 'text', text: data.text })
      }
    }
    const onThinking = (data: BridgeEvents['agent.thinking']) => {
      if (!current(data.streamId, data.sequence)) return
      if (!data.text) return
      const message = findMessage(currentAssistantId)
      if (message) {
        message.reasoningContent = (message.reasoningContent ?? '') + data.text
        settleNotices(message)
        const parts = message.parts ?? (message.parts = [])
        const previous = parts.at(-1)
        if (previous?.type === 'reasoning' && previous.status === 'streaming') previous.text += data.text
        else parts.push({ type: 'reasoning', text: data.text, iteration: message.iteration?.current ?? 1, status: 'streaming', startedAt: eventTime(data) })
      }
    }
    const onIteration = (data: BridgeEvents['agent.iteration']) => {
      if (!current(data.streamId, data.sequence)) return
      const message = findMessage(currentAssistantId)
      if (message) {
        settleReasoning(message, eventTime(data))
        settleNotices(message)
        message.iteration = { current: data.current, maximum: data.maximum, toolCalls: data.toolCalls }
        completedText = message.content
        completedThinking = message.reasoningContent ?? ''
        completedParts = message.parts?.map(part => ({ ...part })) ?? []
      }
    }
    // A notice is a timeline entry, not model output: retries roll the partial
    // answer back but keep their notice, and one line covers consecutive attempts.
    const onNotice = (data: BridgeEvents['agent.notice']) => {
      if (!current(data.streamId, data.sequence)) return
      const message = findMessage(currentAssistantId)
      if (!message) return
      const text = data.message ?? ''
      if (data.resetResponse) {
        const notices = (message.parts ?? []).filter(part => part.type === 'notice')
        message.content = completedText
        message.reasoningContent = completedThinking
        message.parts = [...completedParts.map(part => ({ ...part })), ...notices]
      }
      const parts = message.parts ?? (message.parts = [])
      if (!data.resetResponse) {
        // Bookkeeping notices (compaction, stripped images) update in place so a
        // pair like「正在整理上下文」/「上下文已整理」stays a single line.
        const last = parts.at(-1)
        if (last?.type === 'notice' && last.kind === 'info') last.text = text
        else parts.push({ type: 'notice', id: crypto.randomUUID(), text, kind: 'info', status: 'settled', attempts: 1 })
        return
      }
      const pending = lastPendingNotice(parts)
      if (pending) {
        pending.text = text
        pending.attempts += 1
      } else {
        parts.push({ type: 'notice', id: crypto.randomUUID(), text, kind: 'retry', status: 'pending', attempts: 1 })
      }
    }
    // A steer admitted at a step boundary becomes a normal user turn in the timeline;
    // the answer that follows streams into a fresh assistant bubble.
    const onInputAdmitted = (data: BridgeEvents['agent.inputAdmitted']) => {
      if (!current(data.streamId, data.sequence)) return
      const previous = findMessage(currentAssistantId)
      if (previous) {
        settleReasoning(previous, Date.now())
        settleNotices(previous)
        if (previous.status === 'streaming') previous.status = 'done'
      }
      const admitted: Message = { ...data.message, status: 'done' }
      if (!admitted.parts?.length) {
        admitted.parts = admitted.content ? [{ type: 'text', text: admitted.content }] : undefined
      }
      const nextId = crypto.randomUUID()
      const next: Message = {
        id: nextId, role: 'assistant', content: '', createdAt: new Date().toISOString(), status: 'streaming', toolCalls: [],
      }
      messages.value = [...messages.value.filter(message => message.id !== admitted.id), admitted, next]
      currentAssistantId = nextId
      streamingMessageId.value = nextId
      completedText = ''
      completedThinking = ''
      completedParts = []
      pendingInputs.value = pendingInputs.value.filter(item => item.id !== data.message.id)
    }
    function ensureTool(id: string, name = 'tool'): ToolCall | undefined {
      const message = findMessage(currentAssistantId)
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
      settleReasoning(findMessage(currentAssistantId), eventTime(data))
      settleNotices(findMessage(currentAssistantId))
      const call = ensureTool(data.id, data.name)
      if (call) { call.name = data.name; call.args = data.args; call.parentId = data.parentId }
    }
    const onToolCallOutput = (data: BridgeEvents['agent.toolCallOutput']) => {
      if (!current(data.streamId, data.sequence)) return
      settleReasoning(findMessage(currentAssistantId), eventTime(data))
      const call = ensureTool(data.id)
      if (call) call.outputLines = [...(call.outputLines ?? []), data.isError ? `[stderr] ${data.line}` : data.line].slice(-400)
    }
    const onToolCallCompleted = (data: BridgeEvents['agent.toolCallCompleted']) => {
      if (!current(data.streamId, data.sequence)) return
      settleReasoning(findMessage(currentAssistantId), eventTime(data))
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
      const message = findMessage(currentAssistantId)
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
      const message = findMessage(currentAssistantId)
      if (message) {
        message.finishReason = data.reason
        if (data.reason === 'error' || data.reason === 'protocol_error' || data.reason === 'timeout') message.status = 'error'
        if (data.reason === 'timeout') message.error = '执行已超时，已完成的结果保留，可输入“继续”。'
      }
      settleTools(message, data.reason)
      settleNotices(message, ['error', 'timeout', 'protocol_error'].includes(data.reason) ? 'failed' : 'settled')
      settleReasoning(message, eventTime(data), ['cancelled', 'error', 'timeout', 'protocol_error'].includes(data.reason))
      finish('done')
    }
    const onError = (data: BridgeEvents['agent.error']) => {
      if (!current(data.streamId, data.sequence)) return
      const message = findMessage(currentAssistantId)
      if (message) {
        message.status = 'error'
        message.error = data.message
      }
      settleTools(message, 'error')
      settleNotices(message, 'failed')
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
      bridgeEvents.off('agent.inputAdmitted', onInputAdmitted)
      bridgeEvents.off('agent.done', onDone)
      bridgeEvents.off('agent.error', onError)
      if (detachStream === detach) detachStream = null
    }

    function finish(status: 'done' | null) {
      detach()
      if (status) {
        const message = findMessage(currentAssistantId)
        if (message && message.status === 'streaming') message.status = 'done'
      }
      if (streamingMessageId.value === currentAssistantId) {
        streamingMessageId.value = null
        activeStreamId.value = null
      }
      // Usage is persisted as the run reports it: refetch so the cache line lands with the answer.
      void loadUsage(conversationId)
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
    bridgeEvents.on('agent.inputAdmitted', onInputAdmitted)
    bridgeEvents.on('agent.done', onDone)
    bridgeEvents.on('agent.error', onError)
    const handlers: Partial<{ [K in keyof BridgeEvents]: (data: BridgeEvents[K]) => void }> = {
      'agent.token': onToken, 'agent.thinking': onThinking, 'agent.iteration': onIteration,
      'agent.notice': onNotice, 'agent.toolCallStarted': onToolCallStarted,
      'agent.toolCallOutput': onToolCallOutput, 'agent.toolCallCompleted': onToolCallCompleted,
      'agent.toolCallRejected': onToolCallRejected, 'agent.contextUsage': onContextUsage,
      'agent.inputAdmitted': onInputAdmitted, 'agent.done': onDone, 'agent.error': onError,
    }
    for (const [type, event] of early) {
      if (streamingMessageId.value !== currentAssistantId) break
      const handler = handlers[type] as ((data: BridgeEvents[keyof BridgeEvents]) => void) | undefined
      handler?.(event)
    }
    if (streamingMessageId.value === currentAssistantId) detachStream = detach
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
      applyInitialTitle(conversationId, content)
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

      attachStream(streamId, conversationId, assistantId, early)
    } finally {
      sending.value = false
    }
  }

  /**
   * Summarise older context into a continuation summary — the browser's /compact.
   * Unlike a normal turn nothing is appended to the visible transcript: the run
   * streams an AgentNotice and the summary replaces the model-side history.
   */
  async function compact(apiConfigId: string, focus = '') {
    if (sending.value || streamingMessageId.value) throw new Error('已有对话正在生成，请先等待或停止')
    const conversationId = activeId.value
    if (!conversationId) throw new Error('当前没有对话，无法整理上下文')
    if (isRunning(conversationId)) throw new Error('该会话正在运行，请先停止再整理上下文')
    if (!apiConfigId) throw new Error('先连接一个可用的模型配置，再整理上下文')
    sending.value = true
    const assistantId = crypto.randomUUID()
    try {
      selectionVersion++
      isLoading.value = false
      messages.value = [...messages.value, {
        id: assistantId, role: 'assistant', content: '', createdAt: new Date().toISOString(),
        status: 'streaming', toolCalls: [],
      }]
      streamingMessageId.value = assistantId
      const early: StreamEvent[] = []
      const buffer = (type: keyof BridgeEvents, event: BridgeEvents[keyof BridgeEvents]) => { early.push([type, event]) }
      bridgeEvents.on('*', buffer)
      let streamId: string
      try {
        streamId = await invoke<string>('chat.compact', { conversationId, apiConfigId, focus: focus || null })
      } catch (error) {
        bridgeEvents.off('*', buffer)
        messages.value = messages.value.filter(message => message.id !== assistantId)
        streamingMessageId.value = null
        throw error
      }
      bridgeEvents.off('*', buffer)
      activeStreamId.value = streamId
      attachStream(streamId, conversationId, assistantId, early)
      // Compaction moves the summary boundary, so the old meter is stale until the next turn.
      contextUsage.value = null
      conversationUsage.value = null
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

  /** Stops a run by conversation id — the dock uses this for sessions in other workspaces. */
  async function cancelRun(conversationId: string) {
    await invoke('conversations.cancelRun', { conversationId })
    await loadRunning()
  }

  /** The backend restarted or switched workspace: reload the list and keep the open view consistent. */
  async function refreshAfterBackendChange() {
    if (sending.value) return
    const version = ++selectionVersion
    const list = await invoke<Conversation[]>('conversations.list')
    void loadRunning().catch(() => {})
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
      plan.value = null
      pendingInputs.value = []
      contextUsage.value = null
      conversationUsage.value = null
      isLoading.value = false
      return
    }
    await loadConversationView(id, version)
  }

  // Live run state for every workspace: start, finish and "waiting for you" are pushed
  // as they happen, so the sidebar and the dock never need to poll.
  bridgeEvents.on('runs.changed', data => { running.value = data.conversations ?? [] })

  // Plan proposals arrive as top-level notifications; the persisted record is
  // reloaded on conversation switch and after the decision.
  bridgeEvents.on('agent.planProposed', data => {
    if (data.conversationId && data.conversationId !== activeId.value) return
    plan.value = {
      id: data.id,
      title: data.title,
      markdown: data.markdown,
      status: data.status ?? 'proposed',
      createdAt: data.createdAt ?? new Date().toISOString(),
      decidedAt: null,
    }
  })
  bridgeEvents.on('plans.updated', data => {
    if (data.conversationId && data.conversationId !== activeId.value) return
    plan.value = data.plan
  })

  return {
    conversations, activeId, active, messages, todos, plan, pendingInputs, contextUsage, conversationUsage,
    sending, streamingMessageId, activeStreamId, isLoading, running,
    isRunning, runState, runsIn, loadRunning, cancelRun,
    loadAll, selectConversation, newConversation, renameConversation,
    deleteConversation, setTodos, sendMessage, compact, cancelStream, refreshAfterBackendChange,
    loadPending, enqueueMessage, cancelPending, runPending,
  }
})
