import { StatusDot } from './ui/status-dot'
import { t } from '@/i18n'

/** Marks a conversation row that has a run in flight. Switching sessions never stops a run,
 * so the list has to say which ones are still working (and which are waiting for the person). */
export function runStateLabel(state: string | null): string {
  switch (state) {
    case 'running': return t('运行中', 'Running')
    case 'awaiting-approval': return t('需要授权', 'Approval needed')
    case 'awaiting-answer': return t('需要回答', 'Answer needed')
    case 'awaiting-plan': return t('等待计划审批', 'Plan approval pending')
    default: return t('运行中', 'Running')
  }
}

export function RunMarker({ state }: { state: string | null }) {
  if (!state) return null
  const waiting = state === 'awaiting-approval' || state === 'awaiting-answer' || state === 'awaiting-plan'
  const label = runStateLabel(state)
  return <span className={`run-marker${waiting ? ' is-waiting' : ''}`} title={label}>
    <StatusDot animate={!waiting} size="sm" tone={waiting ? 'warning' : 'active'} />
    <em>{label}</em>
  </span>
}
