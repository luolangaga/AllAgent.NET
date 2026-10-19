import { X } from 'lucide-react'
import { Button } from '@/components/ui/b-button'
import { Spinner } from '@/components/ui/spinner'
import { DiffLines } from '@/components/DiffLines'
import { FileTypeIcon } from '@/components/FileTypeIcon'
import type { GitDiff } from '@/types'

/** The Git panel's diff pane: same row rendering as tool cards, fed by the host's parsed patch. */
export function GitDiffView({ diff, loading, onClose }: { diff: GitDiff | null; loading: boolean; onClose?: () => void }) {
  if (loading) return <div className="git-pane-empty"><Spinner size="lg" /><p>正在读取差异…</p></div>
  if (!diff) return null
  return <div className="git-diff">
    <div className="tool-diff-head">
      <FileTypeIcon name={diff.path} size={14} />
      <span className="tool-diff-path" title={diff.path}>{diff.path}</span>
      {diff.commit ? <span className="tool-diff-tag mono">{diff.commit.slice(0, 8)}</span> : null}
      {diff.staged ? <span className="tool-diff-tag">已暂存</span> : null}
      <span className="tool-metric add">+{diff.added}</span>
      <span className="tool-metric del">−{diff.removed}</span>
      {onClose ? <Button iconLabel="关闭差异" onClick={onClose} size="icon-xs" variant="ghost"><X size={11} /></Button> : null}
    </div>
    <div className="git-diff-scroll">
      {diff.binary
        ? <p className="git-note">{diff.note ?? '二进制文件无法显示文本差异。'}</p>
        : diff.lines.length
          ? <DiffLines lines={diff.lines} />
          : <p className="git-note">没有内容变化。</p>}
      {diff.note && !diff.binary ? <p className="git-note">{diff.note}</p> : null}
      {diff.truncated ? <p className="git-note">差异过大，仅显示前 {diff.lines.length} 行。</p> : null}
    </div>
  </div>
}
