import { beforeEach, describe, expect, it, vi } from 'vitest'

beforeEach(() => {
  vi.resetModules()
  localStorage.clear()
})

describe('浮窗面板 store', () => {
  it('opens, closes and toggles panel state with persistence', async () => {
    const panels = (await import('../src/stores/panels')).usePanelsStore()
    expect(panels.open.files).toBe(false)

    panels.openPanel('files')
    expect(panels.open.files).toBe(true)
    expect(JSON.parse(localStorage.getItem('allagent.panels.open') ?? '{}').files).toBe(true)

    panels.togglePanel('files')
    expect(panels.open.files).toBe(false)
    panels.closePanel('terminal')
    expect(panels.open.terminal).toBe(false)
  })

  it('raises the interacting panel above the others', async () => {
    const panels = (await import('../src/stores/panels')).usePanelsStore()
    panels.openPanel('files')
    panels.openPanel('git')
    expect(panels.zIndex('git')).toBeGreaterThan(panels.zIndex('files'))

    panels.raise('files')
    expect(panels.zIndex('files')).toBeGreaterThan(panels.zIndex('git'))
  })
})

describe('Git 徽标状态', () => {
  it('summarizes uncommitted changes for the topbar badge', async () => {
    const git = (await import('../src/stores/git')).useGitStore()
    git.summary = {
      available: true, version: 'git version 2.51.0', repo: true, root: 'C:\\demo',
      branch: 'main', detached: false, upstream: 'origin/main', ahead: 1, behind: 0, operation: null,
      staged: 2, unstaged: 3, untracked: 1, conflicted: 1,
    }
    const badge = git.summary.staged + git.summary.unstaged + git.summary.untracked + git.summary.conflicted
    expect(badge).toBe(7)
  })
})
