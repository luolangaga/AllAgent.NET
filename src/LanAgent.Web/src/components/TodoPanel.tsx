import { useState } from 'react'
import { Button } from './ui/b-button'
import type { TodoItem } from '../types'

export function TodoPanel({ todos, disabled, onChange, report }: {
  todos: TodoItem[]; disabled: boolean; onChange: (items: TodoItem[]) => Promise<void>; report: (error: unknown) => void
}) {
  const [draft, setDraft] = useState('')
  const [saving, setSaving] = useState(false)
  const locked = disabled || saving
  async function save(items: TodoItem[], added = false) {
    setSaving(true)
    try { await onChange(items); if (added) setDraft('') } catch (error) { report(error) } finally { setSaving(false) }
  }
  return <details className="todo-panel" open>
    <summary>TODO <span>{todos.filter(t => t.status === 'completed').length}/{todos.length}</span></summary>
    <ol aria-label="待办清单">{todos.map(todo => <li key={todo.id}>
      <select aria-label={`${todo.content}的状态`} value={todo.status} disabled={locked} onChange={e => {
        const status = e.target.value as TodoItem['status']
        void save(todos.map(t => t.id === todo.id ? { ...t, status } : status === 'in_progress' && t.status === 'in_progress' ? { ...t, status: 'pending' } : t))
      }}><option value="pending">待处理</option><option value="in_progress">进行中</option><option value="completed">已完成</option></select>
      <span className={todo.status === 'completed' ? 'todo-completed' : ''}>{todo.content}</span>
      <Button variant="ghost" size="sm" disabled={locked} aria-label={`删除待办：${todo.content}`} onClick={() => void save(todos.filter(t => t.id !== todo.id))}>删除</Button>
    </li>)}</ol>
    {todos.length === 0 && <p>暂无待办，多步骤任务的进度会显示在这里。</p>}
    <form onSubmit={e => { e.preventDefault(); if (!locked && draft.trim()) void save([...todos, { id: crypto.randomUUID(), content: draft.trim(), status: 'pending' }], true) }}>
      <input aria-label="新待办" placeholder="添加待办…" maxLength={300} value={draft} disabled={locked} onChange={e => setDraft(e.target.value)} />
      <Button type="submit" variant="outline" size="sm" disabled={locked || !draft.trim() || todos.length >= 100}>添加</Button>
    </form>
  </details>
}
