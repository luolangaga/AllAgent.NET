import type { TaskState } from '@/types'
import { languageTag, t } from '@/i18n'

const numericTaskStates: TaskState[] = [
  'queued', 'running', 'awaitingApproval', 'awaitingAnswer',
  'completed', 'failed', 'cancelled', 'interrupted', 'cancelling',
]

export function taskState(state: TaskState | number): TaskState {
  if (typeof state === 'number') return numericTaskStates[state] ?? 'failed'
  return state
}

export function taskStateLabel(state: TaskState | number): string {
  switch (taskState(state)) {
    case 'queued': return t('排队中', 'Queued')
    case 'running': return t('执行中', 'Running')
    case 'awaitingApproval': return t('等待授权', 'Awaiting approval')
    case 'awaitingAnswer': return t('等待回答', 'Awaiting answer')
    case 'completed': return t('已完成', 'Completed')
    case 'failed': return t('失败', 'Failed')
    case 'cancelled': return t('已取消', 'Cancelled')
    case 'interrupted': return t('已中断', 'Interrupted')
    case 'cancelling': return t('正在取消', 'Cancelling')
  }
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
  return date.toLocaleTimeString(languageTag(), { hour: '2-digit', minute: '2-digit' })
}

export function formatDateTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleString(languageTag(), { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })
}

/** Fixed `yyyy/MM/dd HH:mm` for the commit graph's date column. */
export function formatGitDate(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}/${pad(date.getMonth() + 1)}/${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

export function formatRelative(iso: string): string {
  const date = new Date(iso).getTime()
  if (Number.isNaN(date)) return ''
  const seconds = Math.max(0, Math.round((Date.now() - date) / 1000))
  if (seconds < 60) return t('刚刚', 'just now')
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return t(`${minutes} 分钟前`, `${minutes} min ago`)
  const hours = Math.round(minutes / 60)
  if (hours < 24) return t(`${hours} 小时前`, `${hours} h ago`)
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
