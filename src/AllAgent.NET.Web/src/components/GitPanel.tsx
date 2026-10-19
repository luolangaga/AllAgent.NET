import { useEffect, useRef, useState } from 'react'
import {
  AlertTriangle, ChevronDown, CloudDownload, CloudUpload, Download, GitBranch, RefreshCw, X,
} from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Spinner } from '@/components/ui/spinner'
import { StatusDot } from '@/components/ui/status-dot'
import { Tooltip } from '@/components/ui/b-tooltip'
import { Confirm } from '@/components/shared'
import { FloatingPanel } from '@/components/FloatingPanel'
import { EmptyState } from '@/components/PageShell'
import { ChangesTab } from './git/ChangesTab'
import { LogTab } from './git/LogTab'
import { BranchesTab } from './git/BranchesTab'
import { StashTab } from './git/StashTab'
import { useStore } from '@/lib/store'
import { usePanelsStore } from '@/stores/panels'
import { useGitStore } from '@/stores/git'
import { useNoticesStore } from '@/stores/ui'

type GitTab = 'changes' | 'log' | 'branches' | 'stash'

const OPERATION_LABELS: Record<string, string> = {
  merge: '合并', rebase: '变基', 'cherry-pick': '拣选提交', revert: '还原提交',
}

interface ConfirmState { title: string; description?: string; confirmLabel?: string; action: () => Promise<unknown> }

/** The Git bubble: status/staging/commit, history, branches and stash over the host's `git.*` group. */
export function GitPanel() {
  const panels = useStore(usePanelsStore)
  const git = useStore(useGitStore)
  const notices = useStore(useNoticesStore)
  const [tab, setTab] = useState<GitTab>('changes')
  const [branchMenu, setBranchMenu] = useState(false)
  const [confirm, setConfirm] = useState<ConfirmState | null>(null)
  const menuRef = useRef<HTMLDivElement>(null)
  const open = panels.open.git

  useEffect(() => {
    if (!open) return
    void git.refresh().catch(notices.report)
  }, [open])

  useEffect(() => {
    if (!branchMenu) return
    const close = (event: PointerEvent) => {
      if (!menuRef.current?.contains(event.target as Node)) setBranchMenu(false)
    }
    window.addEventListener('pointerdown', close)
    return () => window.removeEventListener('pointerdown', close)
  }, [branchMenu])

  async function run(action: () => Promise<unknown>): Promise<void> {
    try { await action() } catch (error) { notices.report(error) }
  }

  async function pushWithFallback(): Promise<void> {
    const setUpstream = !git.summary?.upstream
    try {
      await git.push(setUpstream)
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      if (/non-fast-forward|\[rejected\]|fetch first/i.test(message)) {
        setConfirm({
          title: '远程有新提交，推送被拒绝',
          description: '可以先「拉取」合并远程更新；或使用 --force-with-lease 覆盖远程（若远程分支没有他人的新提交）。',
          confirmLabel: '强制推送（force-with-lease）',
          action: () => git.push(false, true),
        })
        return
      }
      notices.report(error)
    }
  }

  if (!open) return null

  const summary = git.summary
  const counts = summary ? summary.staged + summary.unstaged + summary.untracked + summary.conflicted : 0
  const repoName = summary?.root ? summary.root.split(/[\\/]/).filter(Boolean).at(-1) ?? summary.root : ''

  function body() {
    if (git.reportError) return <div className="git-state">
      <AlertTriangle size={20} />
      <p>{git.reportError}</p>
      <Button onClick={() => void git.load().catch(notices.report)} size="sm" variant="outline">重试</Button>
    </div>
    if (!summary) return <div className="git-state"><Spinner size="lg" /><p>正在检测仓库…</p></div>
    if (!summary.available) return <div className="git-state">
      <EmptyState title="没有找到 Git">
        终端面板、文件浏览等不受影响。可以一键安装 Git for Windows（通过 winget），或手动安装后重启 AllAgent。
      </EmptyState>
      <Button disabled={Boolean(git.busy)} onClick={() => void run(async () => {
        const result = await git.installGit()
        notices.notify(result.ok ? 'Git 已就绪' : 'Git 安装未完成', result.message, result.ok ? 'success' : 'destructive')
      })} size="sm">{git.busy || '通过 winget 安装 Git'}</Button>
    </div>
    if (!summary.repo) return <div className="git-state">
      <EmptyState title="当前工作区还不是 Git 仓库">
        初始化后即可在此管理提交、分支与远程。初始化会在工作区根目录创建 .git（不会改动任何现有文件）。
      </EmptyState>
      <Button disabled={Boolean(git.busy)} onClick={() => void run(async () => {
        await git.init()
        notices.notify('仓库已初始化', summary.root, 'success')
      })} size="sm">初始化仓库</Button>
    </div>
    return <>
      {git.status?.operation ? <div className="git-banner">
        <AlertTriangle size={13} />
        <span>
          正在进行「{OPERATION_LABELS[git.status.operation] ?? git.status.operation}」操作
          {git.status.conflicted.length ? ` · ${git.status.conflicted.length} 个文件冲突` : ''}
        </span>
        <Button onClick={() => void run(() => git.continueOperation())} size="xs" variant="outline">继续</Button>
        <Button onClick={() => setConfirm({
          title: '中止当前操作？', description: '工作区将恢复到操作开始前（已解决的冲突会被撤销）。', confirmLabel: '中止',
          action: () => git.abortOperation(),
        })} size="xs" variant="ghost">中止</Button>
      </div> : null}
      {git.job ? <JobConsole /> : null}
      <div className="git-tabs">
        {([['changes', `更改${counts ? ` · ${counts}` : ''}`], ['log', '日志'], ['branches', '分支'], ['stash', '贮藏']] as Array<[GitTab, string]>)
          .map(([id, label]) => <button
            className={`git-tab${tab === id ? ' is-active' : ''}`}
            key={id}
            onClick={() => setTab(id)}
            type="button"
          >{label}</button>)}
      </div>
      <div className="git-body">
        {tab === 'changes' ? <ChangesTab onShowLog={() => setTab('log')} />
          : tab === 'log' ? <LogTab />
            : tab === 'branches' ? <BranchesTab />
              : <StashTab />}
      </div>
    </>
  }

  return <>
    <FloatingPanel
      defaultRect={{ x: Math.max(24, Math.round((window.innerWidth - 1000) / 2)), y: 56, width: 1000, height: 640 }}
      headerExtra={<>
        {summary?.repo ? <span className="git-head-info">
          <span className="mono git-repo" title={summary.root}>{repoName}</span>
          <span className="git-branch-wrap" ref={menuRef}>
            <button className="git-branch-chip" onClick={() => setBranchMenu(menu => !menu)} type="button">
              <GitBranch size={12} />
              {summary.branch ?? (summary.detached ? '游离 HEAD' : '无分支')}
              {summary.ahead ? <span className="git-ahead">↑{summary.ahead}</span> : null}
              {summary.behind ? <span className="git-behind">↓{summary.behind}</span> : null}
              <ChevronDown size={12} />
            </button>
            {branchMenu ? <div className="git-branch-menu">
              {(git.branches?.local ?? []).map(branch => <button
                className={`git-branch-item${branch.current ? ' is-current' : ''}`}
                key={branch.name}
                onClick={() => { setBranchMenu(false); void run(() => git.switchBranch(branch.name)) }}
                type="button"
              >
                <GitBranch size={12} />
                <span>{branch.name}</span>
                {branch.current ? <Badge color="green" size="sm" variant="dot">当前</Badge> : null}
              </button>)}
              <button
                className="git-branch-item"
                onClick={() => { setBranchMenu(false); setTab('branches') }}
                type="button"
              >
                <ChevronDown size={12} style={{ transform: 'rotate(-90deg)' }} />
                <span>管理分支与远程…</span>
              </button>
            </div> : null}
          </span>
        </span> : null}
        <Tooltip content="获取远程更新（fetch --all --prune）">
          <Button
            icon={<Download size={13} />}
            iconLabel="获取远程更新"
            onClick={() => void run(() => git.fetch())}
            size="icon-xs"
            variant="ghost"
          />
        </Tooltip>
        <Tooltip content="拉取并合并远程更新">
          <Button icon={<CloudDownload size={13} />} iconLabel="拉取更新" onClick={() => void run(() => git.pull())} size="icon-xs" variant="ghost" />
        </Tooltip>
        <Tooltip content={summary?.upstream ? '推送到远程' : '首次推送并设置上游分支'}>
          <Button icon={<CloudUpload size={13} />} iconLabel="推送" onClick={() => void pushWithFallback()} size="icon-xs" variant="ghost" />
        </Tooltip>
        <Tooltip content="刷新状态">
          <Button icon={<RefreshCw size={12} />} iconLabel="刷新 Git 状态" onClick={() => void git.refresh().catch(notices.report)} size="icon-xs" variant="ghost" />
        </Tooltip>
      </>}
      icon={<GitBranch size={13} />}
      id="git"
      minHeight={420}
      minWidth={720}
      onClose={() => panels.closePanel('git')}
      title="Git 管理"
    >
      {body()}
    </FloatingPanel>
    {confirm ? <Confirm
      confirmLabel={confirm.confirmLabel}
      description={confirm.description}
      onClose={() => setConfirm(null)}
      onConfirm={confirm.action}
      title={confirm.title}
    /> : null}
  </>
}

/** The streamed fetch/pull/push console; auto-scrolls while lines arrive. */
function JobConsole() {
  const git = useStore(useGitStore)
  const scrollRef = useRef<HTMLPreElement>(null)
  const lines = git.job?.lines.length ?? 0

  useEffect(() => {
    const node = scrollRef.current
    if (node) node.scrollTop = node.scrollHeight
  }, [lines])

  if (!git.job) return null
  const job = git.job
  return <div className={`git-console${job.done && !job.ok ? ' is-error' : ''}`}>
    <div className="git-console-head">
      <StatusDot animate={!job.done} size="sm" tone={job.done ? (job.ok ? 'success' : 'error') : 'active'} />
      <span>{job.label}{job.done ? (job.ok ? '完成' : '失败') : '进行中…'}</span>
      <Tooltip content="关闭控制台"><Button icon={<X size={11} />} iconLabel="关闭控制台" onClick={() => git.dismissJob()} size="icon-xs" variant="ghost" /></Tooltip>
    </div>
    <pre className="git-console-output" ref={scrollRef}>
      {job.lines.length ? job.lines.map(line => line.text).join('\n') : '（等待输出…）'}
    </pre>
    {job.done && !job.ok ? <p className="git-console-error">{job.message}</p> : null}
  </div>
}
