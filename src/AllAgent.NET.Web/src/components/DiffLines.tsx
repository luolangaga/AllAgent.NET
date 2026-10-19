import type { DiffLine } from '@/types'

/**
 * Shared diff row renderer for tool cards and the Git panel: two line-number gutters,
 * a +/− mark and the line text, styled by the existing `.tool-diff-*` classes.
 */
export function DiffLines({ lines }: { lines: DiffLine[] }) {
  return <div className="tool-diff-body">
    {lines.map((line, index) => <div className={`tool-diff-row ${line.kind}`} key={`${index}-${line.kind}`}>
      <span className="tool-diff-no">{line.oldLine ?? ''}</span>
      <span className="tool-diff-no">{line.newLine ?? ''}</span>
      <span className="tool-diff-mark">{line.kind === 'added' ? '+' : line.kind === 'removed' ? '−' : ''}</span>
      <code>{line.text || ' '}</code>
    </div>)}
  </div>
}
