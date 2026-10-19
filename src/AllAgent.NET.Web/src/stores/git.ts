import { defineStore, ref } from '@/lib/store'
import { bridgeEvents, invoke } from '@/bridge'
import type {
  GitBranches, GitCommit, GitDiff, GitIdentity, GitInstallResult, GitJob, GitLog, GitRemoteInfo,
  GitShow, GitStash, GitStatus, GitSummary,
} from '@/types'

const INSTALL_TIMEOUT = 16 * 60 * 1000

export interface GitDiffTarget {
  path: string | null
  staged: boolean
  commit?: string | null
}

const emptySummary = (available: boolean, root: string): GitSummary => ({
  available, version: '', repo: false, root, branch: null, detached: false, upstream: null,
  ahead: 0, behind: 0, operation: null, staged: 0, unstaged: 0, untracked: 0, conflicted: 0,
})

export const useGitStore = defineStore('git', () => {
  const loaded = ref(false)
  const reportError = ref('')
  const summary = ref<GitSummary | null>(null)
  const status = ref<GitStatus | null>(null)
  const branches = ref<GitBranches | null>(null)
  const stashes = ref<GitStash[]>([])
  const remotes = ref<GitRemoteInfo[]>([])
  const identity = ref<GitIdentity | null>(null)
  const busy = ref('')
  const generating = ref(false)

  // History: paged log plus the selected commit's files. The graph tab narrows by ref/text.
  const commits = ref<GitCommit[]>([])
  const logMore = ref(false)
  const logRef = ref('')
  const logGrep = ref('')
  const logLoading = ref(false)
  const show = ref<GitShow | null>(null)
  const showLoading = ref(false)

  // Right-hand diff pane of the changes/history tabs.
  const diff = ref<GitDiff | null>(null)
  const diffLoading = ref(false)
  const diffKey = ref('')

  // Streamed network operation (fetch/pull/push): lines arrive as git.output events.
  const job = ref<{ id: string; label: string; lines: { text: string; error: boolean }[]; done: boolean; ok: boolean; message: string } | null>(null)

  /** Counts for the topbar badge; also refreshes availability when git is installed later. */
  async function load(): Promise<void> {
    try {
      summary.value = await invoke<GitSummary>('git.summary')
      reportError.value = ''
    } catch (error) {
      summary.value = emptySummary(true, '')
      reportError.value = error instanceof Error ? error.message : String(error)
    } finally {
      loaded.value = true
    }
  }

  async function loadBranches(): Promise<void> { branches.value = await invoke<GitBranches>('git.branches') }
  async function loadStashes(): Promise<void> { stashes.value = await invoke<GitStash[]>('git.stashList') }
  async function loadRemotes(): Promise<void> { remotes.value = await invoke<GitRemoteInfo[]>('git.remotes') }
  async function loadIdentity(): Promise<void> { identity.value = await invoke<GitIdentity>('git.identity') }

  function applyStatus(next: GitStatus): void {
    status.value = next
    if (summary.value) summary.value = {
      ...summary.value, repo: true, branch: next.branch ?? null, detached: next.detached,
      upstream: next.upstream ?? null, ahead: next.ahead, behind: next.behind, operation: next.operation ?? null,
      staged: next.staged.length, unstaged: next.unstaged.length, untracked: next.untracked.length, conflicted: next.conflicted.length,
    }
  }

  /** Full reload for opening the panel, switching workspaces and finishing any mutation. */
  async function refresh(): Promise<void> {
    await load()
    if (!summary.value?.repo) { status.value = null; commits.value = []; show.value = null; diff.value = null; return }
    const [statusResult] = await Promise.all([
      invoke<GitStatus>('git.status'),
      loadBranches().catch(() => { branches.value = null }),
      loadStashes().catch(() => { stashes.value = [] }),
      loadRemotes().catch(() => { remotes.value = [] }),
    ])
    applyStatus(statusResult)
  }

  async function mutate(label: string, method: string, params?: unknown): Promise<void> {
    busy.value = label
    try { applyStatus(await invoke<GitStatus>(method, params)) }
    finally { busy.value = '' }
  }

  const stage = (paths: string[]) => mutate('正在暂存…', 'git.stage', { paths })
  const stageAll = () => mutate('正在暂存全部…', 'git.stage', { paths: [], all: true })
  const unstage = (paths: string[]) => mutate('正在取消暂存…', 'git.unstage', { paths })
  const unstageAll = () => mutate('正在取消暂存…', 'git.unstage', { paths: [], all: true })
  const discard = (paths: string[], staged = false) => mutate('正在丢弃更改…', 'git.discard', { paths, staged })
  const discardUntracked = (paths: string[]) => mutate('正在删除未跟踪文件…', 'git.discard', { paths, untracked: true })
  const resolveConflict = (path: string, side: 'ours' | 'theirs') => mutate('正在解决冲突…', 'git.resolveConflict', { path, side })
  const commit = (message: string, amend = false, paths?: string[]) =>
    mutate('正在提交…', 'git.commit', { message, amend, paths: paths && paths.length > 0 ? paths : null })
  const init = async () => { await mutate('正在初始化仓库…', 'git.init'); await refresh() }
  const switchBranch = async (name: string, create = false, startPoint?: string | null) => {
    await mutate(create ? '正在创建分支…' : '正在切换分支…', 'git.switch', { name, create, startPoint: startPoint ?? null })
    await loadBranches().catch(() => {})
  }
  const deleteBranch = async (name: string, force = false) => {
    await mutate('正在删除分支…', 'git.deleteBranch', { name, force })
    await loadBranches().catch(() => {})
  }
  const merge = (name: string) => mutate('正在合并…', 'git.merge', { name })
  const rebase = (name: string) => mutate('正在变基…', 'git.rebase', { name })
  const continueOperation = () => mutate('正在继续操作…', 'git.operationContinue')
  const abortOperation = async () => { await mutate('正在中止操作…', 'git.operationAbort'); await refresh() }
  const resetSoft = (hash: string) => mutate('正在软回退…', 'git.resetSoft', { hash })
  /** "撤销上次提交": HEAD moves back one commit, its changes stay staged. */
  const undoLastCommit = async () => {
    await mutate('正在撤销上次提交…', 'git.undoLastCommit')
    await loadLog(true).catch(() => {})
  }
  const stashPush = async (message: string, includeUntracked: boolean) => {
    await mutate('正在贮藏…', 'git.stashPush', { message, includeUntracked })
    await loadStashes().catch(() => {})
  }
  const stashApply = async (index: number) => { await mutate('正在应用贮藏…', 'git.stashApply', { index }); await loadStashes().catch(() => {}) }
  const stashPop = async (index: number) => { await mutate('正在弹出贮藏…', 'git.stashPop', { index }); await loadStashes().catch(() => {}) }
  const stashDrop = async (index: number) => { await mutate('正在删除贮藏…', 'git.stashDrop', { index }); await loadStashes().catch(() => {}) }
  const saveIdentity = async (name: string, email: string) => { identity.value = await invoke<GitIdentity>('git.setIdentity', { name, email }) }

  /** AI drafting on an explicit model config; null lets the host fall back to its own choice. */
  async function generateCommitMessage(apiConfigId?: string | null): Promise<string> {
    generating.value = true
    try {
      return await invoke<string>('git.generateCommitMessage', { apiConfigId: apiConfigId ?? null }, 180000)
    } finally {
      generating.value = false
    }
  }

  const installGit = async (): Promise<GitInstallResult> => {
    busy.value = '正在通过 winget 安装 Git…'
    try {
      const result = await invoke<GitInstallResult>('git.installGit', undefined, INSTALL_TIMEOUT)
      await load()
      return result
    } finally { busy.value = '' }
  }

  /** fetch/pull/push run as host jobs and stream into the operation console. */
  async function startJob(label: string, method: string, params?: unknown): Promise<void> {
    job.value = { id: '', label, lines: [], done: false, ok: false, message: '' }
    try {
      const result = await invoke<GitJob>(method, params)
      if (job.value && job.value.label === label) job.value = { ...job.value, id: result.id, label: result.label }
    } catch (error) {
      job.value = null
      throw error
    }
  }
  const fetch = () => startJob('获取远程更新', 'git.fetch')
  const pull = (rebase = false) => startJob('拉取更新', 'git.pull', { rebase })
  const push = (setUpstream = false, forceWithLease = false) => startJob('推送', 'git.push', { setUpstream, forceWithLease })
  function dismissJob(): void { job.value = null }

  /** Paged commit graph page: an empty ref means all branches. Text search runs server-side. */
  async function loadLog(reset = true): Promise<void> {
    logLoading.value = true
    try {
      const skip = reset ? 0 : commits.value.length
      const result = await invoke<GitLog>('git.log', {
        skip,
        take: 60,
        all: logRef.value.length === 0,
        ref: logRef.value || null,
        grep: logGrep.value || null,
      })
      commits.value = reset ? result.commits : [...commits.value, ...result.commits]
      logMore.value = result.more
      if (reset) show.value = null
    } finally {
      logLoading.value = false
    }
  }

  /** Ref sidebar clicks and text-search debounce both land here. */
  function setLogFilter(ref: string, grep: string): void {
    logRef.value = ref
    logGrep.value = grep
  }

  async function loadShow(hash: string): Promise<void> {
    showLoading.value = true
    diff.value = null
    try { show.value = await invoke<GitShow>('git.show', { hash }) }
    finally { showLoading.value = false }
  }

  async function openDiff(target: GitDiffTarget): Promise<void> {
    diffKey.value = target.commit ? `${target.commit}:${target.path ?? ''}` : (target.path ?? '全部更改')
    diffLoading.value = true
    try {
      diff.value = await invoke<GitDiff>('git.diff', { path: target.path, staged: target.staged, commit: target.commit ?? null })
    } finally {
      diffLoading.value = false
    }
  }

  function closeDiff(): void { diff.value = null; diffKey.value = '' }

  function reset(): void {
    loaded.value = false
    status.value = null
    branches.value = null
    stashes.value = []
    commits.value = []
    logRef.value = ''
    logGrep.value = ''
    show.value = null
    diff.value = null
    job.value = null
  }

  return {
    loaded, reportError, summary, status, branches, stashes, remotes, identity, busy, generating,
    commits, logMore, logRef, logGrep, logLoading, show, showLoading, diff, diffLoading, diffKey, job,
    load, refresh, stage, stageAll, unstage, unstageAll, discard, discardUntracked, resolveConflict,
    commit, init, switchBranch, deleteBranch, merge, rebase, continueOperation, abortOperation, resetSoft, undoLastCommit,
    stashPush, stashApply, stashPop, stashDrop, loadBranches, loadStashes, loadRemotes, loadIdentity, saveIdentity, installGit,
    generateCommitMessage,
    fetch, pull, push, dismissJob, loadLog, setLogFilter, loadShow, openDiff, closeDiff, reset,
  }
})

// Job output and completion arrive as events; the store is the single consumer of the console.
bridgeEvents.on('git.output', payload => {
  const store = useGitStore()
  if (!store.job || store.job.id !== payload.jobId) return
  store.job = { ...store.job, lines: [...store.job.lines, { text: payload.text, error: payload.error }].slice(-1500) }
})

bridgeEvents.on('git.done', payload => {
  const store = useGitStore()
  if (!store.job || store.job.id !== payload.jobId) return
  store.job = { ...store.job, done: true, ok: payload.ok, message: payload.message }
  void store.refresh().catch(() => {})
})
