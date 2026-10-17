import { useEffect, useState } from 'react'
import { ListTodo, RefreshCw, Trash2 } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Card } from '@/components/ui/card'
import { CodeBlock } from '@/components/ui/code-block'
import { Skeleton } from '@/components/ui/skeleton'
import { StatusDot } from '@/components/ui/status-dot'
import { MessageBubble } from '@/components/MessageBubble'
import { Modal } from '@/components/shared'
import { EmptyState, PageShell } from '@/components/PageShell'
import { formatDateTime, formatRelative, taskIsActive, taskState, taskStateLabel, taskStateTone } from '@/lib/format'
import { useStore } from '@/lib/store'
import { invoke } from '@/bridge'
import { restoreMessages } from '@/utils/execution'
import { useTasksStore } from '@/stores/tasks'
import { useNoticesStore } from '@/stores/ui'
import type { BackgroundTask, Message } from '@/types'

/** Sub-agent output renders with the same message components as the main chat. */
function TaskTranscript({ task }: { task: BackgroundTask }) {
  const notices = useStore(useNoticesStore)
  const [messages, setMessages] = useState<Message[] | null>(null)

  useEffect(() => {
    let active = true
    setMessages(null)
    invoke<Message[]>('conversations.getMessages', { conversationId: task.sessionId })
      .then(loaded => {
        if (active) setMessages(restoreMessages(loaded).filter(message => message.role !== 'system' && message.role !== 'tool'))
      })
      .catch(error => {
        if (active) {
          setMessages([])
          notices.report(error)
        }
      })
    return () => { active = false }
  }, [task.sessionId])

  if (messages === null) return <div className="messages"><Skeleton className="h-5 w-1/3" /><Skeleton className="mt-4 h-24 w-full" /></div>
  if (!messages.length) return <p className="list-description">子代理还没有输出；任务执行中会持续在这里追加。</p>
  return <div className="messages">{messages.map(message => <MessageBubble key={message.id} message={message} />)}</div>
}

function detailTitle(task: BackgroundTask): string {
  return `${task.kind} · ${taskStateLabel(task.state)}`
}

export default function Tasks() {
  const store = useStore(useTasksStore)
  const notices = useStore(useNoticesStore)
  const [detail, setDetail] = useState<BackgroundTask | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => { void store.loadAll().catch(notices.report) }, [])

  async function run(action: () => Promise<unknown>) {
    setBusy(true)
    try {
      await action()
    } catch (error) {
      notices.report(error)
    } finally {
      setBusy(false)
    }
  }

  const sorted = [...store.tasks].sort((a, b) => (taskIsActive(b.state) ? 1 : 0) - (taskIsActive(a.state) ? 1 : 0) || b.updatedAt.localeCompare(a.updatedAt))

  return <PageShell
    action={<Button icon={<RefreshCw size={15} />} loading={store.loading} onClick={() => void run(() => store.loadAll())} variant="outline">刷新</Button>}
    description="子代理与后台任务的结果、状态与取消入口。任务完成后会自动通知父会话，并在这里保留记录。"
    eyebrow="BACKGROUND WORK"
    title="让长任务，在后台推进。"
  >
    <div className="section-meta">
      <span>{store.tasks.length} 个任务 · {store.activeTasks.length} 个进行中</span>
      <span>子代理 · 后台任务 · 自动通知</span>
    </div>
    {!sorted.length
      ? <EmptyState title="暂无后台任务">在对话里让 Agent 派生一个子任务，或者使用后台任务能力后，这里会显示进度与结果。</EmptyState>
      : <Card className="list-card">
        <div className="divide-list">
          {sorted.map(task => <button
            className="list-row task-row"
            key={task.id}
            onClick={() => setDetail(task)}
            type="button"
          >
            <div className="resource-icon"><ListTodo size={18} /></div>
            <div className="list-content">
              <div className="resource-title">
                <h3>{task.title}</h3>
                <Badge size="sm" variant="dot">{task.kind}</Badge>
              </div>
              <p className="list-description">
                {task.currentStep
                  ? `当前步骤：${task.currentStep}`
                  : task.finalOutput
                    ? task.finalOutput.slice(0, 120)
                    : task.error ?? `子代理会话 ${task.sessionId.slice(0, 8)}`}
              </p>
            </div>
            <div className="list-actions" onClick={event => event.stopPropagation()}>
              <span className="task-state">
                <StatusDot animate={taskState(task.state) === 'running'} size="sm" tone={taskStateTone(task.state)} />
                {taskStateLabel(task.state)}
              </span>
              <span className="task-time">{formatRelative(task.updatedAt)}</span>
              {taskIsActive(task.state) ? <Button
                disabled={busy}
                iconLabel="取消任务"
                onClick={() => void run(() => store.cancel(task.id))}
                size="icon-sm"
                variant="ghost"
              ><Trash2 size={15} /></Button> : null}
            </div>
          </button>)}
        </div>
      </Card>}

    {detail ? <Modal
      description={`${detailTitle(detail)} · 子代理会话 ${detail.sessionId.slice(0, 8)}`}
      onClose={() => setDetail(null)}
      size="full"
      title={detail.title}
    >
      <div className="task-detail-meta">
        <Badge size="sm" variant="dot">{taskStateLabel(detail.state)}</Badge>
        <span>创建 {formatDateTime(detail.createdAt)}</span>
        <span>更新 {formatDateTime(detail.updatedAt)}</span>
        {detail.notifyParent ? <Badge color="blue" size="sm" variant="dot">完成通知父会话</Badge> : null}
      </div>
      {detail.error ? <CodeBlock code={detail.error} language="text" maxHeight={200} /> : null}
      <div className="task-transcript">
        <TaskTranscript task={detail} />
      </div>
      {detail.finalOutput ? <details className="task-output">
        <summary>最终输出</summary>
        <CodeBlock code={detail.finalOutput} language="text" maxHeight={320} />
      </details> : null}
    </Modal> : null}
  </PageShell>
}