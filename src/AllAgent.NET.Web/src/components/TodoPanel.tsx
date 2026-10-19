"use client"

import { AnimatePresence, motion, useReducedMotion, type Transition } from 'motion/react'
import { ChevronDown, ListChecks, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Badge } from './ui/badge'
import { Button } from './ui/b-button'
import { Input } from './ui/input'
import { Progress } from './ui/b-progress'
import { StatusDot } from './ui/status-dot'
import { clip } from '@/lib/format'
import type { TodoItem, TodoStatus } from '@/types'

const statusLabels: Record<TodoStatus, string> = {
  pending: '待处理',
  in_progress: '进行中',
  completed: '已完成',
}

const statusTone: Record<TodoStatus, 'neutral' | 'active' | 'success'> = {
  pending: 'neutral',
  in_progress: 'active',
  completed: 'success',
}

// Motion tuned to the Iconiq accordion: a spring on the shell height so the
// panel opens like a component-library disclosure, plus a light stagger for rows.
const shellTransition: Transition = {
  height: { type: 'spring', stiffness: 168, damping: 26, mass: 0.92 },
  opacity: { duration: 0.18, ease: [0.18, 1, 0.32, 1] },
}

const rowSpring: Transition = {
  type: 'spring',
  stiffness: 380,
  damping: 34,
  mass: 0.7,
}

const chevronSpring: Transition = {
  type: 'spring',
  stiffness: 420,
  damping: 26,
  mass: 0.6,
}

const instantTransition: Transition = { duration: 0 }

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
  const instant = useReducedMotion() === true
  const locked = disabled || saving
  const total = todos.length
  const completed = todos.filter(todo => todo.status === 'completed').length
  const active = todos.filter(todo => todo.status === 'in_progress')
  const percent = total > 0 ? Math.round((completed / total) * 100) : 0
  const allDone = total > 0 && completed === total
  const heading = active.length > 0
    ? clip(active[0].content, 44) + (active.length > 1 ? ` +${active.length - 1}` : '')
    : null

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

  return <section className={`todo-bar ${allDone ? 'is-done' : ''}`}>
    <div className={`todo-card ${open ? 'is-open' : ''}`}>
      <button aria-expanded={open} className="todo-header" onClick={() => setOpen(value => !value)} type="button">
        <span aria-hidden className="todo-mark"><ListChecks size={14} /></span>
        <span className="todo-title">TODO</span>
        <Badge color={allDone ? 'green' : 'gray'} size="sm" variant="dot">{completed}/{total}</Badge>
        {heading ? <span className="todo-current">
          <StatusDot animate size="sm" tone="active" />
          <span className="todo-current-text">{heading}</span>
        </span> : null}
        <span className="todo-meter">
          <Progress
            ariaLabel={`TODO 进度 ${completed}/${total}`}
            className="todo-progress"
            max={Math.max(total, 1)}
            showValue={false}
            size="sm"
            tone={allDone ? 'success' : 'default'}
            trackClassName="todo-track"
            value={completed}
          />
          <span className="todo-percent">{percent}%</span>
        </span>
        <motion.span
          animate={{ rotate: open ? 180 : 0 }}
          aria-hidden
          className="todo-chevron"
          transition={instant ? instantTransition : chevronSpring}
        >
          <ChevronDown size={15} />
        </motion.span>
      </button>
      <motion.div
        animate={{ height: open ? 'auto' : 0, opacity: open ? 1 : 0 }}
        aria-hidden={!open}
        className="todo-body-shell"
        initial={false}
        transition={instant ? instantTransition : shellTransition}
      >
        <div className="todo-body" inert={open ? undefined : true}>
          {todos.length > 0 ? <ol className="todo-list">
            <AnimatePresence initial={false}>
              {todos.map((todo, index) => <motion.li
                animate={{ opacity: 1, x: 0 }}
                exit={instant ? { opacity: 0 } : { opacity: 0, x: -12, transition: { duration: 0.16 } }}
                initial={instant ? false : { opacity: 0, x: 6 }}
                key={todo.id}
                layout={instant ? undefined : 'position'}
                transition={instant ? instantTransition : { ...rowSpring, delay: Math.min(index, 8) * 0.02 }}
              >
                <StatusDot animate={todo.status === 'in_progress'} size="sm" tone={statusTone[todo.status]} />
                <span className={`todo-text ${todo.status === 'completed' ? 'todo-done' : ''}`}>{todo.content}</span>
                <span className={`todo-state ${todo.status}`}>{statusLabels[todo.status]}</span>
                <Button
                  disabled={locked}
                  iconLabel={`删除待办：${todo.content}`}
                  onClick={() => void save(todos.filter(item => item.id !== todo.id))}
                  size="icon-sm"
                  variant="ghost"
                ><Trash2 size={14} /></Button>
              </motion.li>)}
            </AnimatePresence>
          </ol> : <p className="todo-empty">暂无待办，多步骤任务的进度会显示在这里。</p>}
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
        </div>
      </motion.div>
    </div>
  </section>
}
