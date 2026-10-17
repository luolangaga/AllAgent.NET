import type { TaskState } from '@/types'

const numericTaskStates: TaskState[] = [
  'queued', 'running', 'awaitingApproval', 'awaitingAnswer',
  'completed', 'failed', 'cancelled', 'interrupted', 'cancelling',
]

export function taskState(state: TaskState | number): TaskState {
  if (typeof state === 'number') return numericTaskStates[state] ?? 'failed'
  return state
}

const taskStateLabels: Record<TaskState, string> = {
  queued: '排队中',
  running: '执行中',
  awaitingApproval: '等待授权',
  awaitingAnswer: '等待回答',
  completed: '已完成',
  failed: '失败',
  cancelled: '已取消',
  interrupted: '已中断',
  cancelling: '正在取消',
}

export function taskStateLabel(state: TaskState | number): string {
  return taskStateLabels[taskState(state)]
}

export function taskStateTone(state: TaskState | number): 'neutral' | 'active' | 'success' | 'warning' | 'error' {
  switch (taskState(state)) {
    case 'running':
    case 'cancelling': return 'active'
    case 'completed': return 'success'
    case 'awaitingApproval':
    case 'awaitingAnswer': return 'warning'
    case 'failed':
    case 'interrupted': return 'error'
    default: return 'neutral'
  }
}

export function taskIsActive(state: TaskState | number): boolean {
  return ['queued', 'running', 'awaitingApproval', 'awaitingAnswer', 'cancelling'].includes(taskState(state))
}

export function formatClock(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
}

export function formatDateTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })
}

export function formatRelative(iso: string): string {
  const date = new Date(iso).getTime()
  if (Number.isNaN(date)) return ''
  const seconds = Math.max(0, Math.round((Date.now() - date) / 1000))
  if (seconds < 60) return '刚刚'
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} 分钟前`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} 小时前`
  return formatDateTime(iso)
}

export function formatTokens(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return '0'
  if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`
  if (value >= 1_000) return `${(value / 1_000).toFixed(value >= 10_000 ? 0 : 1)}k`
  return String(Math.round(value))
}

export function clip(text: string, length: number): string {
  const trimmed = text.trim()
  return trimmed.length > length ? `${trimmed.slice(0, length)}…` : trimmed
}

export function providerLabel(providerId: string): string {
  switch (providerId.toLowerCase()) {
    case 'openai': return 'OpenAI'
    case 'anthropic': return 'Anthropic'
    case 'gemini': return 'Google Gemini'
    case 'ollama': return 'Ollama'
    default: return providerId
  }
}
