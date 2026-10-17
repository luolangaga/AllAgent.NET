import { useEffect, useState } from 'react'
import { ReasoningSteps, ReasoningStepsContent, ReasoningStepsTrigger, ReasoningStep } from './ui/reasoning-steps'
import { Markdown } from './Markdown'
import type { ReasoningPart } from '@/types'
import { clip } from '@/lib/format'

export function ReasoningBlock({ part }: { part: ReasoningPart }) {
  const active = part.status === 'streaming'
  const [now, setNow] = useState(Date.now)
  useEffect(() => {
    if (!active || part.startedAt === undefined) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [active, part.startedAt])
  // A manual toggle applies to this phase only. Moving from thinking to text
  // closes the panel automatically; completed panels can still be reopened.
  const [override, setOverride] = useState<{ phase: ReasoningPart['status']; open: boolean } | null>(null)
  const open = override?.phase === part.status ? override.open : active
  const text = part.text.slice(0, 120000)
  const preview = clip(text.trim().split('\n').filter(Boolean).at(-1) ?? '', 72)
  const duration = part.startedAt !== undefined
    ? Math.round(Math.max(0, (part.completedAt ?? now) - part.startedAt) / 1000)
    : undefined
  return <ReasoningSteps
    className="message-reasoning"
    data-reasoning-iteration={part.iteration}
    data-reasoning-status={part.status}
    duration={duration}
    onOpenChange={next => setOverride({ phase: part.status, open: next })}
    open={open}
  >
    <ReasoningStepsTrigger>思考过程 · 第 {part.iteration} 轮</ReasoningStepsTrigger>
    <ReasoningStepsContent>
      <ReasoningStep
        description={preview}
        label={active ? '正在思考' : part.status === 'interrupted' ? '思考已中断' : '思考结束'}
        status={active ? 'active' : 'done'}
      >
        <div className="message-reasoning-body"><Markdown content={text} /></div>
      </ReasoningStep>
    </ReasoningStepsContent>
  </ReasoningSteps>
}
