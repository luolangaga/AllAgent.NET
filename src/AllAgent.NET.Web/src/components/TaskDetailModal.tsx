import { useEffect, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { CodeBlock } from '@/components/ui/code-block'
import { Skeleton } from '@/components/ui/skeleton'
import { MessageBubble } from '@/components/MessageBubble'
import { Modal } from '@/components/shared'
import { formatDateTime, taskIsActive, taskStateLabel } from '@/lib/format'
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

  useEffect(() => { setMessages(null) }, [task.sessionId])

  // `updatedAt` bumps with every task step, so a running child keeps appending output here.
  useEffect(() => {
    let active = true
    invoke<Message[]>('conversations.getMessages', { conversationId: task.sessionId })
      .then(loaded => {
        if (active) setMessages(restoreMessages(loaded).filter(message => message.role !== 'system' && message.role !== 'tool'))
      })
      .catch(error => {
        if (active) {
          setMessages(current => current ?? [])
          notices.report(error)
        }
      })
    return () => { active = false }
  }, [task.sessionId, task.updatedAt])

  if (messages === null) return <div className="messages"><Skeleton className="h-5 w-1/3" /><Skeleton className="mt-4 h-24 w-full" /></div>
  if (!messages.length) return <p className="list-description">子代理还没有输出；任务执行中会持续在这里追加。</p>
  return <div className="messages">{messages.map(message => <MessageBubble key={message.id} message={message} />)}</div>
}

/** Shell jobs share the parent conversation id, so replaying that session would show the whole chat instead of the command. */
const isShellTask = (task: BackgroundTask) => task.kind === 'shell'

/** Shell jobs stream stdout/stderr into `output`; stderr lines and the exit line are shown inline. */
function ShellOutput({ task }: { task: BackgroundTask }) {
  const outputRef = useRef<HTMLPreElement>(null)
  const output = (task.output ?? '').replace(/\s+$/, '')
  // Live console: follow the newest lines while the command runs; finished output stays readable from the top.
  useEffect(() => {
    const node = outputRef.current
    if (node && taskIsActive(task.state)) node.scrollTop = node.scrollHeight
  }, [output, task.state])
  if (!output) return <p className="list-description">
    {taskIsActive(task.state) ? '命令已启动，等待输出…' : '命令没有产生输出。'}
  </p>
  return <pre className="task-shell-output" ref={outputRef}><code>
    {output.split('\n').map((line, index) => <span className={line.startsWith('[stderr] ') ? 'stderr' : undefined} key={index}>{line + '\n'}</span>)}
  </code></pre>
}

/**
 * Task detail opened from anywhere (task list, chat sub-agent cards). The live task is read
 * from the store on every render, so pushed `tasks.updated` events refresh the dialog instead
 * of leaving a click-time snapshot on screen.
 */
export function TaskDetailModal() {
  const store = useStore(useTasksStore)
  const task = store.detailId ? store.tasks.find(item => item.id === store.detailId) ?? null : null
  if (!task) return null
  const shell = isShellTask(task)
  return <Modal
    description={shell
      ? `后台命令 · ${taskStateLabel(task.state)}`
      : `${task.kind} · ${taskStateLabel(task.state)} · 子代理会话 ${task.sessionId.slice(0, 8)}`}
    onClose={() => store.closeDetail()}
    size="full"
    title={task.title}
  >
    <div className="task-detail-meta">
      <Badge size="sm" variant="dot">{taskStateLabel(task.state)}</Badge>
      <span>创建 {formatDateTime(task.createdAt)}</span>
      <span>更新 {formatDateTime(task.updatedAt)}</span>
      {task.notifyParent ? <Badge color="blue" size="sm" variant="dot">完成通知父会话</Badge> : null}
    </div>
    {task.error ? <CodeBlock code={task.error} language="text" maxHeight={200} /> : null}
    {shell
      ? <ShellOutput task={task} />
      : <>
        <div className="task-transcript">
          <TaskTranscript task={task} />
        </div>
        {task.finalOutput ? <details className="task-output">
          <summary>最终输出</summary>
          <CodeBlock code={task.finalOutput} language="text" maxHeight={320} />
        </details> : null}
      </>}
  </Modal>
}
