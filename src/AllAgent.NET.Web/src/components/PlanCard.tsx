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
      {plan.feedback ? <p className="plan-feedback">{tr('评论：', 'Comment: ')}{plan.feedback}</p> : null}
    </section>
  </div>
}

/** Blocking approval dialog. The run stays paused until the person answers, so Esc does nothing. */
export function PlanDialog({ request, onRespond }: {
  request: PlanProposalRequest
  onRespond: (id: string, approved: boolean, feedback?: string) => Promise<void>
}) {
  const tr = useT()
  const [busy, setBusy] = useState(false)
  const [feedback, setFeedback] = useState('')
  const [error, setError] = useState('')
  const comment = feedback.trim()
  const respond = async (approved: boolean, note = comment) => {
    setBusy(true)
    try {
      await onRespond(request.id, approved, note || undefined)
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
          {tr('批准后将切换到完全访问（写文件、执行命令、访问工作区外）并立即开始执行。提交评论则保持计划模式，AI 会按建议修订并重新提交计划。', 'Approval switches to full access (write files, run commands, access outside the workspace) and starts execution immediately. Submitting a comment keeps plan mode, and the AI revises and resubmits the plan from it.')}
        </DialogDescription>
      </DialogHeader>
      <PlanBody className="plan-dialog-body" markdown={request.markdown} />
      <label className="plan-comment">
        <span>{tr('评论 / 修改建议', 'Comment / suggested change')}</span>
        <textarea
          aria-label={tr('计划评论', 'Plan comment')}
          className="plan-comment-input"
          disabled={busy}
          onChange={event => setFeedback(event.target.value)}
          onKeyDown={event => {
            if ((event.ctrlKey || event.metaKey) && event.key === 'Enter' && comment) {
              event.preventDefault()
              void respond(false)
            }
          }}
          placeholder={tr('例如：第 3 步先补测试；不要修改数据库结构。', 'For example: add tests before step 3; do not change the database schema.')}
          rows={3}
          value={feedback}
        />
        <small>{tr('评论会随计划保存；批准时作为执行要求，提交建议时 AI 会据此继续修订计划。', 'The comment is stored with the plan: on approval it becomes an execution requirement; when submitted as feedback the AI revises the plan from it.')}</small>
      </label>
      {error ? <p className="text-destructive text-sm" role="alert">{error}</p> : null}
      <DialogFooter>
        <Button disabled={busy} onClick={() => void respond(false)} variant="ghost">
          {comment ? tr('提交建议，继续规划', 'Submit feedback and keep planning') : tr('拒绝，继续规划', 'Reject and keep planning')}
        </Button>
        <Button disabled={busy} onClick={() => void respond(true)}>
          {busy ? tr('处理中…', 'Working…') : comment ? tr('批准并执行', 'Approve and execute') : tr('批准并自动执行', 'Approve and execute')}
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
}
