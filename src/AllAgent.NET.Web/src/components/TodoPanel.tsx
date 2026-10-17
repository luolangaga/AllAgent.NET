import { useState } from 'react'
import { ChevronDown, ListChecks, Trash2 } from 'lucide-react'
import { Badge } from './ui/badge'
import { Button } from './ui/b-button'
import { Input } from './ui/input'
import { Progress } from './ui/b-progress'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from './ui/b-select'
import { StatusDot } from './ui/status-dot'
import { clip } from '@/lib/format'
import type { TodoItem, TodoStatus } from '@/types'

const statusOptions: Array<{ value: TodoStatus; label: string }> = [
  { value: 'pending', label: '待处理' },
  { value: 'in_progress', label: '进行中' },
  { value: 'completed', label: '已完成' },
]

const statusTone: Record<TodoStatus, 'neutral' | 'active' | 'success'> = {
  pending: 'neutral',
  in_progress: 'active',
  completed: 'success',
}

// Collapsed by default: a slim status bar above the transcript that expands
// into the editable checklist when the owner wants to change it.
export function TodoPanel({ todos, disabled, onChange, report, defaultOpen = false }: {
  todos: TodoItem[]
  disabled: boolean
  onChange: (items: TodoItem[]) => Promise<void>
  report: (error: unknown) => void
  defaultOpen?: boolean
}) {
  const [draft, setDraft] = useState('')
  const [open, setOpen] = useState(defaultOpen)
  const [saving, setSaving] = useState(false)
  const locked = disabled || saving
  const completed = todos.filter(todo => todo.status === 'completed').length
  const current = todos.find(todo => todo.status === 'in_progress')

  async function save(items: TodoItem[], added = false) {
    setSaving(true)
    try {
      await onChange(items)
      if (added) setDraft('')
    } catch (error) {
      report(error)
    } finally {
      setSaving(false)
    }
  }

  function setStatus(todo: TodoItem, status: TodoStatus) {
    const next = todos.map(item => {
      if (item.id === todo.id) return { ...item, status }
      if (status === 'in_progress' && item.status === 'in_progress') return { ...item, status: 'pending' as TodoStatus }
      return item
    })
    void save(next)
  }

  return <section className={`todo-bar ${open ? 'is-open' : ''}`}>
    <button aria-expanded={open} className="todo-header" onClick={() => setOpen(!open)} type="button">
      <ListChecks size={15} />
      <span className="todo-title">TODO</span>
      <Badge color={completed === todos.length && todos.length > 0 ? 'green' : 'gray'} size="sm" variant="dot">{completed}/{todos.length}</Badge>
      <Progress ariaLabel="TODO 进度" className="todo-progress" max={Math.max(todos.length, 1)} size="sm" value={completed} />
      {current ? <span className="todo-current"><StatusDot animate size="sm" tone="active" />{clip(current.content, 44)}</span> : null}
      <ChevronDown className={`todo-chevron ${open ? 'open' : ''}`} size={14} />
    </button>
    {open ? <div className="todo-body">
      {todos.length > 0 ? <ol className="todo-list">{todos.map(todo => <li key={todo.id}>
        <StatusDot animate={todo.status === 'in_progress'} size="sm" tone={statusTone[todo.status]} />
        <span className={todo.status === 'completed' ? 'todo-done' : undefined}>{todo.content}</span>
        <Select
          disabled={locked}
          items={statusOptions}
          onValueChange={value => setStatus(todo, value as TodoStatus)}
          value={todo.status}
        >
          <SelectTrigger aria-label={`${todo.content} 的状态`} className="todo-status" size="sm">
            <SelectValue>{value => statusOptions.find(option => option.value === value)?.label ?? value}</SelectValue>
          </SelectTrigger>
          <SelectContent>{statusOptions.map(option => <SelectItem key={option.value} value={option.value}>{option.label}</SelectItem>)}</SelectContent>
        </Select>
        <Button
          disabled={locked}
          iconLabel={`删除待办：${todo.content}`}
          onClick={() => void save(todos.filter(item => item.id !== todo.id))}
          size="icon-sm"
          variant="ghost"
        ><Trash2 size={14} /></Button>
      </li>)}</ol> : <p className="todo-empty">暂无待办，多步骤任务的进度会显示在这里。</p>}
      <form className="todo-add" onSubmit={event => {
        event.preventDefault()
        if (!locked && draft.trim()) void save([...todos, { id: crypto.randomUUID(), content: draft.trim(), status: 'pending' }], true)
      }}>
        <Input
          aria-label="新待办"
          disabled={locked}
          maxLength={300}
          onValueChange={setDraft}
          placeholder="添加待办…"
          size="sm"
          value={draft}
        />
        <Button
          disabled={locked || !draft.trim() || todos.length >= 100}
          size="sm"
          type="submit"
          variant="outline"
        >添加</Button>
      </form>
    </div> : null}
  </section>
}
