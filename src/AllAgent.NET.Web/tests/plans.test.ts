import { beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params?: any }
const host = window as unknown as { chrome?: { webview: { postMessage(json: string): void } }; __bridgeCallback(json: string): void }
let handle: (request: Request) => void
const reply = (request: Request, result: unknown, error?: string) => host.__bridgeCallback(JSON.stringify({ id: request.id, result, error }))
const push = (payload: unknown) => host.__bridgeCallback(JSON.stringify(payload))

const proposal = {
  type: 'agent.planProposed',
  id: 'plan-1',
  conversationId: 'c1',
  title: 'Add plan mode',
  markdown: '# Plan\n\n1. Read\n2. Implement',
  status: 'proposed',
  createdAt: '2026-10-19T00:00:00Z',
}

beforeEach(() => {
  vi.resetModules()
  host.chrome = { webview: { postMessage: json => handle(JSON.parse(json)) } }
  handle = request => reply(request, true)
})

describe('plan approvals', () => {
  it('collects a proposed plan once and resolves it through plan.respond', async () => {
    const requests: Request[] = []
    handle = request => { requests.push(request); return reply(request, true) }
    const { useApprovalsStore } = await import('../src/stores/approvals')
    const store = useApprovalsStore()
    push(proposal)
    push(proposal)
    expect(store.plans).toHaveLength(1)
    expect(store.plans[0]).toMatchObject({ id: 'plan-1', title: 'Add plan mode', status: 'proposed' })

    await store.respondPlan('plan-1', true)
    const call = requests.find(request => request.method === 'plan.respond')
    expect(call?.params).toMatchObject({ planId: 'plan-1', approved: true })
    expect(store.plans).toHaveLength(0)
  })

  it('sends the human comment with the plan decision', async () => {
    const requests: Request[] = []
    handle = request => { requests.push(request); return reply(request, true) }
    const { useApprovalsStore } = await import('../src/stores/approvals')
    const store = useApprovalsStore()
    push(proposal)

    await store.respondPlan('plan-1', false, 'Add a rollback step')
    expect(requests.find(request => request.method === 'plan.respond')?.params).toMatchObject({
      planId: 'plan-1',
      approved: false,
      feedback: 'Add a rollback step',
    })
  })

  it('replays pending plans and clears them when another client resolves them', async () => {
    const { useApprovalsStore } = await import('../src/stores/approvals')
    const store = useApprovalsStore()
    handle = request => request.method === 'interactions.pending' ? reply(request, [proposal]) : reply(request, true)
    await store.refresh()
    expect(store.plans).toHaveLength(1)

    push({ type: 'interactions.resolved', id: 'plan-1' })
    expect(store.plans).toHaveLength(0)
  })

  it('keeps the plan pending when the host rejects the response', async () => {
    const { useApprovalsStore } = await import('../src/stores/approvals')
    const store = useApprovalsStore()
    push(proposal)
    handle = request => reply(request, null, '连接中断')
    await expect(store.respondPlan('plan-1', true)).rejects.toThrow('连接中断')
    expect(store.plans).toHaveLength(1)
  })
})
