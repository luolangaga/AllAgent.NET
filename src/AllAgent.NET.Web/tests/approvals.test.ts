import { beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string }
const host = window as unknown as { chrome?: { webview: { postMessage(json: string): void } }; __bridgeCallback(json: string): void }
let handle: (request: Request) => void
const reply = (request: Request, data: unknown, error?: string) => host.__bridgeCallback(JSON.stringify({ id: request.id, data, error }))
const push = (payload: unknown) => host.__bridgeCallback(JSON.stringify(payload))

beforeEach(() => {
  vi.resetModules()
  host.chrome = { webview: { postMessage: json => handle(JSON.parse(json)) } }
  handle = request => reply(request, true)
})

describe('pending host interactions', () => {
  it('keeps a prompt retryable if submitting the answer fails', async () => {
    const { useApprovalsStore } = await import('../src/stores/approvals')
    const store = useApprovalsStore()
    push({ type: 'agent.approvalRequest', requestId: 'a', name: 'file_write', args: '{}', risk: 'Write' })
    push({ type: 'agent.question', id: 'q', question: '选择', choices: ['一', '二'], multiple: true })
    handle = request => reply(request, null, '连接中断')
    await expect(store.respond('a', true)).rejects.toThrow('连接中断')
    await expect(store.answer('q', '["一","二"]')).rejects.toThrow('连接中断')
    expect(store.approvals).toHaveLength(1)
    expect(store.questions).toHaveLength(1)
    handle = request => reply(request, true)
    await store.respond('a', false)
    await store.answer('q', '["一","二"]')
    expect(store.approvals).toHaveLength(0)
    expect(store.questions).toHaveLength(0)
  })

  it('deduplicates replay and removes prompts cancelled or answered by another client', async () => {
    const { useApprovalsStore } = await import('../src/stores/approvals')
    const store = useApprovalsStore()
    const payload = { type: 'agent.question', id: 'q', question: '选择', choices: [], multiple: false }
    push(payload)
    push(payload)
    expect(store.questions).toHaveLength(1)
    push({ type: 'interactions.resolved', id: 'q' })
    expect(store.questions).toHaveLength(0)
    push(payload)
    handle = request => reply(request, [])
    await store.refresh()
    expect(store.questions).toHaveLength(0)
  })
})
