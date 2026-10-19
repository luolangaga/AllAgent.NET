import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params?: any }
const host = window as unknown as { chrome: { webview: { postMessage(json: string): void } }; __bridgeCallback(json: string): void }
const calls: Request[] = []

const statusDto = () => ({
  branch: 'main', detached: false, upstream: 'origin/main', ahead: 1, behind: 0, operation: null,
  staged: [{ path: 'src/App.tsx', originalPath: null, index: 'M', worktree: '.' }],
  unstaged: [{ path: 'src/styles/app.css', originalPath: null, index: '.', worktree: 'M' }],
  untracked: [{ path: 'src/components/GitPanel.tsx', originalPath: null, index: '?', worktree: '?' }],
  conflicted: [],
})

const summaryDto = () => ({
  available: true, version: 'git version 2.51.0', repo: true, root: 'C:\\demo\\allagent-web',
  branch: 'main', detached: false, upstream: 'origin/main', ahead: 1, behind: 0, operation: null,
  staged: 1, unstaged: 1, untracked: 1, conflicted: 0,
})

const demoCommits = [
  { hash: 'a1b2c3d4e5f60718293a4b5c6d7e8f9012345678', short: 'a1b2c3d', author: 'Luo Lan', authorEmail: 'luolan@example.com', date: new Date('2026-10-19T09:12:00Z').toISOString(), subject: '合并浮窗面板', refs: ['HEAD → main', 'origin/main'], parents: ['b2c3d4e5f60718293a4b5c6d7e8f901234567890', 'c3d4e5f60718293a4b5c6d7e8f90123456789012'] },
  { hash: 'c3d4e5f60718293a4b5c6d7e8f90123456789012', short: 'c3d4e5f', author: 'Luo Lan', authorEmail: 'luolan@example.com', date: new Date('2026-10-18T09:12:00Z').toISOString(), subject: '浮窗面板原型', refs: ['feature/panels'], parents: ['b2c3d4e5f60718293a4b5c6d7e8f901234567890'] },
  { hash: 'b2c3d4e5f60718293a4b5c6d7e8f901234567890', short: 'b2c3d4e', author: 'Luo Lan', authorEmail: 'luolan@example.com', date: new Date('2026-10-17T09:12:00Z').toISOString(), subject: '初始提交', refs: ['tag: v1.0.0'], parents: [] },
]

beforeEach(() => {
  vi.resetModules()
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  calls.length = 0
  host.chrome = { webview: { postMessage(json) {
    const request = JSON.parse(json) as Request
    calls.push(request)
    const reply = (data: unknown, error?: string) => host.__bridgeCallback(JSON.stringify({ id: request.id, data, error }))
    switch (request.method) {
      case 'git.summary': return reply(summaryDto())
      case 'git.status': return reply(statusDto())
      case 'git.switch': return reply(statusDto())
      case 'git.stage': return reply(statusDto())
      case 'git.unstage': return reply(statusDto())
      case 'git.discard': return reply(statusDto())
      case 'git.commit': return reply(statusDto())
      case 'git.branches': return reply({
        current: 'main',
        local: [{ name: 'main', current: true, upstream: 'origin/main', date: new Date().toISOString(), subject: '合并浮窗面板' }],
        remote: [{ name: 'origin/main', date: new Date().toISOString(), subject: '合并浮窗面板' }],
        tags: [{ name: 'v1.0.0', date: new Date().toISOString() }],
      })
      case 'git.stashList': return reply([])
      case 'git.remotes': return reply([{ name: 'origin', url: 'https://github.com/example/allagent.git', pushUrl: '' }])
      case 'git.log': return reply({ commits: structuredClone(demoCommits), more: false })
      case 'git.show': {
        const hash = String(request.params?.hash ?? '')
        const found = demoCommits.find(item => item.hash === hash) ?? demoCommits[0]
        return reply({ commit: structuredClone(found), body: '演示提交正文', files: [{ path: 'src/App.tsx', status: 'M', originalPath: null }] })
      }
      case 'git.diff': return reply({
        path: String(request.params?.path ?? ''), commit: null, staged: Boolean(request.params?.staged), added: 1, removed: 1, truncated: false, binary: false,
        lines: [
          { kind: 'removed', oldLine: 1, newLine: null, text: 'const theme = "light"' },
          { kind: 'added', oldLine: null, newLine: 1, text: 'const theme = "system"' },
        ],
        note: null,
      })
      case 'git.generateCommitMessage': return reply('feat: 合并浮窗面板的样式调整')
      // fetch/pull/push return a job descriptor; output arrives as git.output/git.done events.
      case 'git.fetch': return reply({ id: 'job-fetch', label: '获取远程更新' })
      case 'git.pull': return reply({ id: 'job-pull', label: '拉取更新' })
      case 'git.push': return reply({ id: 'job-1', label: '推送' })
      default: return reply(true)
    }
  } } }
})
afterEach(() => { vi.unstubAllGlobals() })

async function settle(check: () => boolean, timeout = 3000) {
  const deadline = Date.now() + timeout
  while (!check() && Date.now() < deadline) {
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
  }
}

describe('Git 面板', () => {
  it('merges every change into one Rider-style list and renders the diff on click', async () => {
    const panels = (await import('../src/stores/panels')).usePanelsStore()
    panels.openPanel('git')
    const { GitPanel } = await import('../src/components/GitPanel')
    const element = document.createElement('div')
    document.body.appendChild(element)
    const root = createRoot(element)
    try {
      await act(async () => { root.render(<GitPanel />) })
      await settle(() => element.textContent?.includes('src/App.tsx') === true && element.textContent?.includes('main') === true, 10000)

      // 暂存 / 未暂存 / 未跟踪合并为一个「更改」列表，不再分成两组
      expect(element.textContent).toContain('更改')
      expect(element.textContent).not.toContain('暂存的更改')
      expect(element.textContent).not.toContain('未进行版本管理的文件')
      expect(element.querySelector('.git-scm-group-count')?.textContent).toBe('3 个文件')
      expect(element.querySelectorAll('.git-file-check:not(.git-group-check)').length).toBe(3)
      // 默认全选（Rider 行为），工具栏与分组都提供全选
      expect(element.querySelectorAll('.git-group-check').length).toBe(1)
      expect(element.textContent).toContain('3 / 3 已勾选')
      expect(element.querySelectorAll('.git-staged-tag').length).toBe(1)

      const clearAll = element.querySelector<HTMLButtonElement>('button[aria-label="取消全选"]')!
      await act(async () => { clearAll.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
      await settle(() => element.textContent?.includes('0 / 3 已勾选') === true, 5000)
      const selectAll = element.querySelector<HTMLButtonElement>('button[aria-label="全选所有更改"]')!
      await act(async () => { selectAll.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
      await settle(() => element.textContent?.includes('3 / 3 已勾选') === true, 5000)

      // 文件名与灰色目录分开渲染
      expect(element.textContent).toContain('GitPanel.tsx')
      expect(element.textContent).toContain('src/components/')

      const row = [...element.querySelectorAll<HTMLElement>('.git-file-main')].find(node => node.textContent?.includes('App.tsx'))!
      await act(async () => { row.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
      await settle(() => element.textContent?.includes('const theme = "system"') === true, 10000)
      expect(calls).toContainEqual(expect.objectContaining({ method: 'git.diff', params: { path: 'src/App.tsx', staged: true, commit: null } }))
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  }, 20000)

  it('keeps switched tabs on the host state: branch switching refreshes the status', async () => {
    const panels = (await import('../src/stores/panels')).usePanelsStore()
    panels.openPanel('git')
    const git = (await import('../src/stores/git')).useGitStore()
    await git.refresh()
    await settle(() => git.status !== null)
    expect(git.summary?.branch).toBe('main')

    await git.switchBranch('feature/panels')
    expect(calls).toContainEqual(expect.objectContaining({ method: 'git.switch', params: { name: 'feature/panels', create: false, startPoint: null } }))
  }, 15000)

  it('streams a push job into the operation console', async () => {
    const panels = (await import('../src/stores/panels')).usePanelsStore()
    panels.openPanel('git')
    const git = (await import('../src/stores/git')).useGitStore()
    await git.push(true)
    await settle(() => git.job?.id === 'job-1')

    host.__bridgeCallback(JSON.stringify({ type: 'event', eventName: 'git.output', data: { jobId: git.job!.id, text: 'To github.com:example/allagent.git', error: false } }))
    host.__bridgeCallback(JSON.stringify({ type: 'event', eventName: 'git.done', data: { jobId: git.job!.id, label: '推送', ok: true, message: '推送完成。' } }))
    await settle(() => git.job?.done === true)

    expect(git.job?.lines.map(line => line.text)).toContain('To github.com:example/allagent.git')
    expect(git.job?.ok).toBe(true)
  }, 15000)

  it('renders the commit graph with lanes, ref chips and a detail pane', async () => {
    const panels = (await import('../src/stores/panels')).usePanelsStore()
    panels.openPanel('git')
    const { GitPanel } = await import('../src/components/GitPanel')
    const element = document.createElement('div')
    document.body.appendChild(element)
    const root = createRoot(element)
    try {
      await act(async () => { root.render(<GitPanel />) })
      await settle(() => element.querySelectorAll('.git-tab').length > 0, 10000)

      const logTab = [...element.querySelectorAll<HTMLElement>('.git-tab')].find(node => node.textContent === '日志')!
      await act(async () => { logTab.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
      await settle(() => element.querySelectorAll('.git-log-row').length === 3, 10000)

      // 每一个提交都有泳道圆点，分支与标签渲染成彩色 chip
      expect(element.querySelectorAll('.git-graph-cell circle').length).toBe(3)
      expect(element.querySelector('.git-graph-cell line, .git-graph-cell path')).not.toBeNull()
      expect(element.querySelector('.git-ref-chip.head')?.textContent).toContain('main')
      expect(element.querySelector('.git-ref-chip.remote')?.textContent).toContain('origin/main')
      expect(element.querySelector('.git-ref-chip.local')?.textContent).toContain('feature/panels')
      expect(element.querySelector('.git-ref-chip.tag')?.textContent).toContain('v1.0.0')
      // 作者与日期列
      expect(element.querySelectorAll('.git-log-author').length).toBe(3)
      expect(element.querySelectorAll('.git-log-date').length).toBe(3)
      // 侧栏引用树
      expect(element.textContent).toContain('所有分支')
      expect(element.textContent).toContain('feature/panels')

      // 选中提交 → 右侧详情（文件列表）
      const row = element.querySelectorAll<HTMLElement>('.git-log-row')[0]
      await act(async () => { row.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
      await settle(() => element.textContent?.includes('软回退到此提交') === true, 10000)
      expect(element.querySelector('.git-log-detail')).not.toBeNull()
      expect(calls).toContainEqual(expect.objectContaining({ method: 'git.show' }))
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  }, 25000)

  it('fills the commit box with the AI-drafted message', async () => {
    const panels = (await import('../src/stores/panels')).usePanelsStore()
    panels.openPanel('git')
    const { GitPanel } = await import('../src/components/GitPanel')
    const element = document.createElement('div')
    document.body.appendChild(element)
    const root = createRoot(element)
    try {
      await act(async () => { root.render(<GitPanel />) })
      await settle(() => element.textContent?.includes('AI 生成') === true, 10000)

      const button = [...element.querySelectorAll('button')].find(node => node.textContent?.includes('AI 生成'))!
      await act(async () => { button.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
      await settle(() => (element.querySelector('textarea.git-commit-input') as HTMLTextAreaElement | null)?.value === 'feat: 合并浮窗面板的样式调整', 10000)

      const textarea = element.querySelector('textarea.git-commit-input') as HTMLTextAreaElement
      expect(textarea.value).toBe('feat: 合并浮窗面板的样式调整')
      expect(calls).toContainEqual(expect.objectContaining({ method: 'git.generateCommitMessage' }))
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  }, 20000)
})
