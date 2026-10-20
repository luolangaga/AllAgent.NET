import { useEffect, useState } from 'react'
import { ListTodo, RefreshCw, Trash2 } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Card } from '@/components/ui/card'
import { StatusDot } from '@/components/ui/status-dot'
import { EmptyState, PageShell } from '@/components/PageShell'
import { formatRelative, taskIsActive, taskState, taskStateLabel, taskStateTone } from '@/lib/format'
import { useStore } from '@/lib/store'
import { useTasksStore } from '@/stores/tasks'
import { useNoticesStore } from '@/stores/ui'
import type { BackgroundTask } from '@/types'

/** Shell jobs have no child conversation; preview their latest output line instead. */
function taskPreview(task: BackgroundTask): string {
  if (task.currentStep) return `当前步骤：${task.currentStep}`
  if (task.kind === 'shell') {
    const tail = (task.output ?? '').trim().split('\n').map(line => line.trim()).filter(Boolean).at(-1)
    if (tail) return tail.length > 160 ? `…${tail.slice(-160)}` : tail
    if (task.error) return task.error
    return taskIsActive(task.state) ? '命令已启动，等待输出…' : '命令没有输出。'
  }
  if (task.finalOutput) return task.finalOutput.slice(0, 120)
  return task.error ?? `子代理会话 ${task.sessionId.slice(0, 8)}`
}

export default function Tasks() {
  const store = useStore(useTasksStore)
  const notices = useStore(useNoticesStore)
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
            onClick={() => store.openDetail(task.id)}
            type="button"
          >
            <div className="resource-icon"><ListTodo size={18} /></div>
            <div className="list-content">
              <div className="resource-title">
                <h3>{task.title}</h3>
                <Badge size="sm" variant="dot">{task.kind}</Badge>
              </div>
              <p className="list-description">{taskPreview(task)}</p>
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
  </PageShell>
}
