import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { TaskDetailModal } from '../src/components/TaskDetailModal'
import { useTasksStore } from '../src/stores/tasks'
import type { BackgroundTask } from '../src/types'

const shellTask: BackgroundTask = {
  id: 'job-shell',
  title: 'dotnet test tests/AllAgent.NET.Tests',
  sessionId: 'conversation-1',
  kind: 'shell',
  state: 'failed',
  createdAt: '2026-10-19T08:00:00Z',
  updatedAt: '2026-10-19T08:27:00Z',
  output: 'Failed!  - Failed: 3\n[stderr] boom\n\n退出码 1',
  error: '退出码 1',
}

let root: Root | null = null
let element: HTMLDivElement | null = null

beforeEach(() => {
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  Object.assign(window, { matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }) })
  element = document.createElement('div')
  document.body.appendChild(element)
  root = createRoot(element)
  const store = useTasksStore()
  store.tasks = []
  store.detailId = null
})

afterEach(async () => {
  if (root) await act(async () => { root?.unmount() })
  root = null
  element?.remove()
  element = null
  vi.unstubAllGlobals()
})

describe('task detail', () => {
  it('shows the command output for shell jobs instead of the parent conversation', async () => {
    const store = useTasksStore()
    store.tasks = [shellTask]
    store.detailId = shellTask.id
    await act(async () => { root?.render(<TaskDetailModal />) })
    const output = document.body.querySelector('.task-shell-output')
    expect(document.body.textContent).toContain('后台命令 · 失败')
    expect(output?.textContent).toContain('Failed!  - Failed: 3')
    expect(output?.querySelector('.stderr')?.textContent).toContain('[stderr] boom')
    expect(document.body.querySelector('.task-transcript')).toBeNull()
  })

  it('keeps the sub-agent transcript for delegated tasks', async () => {
    const store = useTasksStore()
    store.tasks = [{ ...shellTask, id: 'job-agent', kind: 'agent', state: 'completed', finalOutput: 'done' }]
    store.detailId = 'job-agent'
    await act(async () => {
      root?.render(<TaskDetailModal />)
      await new Promise(resolve => setTimeout(resolve, 20))
    })
    expect(document.body.querySelector('.task-transcript')).toBeTruthy()
    expect(document.body.querySelector('.task-shell-output')).toBeNull()
  })
})
