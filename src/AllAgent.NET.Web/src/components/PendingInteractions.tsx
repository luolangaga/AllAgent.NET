import { useState } from 'react'
import { Button } from './ui/b-button'
import { Badge } from './ui/badge'
import { CodeBlock } from './ui/code-block'
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent,
  AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from './ui/b-alert-dialog'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from './ui/b-dialog'
import { Input } from './ui/input'
import { parseObject } from '@/utils/execution'
import { useStore } from '@/lib/store'
import { useApprovalsStore } from '@/stores/approvals'
import type { AgentQuestionRequest, ApprovalRequest } from '@/types'

function riskAppearance(risk: string): 'default' | 'warning' | 'destructive' {
  switch (risk.toLowerCase()) {
    case 'write': return 'warning'
    case 'execute': return 'destructive'
    case 'network': return 'warning'
    default: return 'default'
  }
}

function ApprovalDialog({ request, onRespond }: { request: ApprovalRequest; onRespond: (id: string, approved: boolean) => Promise<void> }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const args = parseObject(request.args)
  const respond = async (approved: boolean) => {
    setBusy(true)
    try {
      await onRespond(request.requestId, approved)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }
  return <AlertDialog open onOpenChange={() => { /* the run is blocked until an answer arrives */ }}>
    <AlertDialogContent>
      <AlertDialogHeader>
        <AlertDialogTitle>需要授权 · {request.name}</AlertDialogTitle>
        <AlertDialogDescription>
          Agent 请求执行这一步操作。允许后仅这一次生效，后续操作仍会重新确认。
        </AlertDialogDescription>
      </AlertDialogHeader>
      <div className="approval-body">
        <div className="approval-meta">
          <Badge color={riskAppearance(request.risk) === 'destructive' ? 'red' : riskAppearance(request.risk) === 'warning' ? 'amber' : 'gray'} size="sm">{request.risk}</Badge>
          {request.source ? <Badge size="sm" variant="dot">{request.source}</Badge> : null}
          {request.requiresExplicitApproval ? <Badge color="amber" size="sm" variant="dot">始终确认</Badge> : null}
        </div>
        <CodeBlock code={JSON.stringify(args, null, 2)} language="json" maxHeight={280} />
      </div>
      {error ? <p className="text-destructive text-sm" role="alert">{error}</p> : null}
      <AlertDialogFooter>
        <AlertDialogCancel disabled={busy} onClick={event => { event.preventDefault(); void respond(false) }}>拒绝</AlertDialogCancel>
        <AlertDialogAction closeOnClick={false} disabled={busy} onClick={() => void respond(true)} variant="default">
          {busy ? '处理中…' : '允许这一次'}
        </AlertDialogAction>
      </AlertDialogFooter>
    </AlertDialogContent>
  </AlertDialog>
}

function QuestionDialog({ request, onAnswer }: { request: AgentQuestionRequest; onAnswer: (id: string, answer: string) => Promise<void> }) {
  const [text, setText] = useState('')
  const [selected, setSelected] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const answer = async (value: string) => {
    setBusy(true)
    try {
      await onAnswer(request.id, value)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }
  return <Dialog open onOpenChange={() => { /* stay open until answered */ }}>
    <DialogContent size="default">
      <DialogHeader>
        <DialogTitle>Agent 需要你的回答</DialogTitle>
        <DialogDescription>{request.question}</DialogDescription>
      </DialogHeader>
      {request.choices.length > 0 ? <div className="question-choices">
        {request.choices.map(choice => <Button
          disabled={busy}
          key={choice}
          aria-pressed={request.multiple ? selected.includes(choice) : undefined}
          onClick={() => {
            if (!request.multiple) { void answer(choice); return }
            setSelected(current => current.includes(choice) ? current.filter(item => item !== choice) : [...current, choice])
          }}
          variant="outline"
        >{choice}</Button>)}
      </div> : null}
      <div className="question-input">
        <Input autoFocus onValueChange={setText} placeholder="输入回答…" value={text} />
      </div>
      {error ? <p className="text-destructive text-sm" role="alert">{error}</p> : null}
      <DialogFooter>
        <Button disabled={busy} onClick={() => void answer('')} variant="ghost">跳过</Button>
        <Button
          disabled={busy || (!text.trim() && selected.length === 0)}
          onClick={() => void answer(request.multiple ? JSON.stringify([...selected, ...(text.trim() ? [text.trim()] : [])]) : text.trim())}
        >发送回答</Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
}

export function PendingInteractions() {
  const approvals = useStore(useApprovalsStore)
  const approval = approvals.approvals[0]
  const question = approvals.questions[0]
  return <>
    {approval ? <ApprovalDialog key={approval.requestId} onRespond={approvals.respond} request={approval} /> : null}
    {!approval && question ? <QuestionDialog key={question.id} onAnswer={approvals.answer} request={question} /> : null}
  </>
}
