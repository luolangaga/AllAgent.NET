import { useState } from 'react'
import {
  AlertTriangle, Check, ChevronRight, History, ListChecks, RotateCcw, Sparkles, Trash2, Undo2, UserCog,
} from 'lucide-react'
import { Button } from '@/components/ui/b-button'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/b-tooltip'
import { Confirm, Modal, Field } from '@/components/shared'
import { FileTypeIcon } from '@/components/FileTypeIcon'
import { GitDiffView } from './GitDiffView'
import { useStore } from '@/lib/store'
import { useApiConfigsStore } from '@/stores/apiConfigs'
import { useGitStore } from '@/stores/git'
import { useModelSelectionStore } from '@/stores/modelSelection'
import { useSettingsStore } from '@/stores/settings'
import { useNoticesStore } from '@/stores/ui'
import { usePanelsStore } from '@/stores/panels'
import type { GitFileEntry } from '@/types'

interface ConfirmState { title: string; description?: string; confirmLabel?: string; action: () => Promise<unknown> }

/** One merged change row: a path may be staged, modified in the worktree, or both. */
interface ChangeRow {
  entry: GitFileEntry
  staged: boolean
  modified: boolean
  untracked: boolean
}

/** Status letter → colour: conflicts and deletions red, additions green, everything else amber/grey. */
function letterTone(letter: string): string {
  switch (letter) {
    case 'U': case 'D': return 'del'
    case 'A': return 'add'
    case 'R': case 'C': return 'info'
    case '?': return 'new'
    default: return 'edit'
  }
}

function letterOf(row: ChangeRow): string {
  if (row.untracked) return 'U'
  const candidate = row.entry.worktree !== '.' && row.entry.worktree !== '?' ? row.entry.worktree : row.entry.index
  return candidate === '?' ? 'U' : candidate
}

/** Splits a path into the file name and its trailing directory, which shows dimmed like VSCode. */
function splitPath(path: string): { name: string; directory: string } {
  const slash = path.lastIndexOf('/')
  return slash < 0 ? { name: path, directory: '' } : { name: path.slice(slash + 1), directory: path.slice(0, slash + 1) }
}

const LIST_LIMIT = 400

/**
 * Rider-style Source Control view: one merged list of every change (staged, modified and
 * untracked together) with per-file checkboxes choosing what the next commit contains. Committing
 * stages the checked files itself, so there is no separate staging area to manage.
 */
export function ChangesTab({ onShowLog }: { onShowLog?: () => void }) {
  const git = useStore(useGitStore)
  const notices = useStore(useNoticesStore)
  const panels = useStore(usePanelsStore)
  const settingsStore = useStore(useSettingsStore)
  const api = useStore(useApiConfigsStore)
  const modelSelection = useStore(useModelSelectionStore)
  const [message, setMessage] = useState('')
  const [amend, setAmend] = useState(false)
  const [confirm, setConfirm] = useState<ConfirmState | null>(null)
  const [identityOpen, setIdentityOpen] = useState(false)
  const [identityName, setIdentityName] = useState('')
  const [identityEmail, setIdentityEmail] = useState('')
  // Paths the user unchecked; everything else is included in the commit (Rider's default).
  const [excluded, setExcluded] = useState<string[]>([])
  const [collapsed, setCollapsed] = useState<string[]>([])
  const status = git.status
  const busy = Boolean(git.busy)

  function isIdentityError(error: unknown): boolean {
    return String(error instanceof Error ? error.message : error).includes('提交身份')
  }

  async function run(action: () => Promise<unknown>): Promise<void> {
    try { await action() } catch (error) {
      if (isIdentityError(error)) { openIdentity(); return }
      // A failed path operation usually means the list is stale (file committed/deleted elsewhere):
      // refresh the status before reporting so the next click works.
      void git.refresh().catch(() => {})
      notices.report(error)
    }
  }

  function openIdentity(): void {
    void git.loadIdentity().then(() => {
      setIdentityName(git.identity?.name ?? '')
      setIdentityEmail(git.identity?.email ?? '')
    }).catch(() => {})
    setIdentityOpen(true)
  }

  if (!status) return <div className="git-pane-empty"><Spinner size="lg" /><p>正在读取仓库状态…</p></div>

  // Merge the three git lists into one row per path; a path keeps all of its state flags.
  const merged = new Map<string, ChangeRow>()
  const rowOf = (entry: GitFileEntry): ChangeRow => {
    const existing = merged.get(entry.path)
    if (existing) return existing
    const created: ChangeRow = { entry, staged: false, modified: false, untracked: false }
    merged.set(entry.path, created)
    return created
  }
  for (const entry of status.staged) { const row = rowOf(entry); row.staged = true; row.entry = entry }
  for (const entry of status.unstaged) { const row = rowOf(entry); row.modified = true; row.entry = entry }
  for (const entry of status.untracked) { const row = rowOf(entry); row.untracked = true; row.entry = entry }
  const changes = [...merged.values()].sort((left, right) => left.entry.path.localeCompare(right.entry.path))
  const conflicts = status.conflicted
  const selected = changes.filter(row => !excluded.includes(row.entry.path))
  const selectedPaths = selected.map(row => row.entry.path)
  const allSelected = changes.length > 0 && selected.length === changes.length
  const visible = changes.slice(0, LIST_LIMIT)
  const clean = changes.length === 0 && conflicts.length === 0
  // The Settings override wins; otherwise the model currently chosen in the composer, then the
  // configured default. Passing it explicitly keeps drafting on the user's model, not a guess.
  const commitConfigId = settingsStore.gitCommitConfigId
    || modelSelection.configId
    || settingsStore.defaultProviderId
    || api.configs.find(item => item.isEnabled)?.id
    || null

  const toggleSelected = (path: string) => setExcluded(current => current.includes(path) ? current.filter(item => item !== path) : [...current, path])
  function toggleAll(): void {
    setExcluded(allSelected ? changes.map(row => row.entry.path) : [])
  }
  const toggleCollapse = (id: string) => setCollapsed(current => current.includes(id) ? current.filter(item => item !== id) : [...current, id])

  async function submit(pushAfter: boolean): Promise<void> {
    if (conflicts.length > 0) { notices.notify('请先解决冲突', '冲突文件解决并标记后，才能提交。', 'warning'); return }
    if (!selectedPaths.length) { notices.notify('没有勾选要提交的文件', '勾选至少一个文件后再提交。', 'warning'); return }
    if (!message.trim() && !amend) { notices.notify('请填写提交信息', '或勾选「修正(M)」以复用原有信息，也可以点「AI 生成」。', 'warning'); return }
    try {
      await git.commit(message.trim(), amend, selectedPaths)
      setMessage('')
      setAmend(false)
      notices.notify('已提交', undefined, 'success')
      if (pushAfter) await git.push(!git.summary?.upstream)
    } catch (error) {
      if (isIdentityError(error)) { openIdentity(); return }
      notices.report(error)
    }
  }

  function discardSelected(): void {
    const rows = selected
    if (!rows.length) return
    setConfirm({
      title: `丢弃所选 ${rows.length} 个文件的更改？`,
      description: '内容将还原到上次提交（未跟踪文件会被删除），无法撤销。',
      confirmLabel: '丢弃',
      action: async () => {
        // Files with staged content are restored from HEAD (index + worktree) in one call;
        // the rest only need their worktree restored.
        const tracked = rows.filter(row => !row.untracked)
        const stagedPaths = tracked.filter(row => row.staged).map(row => row.entry.path)
        const worktreePaths = tracked.filter(row => !row.staged).map(row => row.entry.path)
        const untrackedPaths = rows.filter(row => row.untracked).map(row => row.entry.path)
        if (stagedPaths.length) await git.discard(stagedPaths, true)
        if (worktreePaths.length) await git.discard(worktreePaths)
        if (untrackedPaths.length) await git.discardUntracked(untrackedPaths)
        setExcluded([])
      },
    })
  }

  function rowActions(row: ChangeRow) {
    const { entry } = row
    return <Tooltip content={row.untracked ? '删除未跟踪文件' : '丢弃更改'}>
      <Button
        icon={row.untracked ? <Trash2 size={12} /> : <Undo2 size={12} />}
        iconLabel={row.untracked ? '删除文件' : '丢弃更改'}
        onClick={() => setConfirm({
          title: row.untracked ? `删除未跟踪文件「${entry.path}」？` : `丢弃「${entry.path}」的更改？`,
          description: row.untracked ? '文件将从磁盘删除，无法撤销。' : '文件将还原到上次提交的内容（含已暂存的改动），无法撤销。',
          confirmLabel: row.untracked ? '删除' : '丢弃',
          action: () => row.untracked ? git.discardUntracked([entry.path]) : git.discard([entry.path], row.staged),
        })}
        size="icon-xs"
        variant="ghost"
      />
    </Tooltip>
  }

  return <>
    <div className="git-changes">
      <div className="git-scm">
        <div className="git-scm-toolbar">
          <Tooltip content="刷新状态"><Button icon={<RotateCcw size={12} />} iconLabel="刷新状态" onClick={() => void git.refresh().catch(notices.report)} size="icon-xs" variant="ghost" /></Tooltip>
          <Tooltip content={allSelected ? '取消全选' : '全选所有更改'}>
            <Button
              icon={<ListChecks size={13} />}
              iconLabel={allSelected ? '取消全选' : '全选所有更改'}
              onClick={toggleAll}
              size="icon-xs"
              variant="ghost"
            />
          </Tooltip>
          <Tooltip content="丢弃勾选的更改"><Button
            disabled={!selected.length}
            icon={<Undo2 size={13} />}
            iconLabel="丢弃勾选的更改"
            onClick={discardSelected}
            size="icon-xs"
            variant="ghost"
          /></Tooltip>
          <span className="git-scm-selection">{changes.length ? `${selected.length} / ${changes.length} 已勾选` : ''}</span>
        </div>
        <div className="git-scm-list">
          {clean ? <p className="git-note git-clean">工作区是干净的，没有待提交的更改。</p> : null}
          {conflicts.length ? <div className="git-group">
            <div className="git-scm-group-head">
              <span className="git-scm-group-title conflicted">冲突</span>
              <span className="git-scm-group-count">{conflicts.length} 个文件</span>
            </div>
            <div className="git-files">
              {conflicts.map(entry => <div className="git-file" key={`conflict:${entry.path}`}>
                <span className="git-letter del">U</span>
                <FileTypeIcon name={entry.path} size={14} />
                <span className="git-file-name" title={entry.path}>{splitPath(entry.path).name}<span className="git-file-dir">{splitPath(entry.path).directory}</span></span>
                <div className="git-file-actions">
                  <Tooltip content="采用我的版本并标记已解决"><Button iconLabel="用我的版本" onClick={() => void run(() => git.resolveConflict(entry.path, 'ours'))} size="xs" variant="ghost">我的</Button></Tooltip>
                  <Tooltip content="采用他们的版本并标记已解决"><Button iconLabel="用他们的版本" onClick={() => void run(() => git.resolveConflict(entry.path, 'theirs'))} size="xs" variant="ghost">他们的</Button></Tooltip>
                  <Tooltip content="仅标记为已解决（工作区内容保持不变）"><Button icon={<Check size={12} />} iconLabel="标记已解决" onClick={() => void run(() => git.stage([entry.path]))} size="icon-xs" variant="ghost" /></Tooltip>
                </div>
              </div>)}
            </div>
          </div> : null}
          {changes.length ? <div className="git-group">
            <div className="git-scm-group-head">
              <button
                aria-label={collapsed.includes('changes') ? '展开' : '折叠'}
                className={`git-scm-chevron${collapsed.includes('changes') ? '' : ' is-open'}`}
                onClick={() => toggleCollapse('changes')}
                type="button"
              ><ChevronRight size={12} /></button>
              <input
                aria-label="全选所有更改"
                checked={allSelected}
                className="git-file-check git-group-check"
                onChange={toggleAll}
                ref={node => { if (node) node.indeterminate = selected.length > 0 && !allSelected }}
                type="checkbox"
              />
              <span className="git-scm-group-title">更改</span>
              <span className="git-scm-group-count">{changes.length} 个文件</span>
            </div>
            {collapsed.includes('changes') ? null : <div className="git-files">
              {visible.map(row => {
                const letter = letterOf(row)
                const { name, directory } = splitPath(row.entry.path)
                return <div className={`git-file${git.diffKey === row.entry.path ? ' is-active' : ''}`} key={row.entry.path}>
                  <input
                    aria-label={`提交 ${row.entry.path}`}
                    checked={!excluded.includes(row.entry.path)}
                    className="git-file-check"
                    onChange={() => toggleSelected(row.entry.path)}
                    type="checkbox"
                  />
                  <button
                    className="git-file-main"
                    onClick={() => void run(() => git.openDiff({ path: row.entry.path, staged: row.staged && !row.modified }))}
                    title={row.entry.originalPath ? `${row.entry.originalPath} → ${row.entry.path}` : row.entry.path}
                    type="button"
                  >
                    <span className={`git-letter ${letterTone(letter)}`}>{letter}</span>
                    <FileTypeIcon name={name} size={14} />
                    <span className="git-file-name">
                      {name}<span className="git-file-dir">{directory}</span>
                      {row.entry.originalPath ? <span className="git-file-origin"> ← {splitPath(row.entry.originalPath).name}</span> : null}
                    </span>
                    {row.staged ? <span className="git-staged-tag">已暂存</span> : null}
                  </button>
                  <div className="git-file-actions">{rowActions(row)}</div>
                </div>
              })}
              {changes.length > LIST_LIMIT ? <div className="git-scm-browse">
                <span>还有 {changes.length - LIST_LIMIT} 个文件未显示</span>
                <Button onClick={() => panels.openPanel('files')} size="xs" variant="link">浏览</Button>
              </div> : null}
            </div>}
          </div> : null}
          {git.busy ? <p className="git-note"><Spinner size="sm" /> {git.busy}</p> : null}
        </div>
        <div className="git-scm-commit">
          <div className="git-scm-commit-tools">
            <label className="git-check">
              <input checked={amend} onChange={event => setAmend(event.target.checked)} type="checkbox" />
              修正(M)
            </label>
            <span className="git-scm-spacer" />
            <Tooltip content="打开提交日志与提交图"><Button icon={<History size={13} />} iconLabel="查看日志" onClick={() => onShowLog?.()} size="icon-xs" variant="ghost" /></Tooltip>
            <Tooltip content="撤销上次提交（改动保持在暂存区）"><Button
              disabled={busy}
              icon={<RotateCcw size={13} />}
              iconLabel="撤销上次提交"
              onClick={() => setConfirm({
                title: '撤销上次提交？', description: 'HEAD 回退一次提交，改动保留在暂存区，历史将被重写。', confirmLabel: '撤销提交',
                action: () => git.undoLastCommit(),
              })}
              size="icon-xs"
              variant="ghost"
            /></Tooltip>
            <Tooltip content="用当前模型根据改动生成提交信息"><Button
              disabled={busy || git.generating || changes.length === 0}
              icon={<Sparkles size={12} />}
              iconLabel="AI 生成提交信息"
              loading={git.generating}
              onClick={() => void run(async () => {
                const generated = await git.generateCommitMessage(commitConfigId)
                setMessage(generated)
                notices.notify('已生成提交信息', '内容来自当前模型，请检查后再提交。', 'success')
              })}
              size="xs"
              variant="ghost"
            >AI 生成</Button></Tooltip>
            <Tooltip content="提交身份（user.name / user.email）"><Button icon={<UserCog size={13} />} iconLabel="提交身份" onClick={openIdentity} size="icon-xs" variant="ghost" /></Tooltip>
          </div>
          <textarea
            className="git-commit-input"
            onChange={event => setMessage(event.target.value)}
            onKeyDown={event => { if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') void submit(false) }}
            placeholder={amend ? '修正上次提交的信息（留空则沿用原信息）' : '提交消息（Ctrl+Enter 提交，或点 AI 生成）'}
            rows={3}
            spellCheck={false}
            value={message}
          />
          {conflicts.length ? <p className="git-note git-conflict-note"><AlertTriangle size={12} /> 先解决冲突再提交。</p> : null}
          <div className="git-scm-commit-actions">
            <Button className="git-commit-primary" disabled={busy || clean || !selectedPaths.length || conflicts.length > 0} onClick={() => void submit(false)} size="sm">提交 {selectedPaths.length ? `(${selectedPaths.length})` : ''}</Button>
            <Button disabled={busy || clean || conflicts.length > 0} onClick={() => void submit(true)} size="sm" variant="outline">提交并推送</Button>
          </div>
        </div>
      </div>
      <div className="git-diff-pane">
        {git.diff || git.diffLoading
          ? <GitDiffView diff={git.diff} loading={git.diffLoading} onClose={() => git.closeDiff()} />
          : <div className="git-pane-empty"><Undo2 size={18} /><p>点击文件查看差异；勾选决定本次提交包含哪些文件。</p></div>}
      </div>
    </div>
    {confirm ? <Confirm
      confirmLabel={confirm.confirmLabel}
      description={confirm.description}
      onClose={() => setConfirm(null)}
      onConfirm={confirm.action}
      title={confirm.title}
    /> : null}
    {identityOpen ? <Modal
      description="Git 用它标注每次提交的作者；保存在当前仓库的 local 配置中。"
      footer={<Button onClick={() => {
        const name = identityName.trim()
        const email = identityEmail.trim()
        if (!name || !email) { notices.notify('请填写用户名与邮箱', undefined, 'warning'); return }
        void git.saveIdentity(name, email).then(() => { setIdentityOpen(false); notices.notify('已保存提交身份', `${name} <${email}>`, 'success') }).catch(notices.report)
      }}>保存</Button>}
      onClose={() => setIdentityOpen(false)}
      size="sm"
      title="提交身份"
    >
      <Field label="用户名" onChange={setIdentityName} placeholder="例如 Luo Lan" required value={identityName} />
      <Field label="邮箱" onChange={setIdentityEmail} placeholder="例如 luolan@example.com" required value={identityEmail} />
    </Modal> : null}
  </>
}
