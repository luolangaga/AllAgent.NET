import { useEffect, useState } from 'react'
import { GitBranch, GitMerge, RefreshCw, Trash2, Upload } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/b-tooltip'
import { Confirm } from '@/components/shared'
import { useStore } from '@/lib/store'
import { formatRelative } from '@/lib/format'
import { useGitStore } from '@/stores/git'
import { useNoticesStore } from '@/stores/ui'

interface ConfirmState { title: string; description?: string; confirmLabel?: string; action: () => Promise<unknown> }

/** Branches: local (switch/merge/rebase/delete), remote checkout, tags and remotes. */
export function BranchesTab() {
  const git = useStore(useGitStore)
  const notices = useStore(useNoticesStore)
  const [newBranch, setNewBranch] = useState('')
  const [confirm, setConfirm] = useState<ConfirmState | null>(null)
  const branches = git.branches

  useEffect(() => {
    if (!branches) void git.loadBranches().catch(notices.report)
    if (!git.remotes.length) void git.loadRemotes().catch(() => {})
  }, [])

  async function run(action: () => Promise<unknown>, success?: string): Promise<void> {
    try {
      await action()
      if (success) notices.notify(success, undefined, 'success')
    } catch (error) { notices.report(error) }
  }

  async function createBranch(): Promise<void> {
    const name = newBranch.trim()
    if (!name) { notices.notify('请输入分支名称', undefined, 'warning'); return }
    await run(() => git.switchBranch(name, true), `已创建并切换到 ${name}`)
    setNewBranch('')
  }

  async function deleteBranch(name: string, force: boolean): Promise<void> {
    try {
      await git.deleteBranch(name, force)
      notices.notify('已删除分支', name, 'success')
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error)
      if (!force && /not fully merged|未合并/i.test(message)) {
        setConfirm({
          title: `强制删除分支「${name}」？`,
          description: '该分支有未合并的提交；强制删除会永久丢失这些提交。',
          confirmLabel: '强制删除',
          action: () => git.deleteBranch(name, true),
        })
        return
      }
      notices.report(error)
    }
  }

  if (!branches) return <div className="git-pane-empty"><Spinner size="lg" /><p>正在读取分支…</p></div>

  return <>
    <div className="git-branches">
      <div className="git-branch-create">
        <Input
          aria-label="新分支名称"
          onKeyDown={event => { if (event.key === 'Enter') void createBranch() }}
          onValueChange={setNewBranch}
          placeholder="新分支名称，例如 feature/web-panels"
          size="sm"
          value={newBranch}
        />
        <Button disabled={Boolean(git.busy)} onClick={() => void createBranch()} size="sm">新建分支</Button>
        <Tooltip content="刷新分支、标签与远程信息">
          <Button icon={<RefreshCw size={13} />} iconLabel="刷新分支" onClick={() => void run(async () => { await git.loadBranches(); await git.loadRemotes() })} size="icon-sm" variant="ghost" />
        </Tooltip>
      </div>

      <section className="git-section">
        <h4>本地分支 · {branches.local.length}</h4>
        <div className="git-branch-list">
          {branches.local.map(branch => <div className="git-branch-row" key={branch.name}>
            <span className="git-branch-name">
              <GitBranch aria-hidden size={12} />
              {branch.name}
              {branch.current ? <Badge color="green" size="sm" variant="dot">当前</Badge> : null}
              {branch.upstream ? <Badge color="gray" size="sm">→ {branch.upstream}</Badge> : null}
            </span>
            <span className="git-branch-meta" title={branch.subject}>{branch.subject || '（无提交信息）'} · {formatRelative(branch.date)}</span>
            <div className="git-branch-actions">
              {branch.current ? null : <>
                <Button disabled={Boolean(git.busy)} onClick={() => void run(() => git.switchBranch(branch.name), `已切换到 ${branch.name}`)} size="xs" variant="outline">切换</Button>
                <Button disabled={Boolean(git.busy)} onClick={() => setConfirm({
                  title: `把「${branch.name}」合并到当前分支？`, description: '若存在冲突，可在顶部横幅中止或解决。', confirmLabel: '合并',
                  action: () => git.merge(branch.name),
                })} size="xs" variant="ghost">合并</Button>
                <Button disabled={Boolean(git.busy)} onClick={() => setConfirm({
                  title: `把当前分支变基到「${branch.name}」？`, description: '会重写当前分支上尚未推送的提交；若已推送请谨慎。', confirmLabel: '变基',
                  action: () => git.rebase(branch.name),
                })} size="xs" variant="ghost">变基</Button>
                <Tooltip content="删除分支">
                  <Button
                    icon={<Trash2 size={12} />}
                    iconLabel="删除分支"
                    onClick={() => setConfirm({
                      title: `删除分支「${branch.name}」？`, description: '未合并的分支需要二次确认后强制删除。', confirmLabel: '删除',
                      action: () => deleteBranch(branch.name, false),
                    })}
                    size="icon-xs"
                    variant="ghost"
                  />
                </Tooltip>
              </>}
            </div>
          </div>)}
          {!branches.local.length ? <p className="git-note">还没有本地分支。</p> : null}
        </div>
      </section>

      <section className="git-section">
        <h4>远程分支 · {branches.remote.length}</h4>
        <div className="git-branch-list">
          {branches.remote.map(branch => <div className="git-branch-row" key={branch.name}>
            <span className="git-branch-name"><Upload aria-hidden size={12} />{branch.name}</span>
            <span className="git-branch-meta" title={branch.subject}>{branch.subject || '（无提交信息）'}</span>
            <div className="git-branch-actions">
              <Button disabled={Boolean(git.busy)} onClick={() => {
                const local = branch.name.includes('/') ? branch.name.split('/').slice(1).join('/') : branch.name
                void run(() => git.switchBranch(local, true, branch.name), `已检出 ${local}`)
              }} size="xs" variant="outline">检出</Button>
            </div>
          </div>)}
          {!branches.remote.length ? <p className="git-note">没有远程分支；配置远程仓库后先「获取」。</p> : null}
        </div>
      </section>

      <section className="git-section">
        <h4>标签 · {branches.tags.length}</h4>
        <div className="git-tags">
          {branches.tags.map(tag => <Tooltip content={tag.date ? `创建于 ${tag.date}` : '标签'} key={tag.name}>
            <span className="git-tag mono">{tag.name}</span>
          </Tooltip>)}
          {!branches.tags.length ? <p className="git-note">没有标签。</p> : null}
        </div>
      </section>

      <section className="git-section">
        <h4>远程仓库 · {git.remotes.length}</h4>
        <div className="git-branch-list">
          {git.remotes.map(remote => <div className="git-branch-row" key={remote.name}>
            <span className="git-branch-name"><GitMerge aria-hidden size={12} />{remote.name}</span>
            <span className="git-branch-meta mono" title={remote.url}>{remote.url}{remote.pushUrl && remote.pushUrl !== remote.url ? ` · push: ${remote.pushUrl}` : ''}</span>
          </div>)}
          {!git.remotes.length ? <p className="git-note">没有配置远程仓库（git remote add origin …）。</p> : null}
        </div>
      </section>
    </div>
    {confirm ? <Confirm
      confirmLabel={confirm.confirmLabel}
      description={confirm.description}
      onClose={() => setConfirm(null)}
      onConfirm={confirm.action}
      title={confirm.title}
    /> : null}
  </>
}
