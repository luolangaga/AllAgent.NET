import { useState } from 'react'
import { Archive, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/b-button'
import { Input } from '@/components/ui/input'
import { Confirm } from '@/components/shared'
import { useStore } from '@/lib/store'
import { formatDateTime } from '@/lib/format'
import { useGitStore } from '@/stores/git'
import { useNoticesStore } from '@/stores/ui'

interface ConfirmState { title: string; description?: string; confirmLabel?: string; action: () => Promise<unknown> }

/** Stash: park the working tree, then apply/pop/drop entries. */
export function StashTab() {
  const git = useStore(useGitStore)
  const notices = useStore(useNoticesStore)
  const [message, setMessage] = useState('')
  const [includeUntracked, setIncludeUntracked] = useState(false)
  const [confirm, setConfirm] = useState<ConfirmState | null>(null)

  async function run(action: () => Promise<unknown>, success?: string): Promise<void> {
    try {
      await action()
      if (success) notices.notify(success, undefined, 'success')
    } catch (error) { notices.report(error) }
  }

  async function stash(): Promise<void> {
    await run(async () => {
      await git.stashPush(message.trim(), includeUntracked)
    }, '已贮藏当前更改')
    setMessage('')
    setIncludeUntracked(false)
  }

  const dirty = Boolean(git.status && (git.status.staged.length || git.status.unstaged.length || (includeUntracked && git.status.untracked.length)))

  return <>
    <div className="git-stash">
      <div className="git-stash-create">
        <Input
          aria-label="贮藏说明"
          onValueChange={setMessage}
          placeholder="贮藏说明（可选），例如 调试中的面板改动"
          size="sm"
          value={message}
        />
        <label className="git-check">
          <input checked={includeUntracked} onChange={event => setIncludeUntracked(event.target.checked)} type="checkbox" />
          包含未跟踪文件
        </label>
        <Button disabled={Boolean(git.busy)} onClick={() => void stash()} size="sm">{dirty ? '贮藏当前更改' : '贮藏当前更改'}</Button>
      </div>
      <div className="git-stash-list">
        {git.stashes.map(stash => <div className="git-stash-row" key={stash.ref}>
          <Archive aria-hidden className="git-stash-icon" size={14} />
          <div className="git-stash-info">
            <span className="git-stash-message">{stash.message.replace(/^WIP on [^:]+:\s*/, '') || stash.ref}</span>
            <span className="git-stash-meta mono">{stash.ref} · {formatDateTime(stash.date)}</span>
          </div>
          <div className="git-stash-actions">
            <Button disabled={Boolean(git.busy)} onClick={() => void run(() => git.stashApply(stash.index), '已应用贮藏（列表保留）')} size="xs" variant="outline">应用</Button>
            <Button disabled={Boolean(git.busy)} onClick={() => void run(() => git.stashPop(stash.index), '已弹出贮藏')} size="xs" variant="ghost">弹出</Button>
            <Button
              icon={<Trash2 size={12} />}
              iconLabel="删除贮藏"
              onClick={() => setConfirm({
                title: `删除「${stash.ref}」？`, description: '贮藏记录将被永久删除，改动不再可恢复。', confirmLabel: '删除',
                action: () => git.stashDrop(stash.index),
              })}
              size="icon-xs"
              variant="ghost"
            />
          </div>
        </div>)}
        {!git.stashes.length ? <p className="git-note">还没有贮藏记录；临时切换分支前可以先把改动贮藏起来。</p> : null}
      </div>
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
