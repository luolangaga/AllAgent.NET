import { defineStore } from '@/lib/store'
import { ref, computed } from '@/lib/store'
import type { Conversation, Message } from '@/types'
import { invoke, bridgeEvents, type BridgeEvents } from '@/bridge'
import { restoreMessages, parseObject } from '@/utils/execution'

export const useConversationsStore = defineStore('conversations', () => {
  const conversations = ref<Conversation[]>([])
  const activeId = ref<string | null>(null)
  const messages = ref<Message[]>([])
  const streamingMessageId = ref<string | null>(null)
  const activeStreamId = ref<string | null>(null)
  const isLoading = ref(false)
  let selectionVersion = 0
  let streamCleanup: (() => void) | undefined
  let starting = false

  const active = computed(() => conversations.value.find(c => c.id === activeId.value) ?? null)

  function findMessage(id: string): Message | undefined {
    return messages.value.find(m => m.id === id)
  }

  async function loadAll() {
    conversations.value = await invoke<Conversation[]>('conversations.list')
  }

  async function selectConversation(id: string) {
    if (starting || streamingMessageId.value) throw new Error('请先停止当前生成')
    const selection = ++selectionVersion
    activeId.value = id
    messages.value = []
    isLoading.value = true
    try {
      const loaded = restoreMessages(await invoke<Message[]>('conversations.getMessages', { conversationId: id }))
      if (selection === selectionVersion) messages.value = loaded
    } finally {
      if (selection === selectionVersion) isLoading.value = false
    }
  }

  async function createConversation() {
    if (streamingMessageId.value) throw new Error('请先停止当前生成')
    const conv = await invoke<Conversation>('conversations.create', { title: 'New Conversation' })
    selectionVersion++
    isLoading.value = false
    conversations.value.unshift(conv)
    activeId.value = conv.id
    messages.value = []
    return conv
  }

  async function renameConversation(id: string, title: string) {
    await invoke('conversations.rename', { id, title })
    const c = conversations.value.find(c => c.id === id)
    if (c) c.title = title
  }

  async function deleteConversation(id: string) {
    if (starting || streamingMessageId.value) throw new Error('请先停止当前生成')
    selectionVersion++
    await invoke('conversations.delete', { id })
    conversations.value = conversations.value.filter(c => c.id !== id)
    if (activeId.value === id) {
      activeId.value = conversations.value[0]?.id ?? null
      messages.value = activeId.value
        ? restoreMessages(await invoke<Message[]>('conversations.getMessages', { conversationId: activeId.value }))
        : []
    }
  }

  async function sendMessage(content: string, apiConfigId: string, thinkingEnabled = false, thinkingBudget = 8000) {
    if (starting || streamingMessageId.value) throw new Error('已有对话正在生成')
    starting = true
    try {
    if (!activeId.value) await createConversation()

    const userMsg: Message = {
      id: crypto.randomUUID(),
      role: 'user',
      content,
      createdAt: new Date().toISOString(),
      status: 'done'
    }
    messages.value.push(userMsg)

    const assistantId = crypto.randomUUID()
    const assistantMsg: Message = {
      id: assistantId,
      role: 'assistant',
      content: '',
      createdAt: new Date().toISOString(),
      status: 'streaming',
      toolCalls: []
    }
    messages.value.push(assistantMsg)
    streamingMessageId.value = assistantId

    // Native events can arrive before the startStream response. Replay them after listeners attach.
    const earlyEvents: Array<{ type: keyof BridgeEvents; data: BridgeEvents[keyof BridgeEvents] }> = []
    const bufferEarlyEvent = (type: keyof BridgeEvents, data: BridgeEvents[keyof BridgeEvents]) => { earlyEvents.push({ type, data }) }
    bridgeEvents.on('*', bufferEarlyEvent)
    let streamId: string
    try { streamId = await invoke<string>('chat.startStream', {
      conversationId: activeId.value,
      message: content,
      apiConfigId,
      thinkingEnabled,
      thinkingBudget,
    }) } catch (error) {
      assistantMsg.status = 'error'
      streamingMessageId.value = null
      throw error
    } finally { bridgeEvents.off('*', bufferEarlyEvent) }
    activeStreamId.value = streamId

    let completedText = ''
    let completedThinking = ''
    const onIteration = (data: { streamId: string }) => {
      if (data.streamId !== streamId) return
      const msg = findMessage(assistantId)
      if (msg) { completedText = msg.content; completedThinking = msg.reasoningContent ?? '' }
    }

    const onToken = (data: { text: string; streamId: string }) => {
      if (data.streamId !== streamId) return
      const msg = findMessage(assistantId)
      if (msg) msg.content += data.text
    }

    const onThinking = (data: { text: string; streamId: string }) => {
      if (data.streamId !== streamId) return
      const msg = findMessage(assistantId)
      if (msg) msg.reasoningContent = (msg.reasoningContent ?? '') + data.text
    }

    const onNotice = (data: { resetResponse: boolean; streamId: string }) => {
      if (data.streamId !== streamId || !data.resetResponse) return
      const msg = findMessage(assistantId)
      if (msg) { msg.content = completedText; msg.reasoningContent = completedThinking }
    }

    const onToolCallStarted = (data: { id: string; name: string; args: string; parentId?: string; streamId: string }) => {
      if (data.streamId !== streamId) return
      const msg = findMessage(assistantId)
      if (msg) {
        if (!msg.toolCalls) msg.toolCalls = []
        msg.toolCalls.push({
          id: data.id,
          parentId: data.parentId,
          name: data.name,
          args: data.args,
          status: 'running',
          outputLines: []
        })
      }
    }

    const onToolCallOutput = (data: { id: string; line: string; isError: boolean; streamId: string }) => {
      if (data.streamId !== streamId) return
      const msg = findMessage(assistantId)
      if (msg) {
        const tc = msg.toolCalls?.find(t => t.id === data.id)
        if (tc) {
          if (!tc.outputLines) tc.outputLines = []
          tc.outputLines.push(data.line)
        }
      }
    }

    const onToolCallCompleted = (data: { id: string; name: string; result: string; streamId: string }) => {
      if (data.streamId !== streamId) return
      const msg = findMessage(assistantId)
      if (msg) {
        const tc = msg.toolCalls?.find(t => t.id === data.id)
        if (tc) { tc.status = parseObject(data.result).ok === false ? 'error' : 'done'; tc.output = data.result }
      }
    }

    const onDone = (data: { reason: string; streamId: string }) => {
      if (data.streamId !== streamId) return
      cleanup()
      const msg = findMessage(assistantId)
      if (msg) msg.status = 'done'
      streamingMessageId.value = null
      activeStreamId.value = null
      const conv = conversations.value.find(c => c.id === activeId.value)
      if (conv && conv.title === 'New Conversation' && content.length > 0) {
        conv.title = content.length > 40 ? content.slice(0, 40) + '…' : content
      }
    }

    const onError = (data: { message: string; streamId: string }) => {
      if (data.streamId !== streamId) return
      cleanup()
      const msg = findMessage(assistantId)
      if (msg) {
        msg.status = 'error'
        msg.content += `\n\n[Error: ${data.message}]`
      }
      streamingMessageId.value = null
      activeStreamId.value = null
    }

    function cleanup() {
      bridgeEvents.off('agent.token', onToken)
      bridgeEvents.off('agent.thinking', onThinking)
      bridgeEvents.off('agent.iteration', onIteration)
      bridgeEvents.off('agent.toolCallStarted', onToolCallStarted)
      bridgeEvents.off('agent.toolCallOutput', onToolCallOutput)
      bridgeEvents.off('agent.toolCallCompleted', onToolCallCompleted)
      bridgeEvents.off('agent.done', onDone)
      bridgeEvents.off('agent.error', onError)
      bridgeEvents.off('agent.notice', onNotice)
    }

    bridgeEvents.on('agent.token', onToken)
    bridgeEvents.on('agent.thinking', onThinking)
    bridgeEvents.on('agent.iteration', onIteration)
    bridgeEvents.on('agent.toolCallStarted', onToolCallStarted)
    bridgeEvents.on('agent.toolCallOutput', onToolCallOutput)
    bridgeEvents.on('agent.toolCallCompleted', onToolCallCompleted)
    bridgeEvents.on('agent.done', onDone)
    bridgeEvents.on('agent.error', onError)
    bridgeEvents.on('agent.notice', onNotice)
    for (const event of earlyEvents) bridgeEvents.emit(event.type, event.data)
    if (activeStreamId.value) streamCleanup = cleanup
    } finally { starting = false }
  }

  async function cancelStream() {
    if (activeStreamId.value) {
      await invoke('chat.cancelStream', { streamId: activeStreamId.value })
      streamCleanup?.()
      streamCleanup = undefined
      const msg = findMessage(streamingMessageId.value ?? '')
      if (msg) msg.status = 'done'
      streamingMessageId.value = null
      activeStreamId.value = null
    }
  }

  return {
    conversations, activeId, active, messages, streamingMessageId, isLoading,
    loadAll, selectConversation, createConversation, renameConversation,
    deleteConversation, sendMessage, cancelStream
  }
})
