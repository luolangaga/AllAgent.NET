import { beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params: Record<string, unknown> }
const host = window as unknown as {
  chrome: { webview: { postMessage(json: string): void } }
  __bridgeCallback(json: string): void
}
let handle: (request: Request) => void
let calls: Request[] = []
const reply = (request: Request, result: unknown) => host.__bridgeCallback(JSON.stringify({ id: request.id, result }))
const event = (eventName: string, data: unknown) =>
  host.__bridgeCallback(JSON.stringify({ type: 'event', eventName, data, streamId: 'stream-c' }))

beforeEach(() => {
  vi.resetModules()
  calls = []
  host.chrome = { webview: { postMessage(json: string) { handle(JSON.parse(json) as Request) } } }
  handle = request => {
    calls.push(request)
    if (request.method === 'conversations.create') {
      reply(request, { id: 'conv-1', title: 'New Conversation', modelId: '', providerId: '', createdAt: '', updatedAt: '' })
    } else if (request.method === 'chat.compact') reply(request, 'stream-c')
    else reply(request, true)
  }
})

describe('/compact in the web UI', () => {
  it('is no longer terminal-only and runs through chat.compact', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const { isTerminalOnly, runCommand } = await import('../src/lib/commandRouter')
    expect(isTerminalOnly('/compact')).toBe(false)

    const store = useConversationsStore()
    await store.createConversation()
    const consumed = await runCommand('/compact 数据库迁移', { go: () => {}, openPalette: () => {} }, { apiConfigId: 'config-9' })
    expect(consumed).toBe(true)

    const request = calls.find(item => item.method === 'chat.compact')
    expect(request?.params).toMatchObject({ conversationId: 'conv-1', apiConfigId: 'config-9', focus: '数据库迁移' })

    // The run streams into a transient assistant bubble: no user message is persisted.
    expect(store.messages.map(message => message.role)).toEqual(['assistant'])
    expect(store.messages.at(-1)).toMatchObject({ status: 'streaming' })
    event('agent.notice', { message: '正在整理上下文；完整消息历史仍保留。' })
    expect(store.messages.at(-1)?.parts?.at(-1)).toMatchObject({ type: 'notice', text: '正在整理上下文；完整消息历史仍保留。' })
    event('agent.notice', { message: '上下文已整理，完整历史仍可查看。' })
    // The paired notices update one line instead of staking up two banners.
    expect(store.messages.at(-1)?.parts?.at(-1)).toMatchObject({ type: 'notice', text: '上下文已整理，完整历史仍可查看。' })
    event('agent.done', { reason: 'stop' })
    expect(store.messages.at(-1)?.status).toBe('done')
    expect(store.activeStreamId).toBeNull()
  })

  it('drops the transient bubble when the host rejects the compaction', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const { runCommand } = await import('../src/lib/commandRouter')
    handle = request => {
      calls.push(request)
      if (request.method === 'conversations.create') reply(request, { id: 'conv-1', title: 'New Conversation', modelId: '', providerId: '', createdAt: '', updatedAt: '' })
      else if (request.method === 'chat.compact') host.__bridgeCallback(JSON.stringify({ id: request.id, error: '会话为空，无需整理。' }))
      else reply(request, true)
    }
    const store = useConversationsStore()
    await store.createConversation()
    await runCommand('/compact', { go: () => {}, openPalette: () => {} }, { apiConfigId: 'config-9' })
    expect(store.messages).toHaveLength(0)
    expect(store.activeStreamId).toBeNull()
  })
})
