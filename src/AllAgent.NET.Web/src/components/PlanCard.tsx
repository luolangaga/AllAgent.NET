import { useState } from 'react'
import { ChevronDown, ChevronRight } from 'lucide-react'
import { Markdown } from './Markdown'
import { Badge } from './ui/badge'
import { Button } from './ui/b-button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from './ui/b-dialog'
import { cn } from '@/lib/utils'
import { useT } from '@/i18n'
import type { PlanDocument, PlanProposalRequest } from '@/types'

function statusLabel(status: string): string {
  switch (status) {
    case 'proposed': return '待批准'
    case 'approved': return '已批准'
    case 'rejected': return '已拒绝'
    default: return status
  }
}

function statusColor(status: string): 'green' | 'red' | 'amber' | 'gray' {
  if (status === 'approved') return 'green'
  if (status === 'rejected') return 'red'
  if (status === 'proposed') return 'amber'
  return 'gray'
}

/** Plan content is Markdown in the host; `Markdown` turns it into sanitized HTML for the browser. */
export function PlanBody({ markdown, className }: { markdown: string; className?: string }) {
  return <div className={cn('plan-body', className)}><Markdown content={markdown} /></div>
}

export function PlanPanel({ plan }: { plan: PlanDocument }) {
  const tr = useT()
  const [open, setOpen] = useState(false)
  const Chevron = open ? ChevronDown : ChevronRight
  const label = plan.status === 'proposed' ? tr('待批准', 'Pending approval')
    : plan.status === 'approved' ? tr('已批准', 'Approved')
    : plan.status === 'rejected' ? tr('已拒绝', 'Rejected')
    : statusLabel(plan.status)
  return <div className="plan-panel-shell">
    <section className={cn('plan-panel', `plan-${plan.status}`)}>
      <button aria-expanded={open} className="plan-panel-head" onClick={() => setOpen(value => !value)} type="button">
        <span className="eyebrow">PLAN</span>
        <span className="plan-panel-title">{plan.title}</span>
        <Badge color={statusColor(plan.status)} size="sm" variant="dot">{label}</Badge>
        <Chevron aria-hidden className="plan-panel-chevron" size={14} />
      </button>
      {open ? <PlanBody className="plan-panel-body" markdown={plan.markdown} /> : null}
      {plan.feedback ? <p className="plan-feedback">{plan.feedback}</p> : null}
    </section>
  </div>
}

/** Blocking approval dialog. The run stays paused until the person answers, so Esc does nothing. */
export function PlanDialog({ request, onRespond }: {
  request: PlanProposalRequest
  onRespond: (id: string, approved: boolean) => Promise<void>
}) {
  const tr = useT()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const respond = async (approved: boolean) => {
    setBusy(true)
    try {
      await onRespond(request.id, approved)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }
  return <Dialog open onOpenChange={() => { /* the run is blocked until an answer arrives */ }}>
    <DialogContent className="plan-dialog" showCloseButton={false} size="lg">
      <DialogHeader>
        <DialogTitle>{tr('计划审批', 'Plan approval')} · {request.title}</DialogTitle>
        <DialogDescription>
          {tr('批准后将切换到完全访问（写文件、执行命令、访问工作区外），并立即开始执行。拒绝则保持计划模式，AI 会继续修订计划。', 'Approval switches to full access (write files, run commands, access outside the workspace) and starts execution immediately. Rejecting keeps plan mode, and the AI will revise the plan.')}
        </DialogDescription>
      </DialogHeader>
      <PlanBody className="plan-dialog-body" markdown={request.markdown} />
      {error ? <p className="text-destructive text-sm" role="alert">{error}</p> : null}
      <DialogFooter>
        <Button disabled={busy} onClick={() => void respond(false)} variant="ghost">{tr('拒绝，继续规划', 'Reject and keep planning')}</Button>
        <Button disabled={busy} onClick={() => void respond(true)}>
          {busy ? tr('处理中…', 'Working…') : tr('批准并自动执行', 'Approve and execute')}
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
}
