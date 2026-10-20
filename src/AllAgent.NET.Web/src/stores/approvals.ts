import { defineStore, ref } from '@/lib/store'
import type { AgentQuestionRequest, ApprovalRequest, PlanProposalRequest } from '@/types'
import { bridgeEvents, invoke } from '@/bridge'

// Host approvals, agent questions and plan proposals only arrive when the backend
// routes them over the bridge (the browser workspace started from `/web-ui`).
// Desktop builds resolve them with native dialogs and never emit these events.
export const useApprovalsStore = defineStore('approvals', () => {
  const approvals = ref<ApprovalRequest[]>([])
  const questions = ref<AgentQuestionRequest[]>([])
  const plans = ref<PlanProposalRequest[]>([])
  let revision = 0

  // The backend replays prompts that are still pending, so drop repeats by id.
  function addApproval(data: ApprovalRequest) {
    if (approvals.value.some(item => item.requestId === data.requestId)) return
    approvals.value = [...approvals.value, {
      requestId: data.requestId,
      name: data.name,
      risk: data.risk,
      args: data.args,
      source: data.source,
      requiresExplicitApproval: data.requiresExplicitApproval,
    }]
  }

  function addQuestion(data: AgentQuestionRequest) {
    if (questions.value.some(item => item.id === data.id)) return
    questions.value = [...questions.value, { id: data.id, question: data.question, choices: data.choices ?? [], multiple: data.multiple ?? false }]
  }

  function addPlan(data: PlanProposalRequest) {
    if (plans.value.some(item => item.id === data.id)) return
    plans.value = [...plans.value, {
      id: data.id,
      conversationId: data.conversationId,
      title: data.title,
      markdown: data.markdown,
      status: data.status ?? 'proposed',
      createdAt: data.createdAt ?? new Date().toISOString(),
    }]
  }

  bridgeEvents.on('agent.approvalRequest', data => { revision++; addApproval(data) })
  bridgeEvents.on('agent.question', data => { revision++; addQuestion(data) })
  bridgeEvents.on('agent.planProposed', data => { revision++; addPlan(data) })
  bridgeEvents.on('interactions.resolved', ({ id }) => {
    revision++
    approvals.value = approvals.value.filter(item => item.requestId !== id)
    questions.value = questions.value.filter(item => item.id !== id)
    plans.value = plans.value.filter(item => item.id !== id)
  })

  /**
   * Pulls what is still waiting. A run blocked on a prompt outlives the page that saw it,
   * so every load and every reconnect asks instead of relying on the live event only.
   */
  async function refresh(): Promise<void> {
    const version = revision
    const pending = await invoke('interactions.pending') as { type?: string }[] | undefined
    if (version !== revision) return refresh()
    approvals.value = []
    questions.value = []
    plans.value = []
    for (const item of pending ?? []) {
      if (item?.type === 'agent.question') addQuestion(item as unknown as AgentQuestionRequest)
      else if (item?.type === 'agent.approvalRequest') addApproval(item as unknown as ApprovalRequest)
      else if (item?.type === 'agent.planProposed') addPlan(item as unknown as PlanProposalRequest)
    }
  }

  async function respond(requestId: string, approved: boolean) {
    await invoke('agent.approvalRespond', { requestId, approved })
    revision++
    approvals.value = approvals.value.filter(item => item.requestId !== requestId)
  }

  async function answer(id: string, answer: string) {
    await invoke('agent.questionAnswer', { id, answer })
    revision++
    questions.value = questions.value.filter(item => item.id !== id)
  }

  async function respondPlan(id: string, approved: boolean) {
    await invoke('plan.respond', { planId: id, approved })
    revision++
    plans.value = plans.value.filter(item => item.id !== id)
  }

  return { approvals, questions, plans, respond, answer, respondPlan, refresh }
})
