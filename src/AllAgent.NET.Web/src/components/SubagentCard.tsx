import { useEffect, useMemo } from 'react'
import { ChevronRight, Users } from 'lucide-react'
import { StatusDot } from './ui/status-dot'
import { formatRelative, taskIsActive, taskState, taskStateLabel, taskStateTone } from '@/lib/format'
import { useStore } from '@/lib/store'
import { useTasksStore } from '@/stores/tasks'
import { parseArguments } from '@/utils/execution'
import type { BackgroundTask, ToolCall } from '@/types'

/** Expected children parsed from the call's arguments while the task list has not caught up. */
function expectedRows(call: ToolCall): Array<{ key: string; title: string }> {
  const args = parseArguments(call.args) as Record<string, unknown>
  const items = Array.isArray(args.tasks) ? args.tasks : Array.isArray(args.chain) ? args.chain : null
  if (items) return items.map((item, index) => {
    const entry = (item ?? {}) as { agent?: string; task?: string }
    return { key: `expected-${index}`, title: `${entry.agent ?? 'agent'} · ${clip(entry.task ?? '')}` }
  })
  if (args.agent || args.task) return [{ key: 'expected-0', title: `${args.agent ?? 'agent'} · ${clip(String(args.task ?? ''))}` }]
  return []
}

function clip(text: string): string {
  const flat = text.replace(/\s+/g, ' ').trim()
  return flat.length > 64 ? `${flat.slice(0, 64)}…` : flat
}

/** The live step line shown under a row, mirroring the tasks page wording. */
function stepLine(task: BackgroundTask): string {
  if (taskIsActive(task.state)) return task.currentStep ? `当前步骤：${task.currentStep}` : '等待执行'
  if (taskState(task.state) === 'failed') return task.error ? clip(task.error) : '执行失败'
  if (taskState(task.state) === 'cancelled') return '已停止'
  return task.finalOutput ?? task.output ? clip(task.finalOutput ?? task.output ?? '') : '已完成'
}

function summary(rows: BackgroundTask[]): string {
  const active = rows.filter(task => taskIsActive(task.state)).length
  const done = rows.filter(task => taskState(task.state) === 'completed').length
  if (active) return `${rows.length} 个子任务 · ${done} 完成 · ${active} 进行中`
  return `${rows.length} 个子任务 · ${done} 完成`
}

/**
 * Sub-agent activity gets its own card instead of a plain tool row: the child tasks stream
 * their live step here and every row opens the shared task-detail dialog.
 */
export function SubagentCard({ call }: { call: ToolCall }) {
  const tasks = useStore(useTasksStore)
  const running = call.status === 'running'
  // A chat page can render cards before the task list has ever loaded, and the child task may
  // not exist yet when the card mounts — poll for as long as the call itself is running.
  useEffect(() => { if (!tasks.loaded && !tasks.loading) void tasks.loadAll().catch(() => {}) }, [])
  useEffect(() => {
    if (!running) return
    void tasks.refresh().catch(() => {})
    const timer = window.setInterval(() => { void tasks.refresh().catch(() => {}) }, 1500)
    return () => window.clearInterval(timer)
  }, [running])

  const related = useMemo(() => tasks.tasks
    .filter(task => task.parentToolCallId === call.id)
    .sort((a, b) => a.createdAt.localeCompare(b.createdAt)), [tasks.tasks, call.id])
  const expected = useMemo(() => expectedRows(call), [call.args])

  return <div className="subagent-card">
    <div className="subagent-card-head">
      <Users aria-hidden className="tool-icon" size={15} />
      <span className="subagent-title">子代理</span>
      {call.rejected
        ? <span className="subagent-note">已拒绝</span>
        : related.length
          ? <span className="subagent-count">{summary(related)}</span>
          : <span className="subagent-count">{running ? '正在派生…' : '已结束'}</span>}
      {running ? <StatusDot animate size="sm" tone="active" /> : null}
    </div>
    <div className="subagent-rows">
      {related.length
        ? related.map(task => <button
          className="subagent-row"
          key={task.id}
          onClick={() => tasks.openDetail(task.id)}
          type="button"
        >
          <StatusDot animate={taskIsActive(task.state)} size="sm" tone={taskStateTone(task.state)} />
          <span className="subagent-row-main">
            <span className="subagent-row-title">{task.title}</span>
            <span className="subagent-row-detail">{stepLine(task)}</span>
          </span>
          <span className="subagent-state">{taskStateLabel(task.state)}</span>
          <span className="subagent-time">{formatRelative(task.updatedAt)}</span>
          <ChevronRight aria-hidden className="subagent-chevron" size={14} />
        </button>)
        : expected.map(row => <div className="subagent-row is-pending" key={row.key}>
          <StatusDot animate={running} size="sm" tone={running ? 'active' : 'neutral'} />
          <span className="subagent-row-main">
            <span className="subagent-row-title">{row.title}</span>
            <span className="subagent-row-detail">{running ? '准备中…' : '没有留下任务记录'}</span>
          </span>
        </div>)}
    </div>
  </div>
}
