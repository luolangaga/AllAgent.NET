import { StatusDot } from './ui/status-dot'

const labels: Record<string, string> = {
  running: '运行中',
  'awaiting-approval': '需要授权',
  'awaiting-answer': '需要回答',
}

/** Marks a conversation row that has a run in flight. Switching sessions never stops a run,
 * so the list has to say which ones are still working (and which are waiting for the person). */
export function RunMarker({ state }: { state: string | null }) {
  if (!state) return null
  const waiting = state === 'awaiting-approval' || state === 'awaiting-answer'
  const label = labels[state] ?? '运行中'
  return <span className={`run-marker${waiting ? ' is-waiting' : ''}`} title={label}>
    <StatusDot animate={!waiting} size="sm" tone={waiting ? 'warning' : 'active'} />
    <em>{label}</em>
  </span>
}
