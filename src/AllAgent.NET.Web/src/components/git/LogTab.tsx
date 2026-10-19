import { useEffect, useMemo, useState, type CSSProperties } from 'react'
import { ArrowLeft, ClipboardCopy, Cloud, GitBranch, RefreshCw, Search, Tag, X } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/b-tooltip'
import { Confirm } from '@/components/shared'
import { GitDiffView } from './GitDiffView'
import { FileTypeIcon } from '@/components/FileTypeIcon'
import { useStore } from '@/lib/store'
import { buildGraph, graphColor, type GraphRow } from '@/lib/gitGraph'
import { formatGitDate } from '@/lib/format'
import { useGitStore } from '@/stores/git'
import { useNoticesStore } from '@/stores/ui'

interface ConfirmState { title: string; description?: string; confirmLabel?: string; action: () => Promise<unknown> }

const HASH_INPUT = /^[0-9a-f]{4,40}$/i

/**
 * The commit graph: a ref sidebar (branches / remotes / tags), a text-or-hash filter and the
 * topological log rendered with coloured lanes, ref chips and author/date columns. Selecting a
 * commit opens its files and diffs in the right-hand detail pane.
 */
export function LogTab() {
  const git = useStore(useGitStore)
  const notices = useStore(useNoticesStore)
  const [refQuery, setRefQuery] = useState('')
  const [text, setText] = useState(() => git.logGrep)
  const [confirm, setConfirm] = useState<ConfirmState | null>(null)
  const rows = useMemo(() => buildGraph(git.commits), [git.commits])
  // One graph column width for every row so the subject/author/date columns line up.
  const graphWidth = useMemo(() => Math.max(...[1, ...rows.map(row => row.laneCount)]) * 14 + 4, [rows])
  const hashLike = HASH_INPUT.test(text.trim())
  const visible = hashLike
    ? rows.filter(row => row.commit.hash.toLowerCase().startsWith(text.trim().toLowerCase()))
    : rows

  useEffect(() => {
    void git.loadBranches().catch(() => {})
    void git.loadRemotes().catch(() => {})
    if (!git.commits.length) void git.loadLog(true).catch(notices.report)
  }, [])

  // Debounced server-side search; hash-like input filters the loaded page client-side instead.
  useEffect(() => {
    const trimmed = text.trim()
    if (HASH_INPUT.test(trimmed)) return
    const timer = window.setTimeout(() => {
      if (git.logGrep === trimmed) return
      git.setLogFilter(git.logRef, trimmed)
      void git.loadLog(true).catch(notices.report)
    }, 350)
    return () => window.clearTimeout(timer)
  }, [text])

  async function run(action: () => Promise<unknown>): Promise<void> {
    try { await action() } catch (error) { notices.report(error) }
  }

  function pickRef(ref: string): void {
    git.setLogFilter(ref, git.logGrep)
    void run(() => git.loadLog(true))
  }

  function copyHash(hash: string): void {
    void navigator.clipboard.writeText(hash)
      .then(() => notices.notify('已复制提交哈希', hash.slice(0, 12), 'success'))
      .catch(notices.report)
  }

  const branches = git.branches
  const needle = refQuery.trim().toLowerCase()
  const matches = (value: string) => needle.length === 0 || value.toLowerCase().includes(needle)
  const local = (branches?.local ?? []).filter(branch => matches(branch.name))
  const remote = (branches?.remote ?? []).filter(branch => matches(branch.name))
  const tags = (branches?.tags ?? []).filter(tag => matches(tag.name))
  const selected = git.show?.commit.hash ?? null

  return <>
    <div className={`git-log${selected || git.diff ? ' has-detail' : ''}`}>
      <aside className="git-log-side">
        <div className="git-log-side-search">
          <Input
            aria-label="筛选分支或标签"
            onValueChange={setRefQuery}
            placeholder="分支或标签"
            size="sm"
            startAdornment={<Search size={12} />}
            value={refQuery}
          />
        </div>
        <div className="git-ref-list">
          <button className={`git-ref-item${git.logRef.length === 0 ? ' is-active' : ''}`} onClick={() => pickRef('')} type="button">
            <GitBranch aria-hidden size={12} />
            <span>所有分支</span>
          </button>
          {local.length ? <div className="git-ref-group">本地分支</div> : null}
          {local.map(branch => <button
            className={`git-ref-item${git.logRef === branch.name ? ' is-active' : ''}`}
            key={branch.name}
            onClick={() => pickRef(branch.name)}
            title={branch.subject}
            type="button"
          >
            <GitBranch aria-hidden size={12} />
            <span>{branch.name}</span>
            {branch.current ? <span className="git-ref-badge">HEAD</span> : null}
          </button>)}
          {remote.length ? <div className="git-ref-group">远程分支</div> : null}
          {remote.map(branch => <button
            className={`git-ref-item${git.logRef === branch.name ? ' is-active' : ''}`}
            key={branch.name}
            onClick={() => pickRef(branch.name)}
            title={branch.subject}
            type="button"
          >
            <Cloud aria-hidden size={12} />
            <span>{branch.name}</span>
          </button>)}
          {tags.length ? <div className="git-ref-group">标签</div> : null}
          {tags.map(tag => <button
            className={`git-ref-item${git.logRef === tag.name ? ' is-active' : ''}`}
            key={tag.name}
            onClick={() => pickRef(tag.name)}
            type="button"
          >
            <Tag aria-hidden size={12} />
            <span>{tag.name}</span>
          </button>)}
        </div>
      </aside>
      <div className="git-log-main" style={{ '--graph-width': `${graphWidth}px` } as CSSProperties}>
        <div className="git-log-toolbar">
          <Input
            aria-label="按文本或哈希搜索提交"
            onValueChange={setText}
            placeholder="文本或哈希"
            size="sm"
            startAdornment={<Search size={12} />}
            value={text}
          />
          <span className="git-log-scope">{git.logRef ? `范围：${git.logRef}` : '所有分支'}</span>
          <Tooltip content="刷新提交图">
            <Button icon={<RefreshCw size={12} />} iconLabel="刷新提交图" onClick={() => void run(() => git.loadLog(true))} size="icon-xs" variant="ghost" />
          </Tooltip>
        </div>
        <div className="git-log-columns">
          <span className="git-log-col-graph" />
          <span>描述</span>
          <span className="git-log-col-author">作者</span>
          <span className="git-log-col-date">日期</span>
        </div>
        <div className="git-log-rows">
          {git.logLoading && !git.commits.length ? <div className="git-pane-empty"><Spinner size="lg" /><p>正在读取提交图…</p></div> : null}
          {visible.map(row => <button
            className={`git-log-row${selected === row.commit.hash ? ' is-active' : ''}`}
            key={row.commit.hash}
            onClick={() => void run(async () => {
              git.closeDiff()
              await git.loadShow(row.commit.hash)
            })}
            title={`${row.commit.short} · ${row.commit.subject}`}
            type="button"
          >
            <GraphCell row={row} width={graphWidth} />
            <span className="git-log-subject">
              <span className="git-log-subject-text">{row.commit.subject}</span>
              {row.commit.refs.length ? <span className="git-log-refs"><RefChips refs={row.commit.refs} remotes={git.remotes.map(remote => remote.name)} /></span> : null}
            </span>
            <span className="git-log-author">{row.commit.author}</span>
            <span className="git-log-date">{formatGitDate(row.commit.date)}</span>
          </button>)}
          {!visible.length && !git.logLoading ? <p className="git-note">
            {text.trim() ? '没有匹配的提交（输入哈希前缀可在已加载的页面内定位）。' : '当前范围内还没有提交。'}
          </p> : null}
          {git.logMore ? <div className="git-log-more">
            <Button loading={git.logLoading} onClick={() => void run(() => git.loadLog(false))} size="xs" variant="outline">加载更多</Button>
            <span>{git.commits.length} 个提交已加载</span>
          </div> : null}
        </div>
      </div>
      {selected || git.diff ? <aside className="git-log-detail">
        {git.diff || git.diffLoading ? <>
          <div className="git-show-head">
            <Button icon={<ArrowLeft size={12} />} onClick={() => git.closeDiff()} size="xs" variant="ghost">返回提交</Button>
          </div>
          <GitDiffView diff={git.diff} loading={git.diffLoading} />
        </> : git.showLoading ? <div className="git-pane-empty"><Spinner size="lg" /><p>正在读取提交…</p></div> : git.show ? <>
          <div className="git-show-head">
            <span className="git-hash mono">{git.show.commit.hash.slice(0, 10)}</span>
            <span className="git-log-meta">{git.show.commit.author} · {formatGitDate(git.show.commit.date)}</span>
            <span className="git-scm-spacer" />
            <Tooltip content="复制完整哈希"><Button icon={<ClipboardCopy size={12} />} iconLabel="复制哈希" onClick={() => copyHash(git.show!.commit.hash)} size="icon-xs" variant="ghost" /></Tooltip>
            <Tooltip content="关闭详情"><Button icon={<X size={12} />} iconLabel="关闭详情" onClick={() => { git.show = null; git.closeDiff() }} size="icon-xs" variant="ghost" /></Tooltip>
          </div>
          <h3 className="git-show-subject">{git.show.commit.subject}</h3>
          <div className="git-show-actions">
            <Badge color="gray" size="sm">{git.show.commit.author}</Badge>
            <Button onClick={() => setConfirm({
              title: `软回退到 ${git.show!.commit.short}？`,
              description: 'HEAD 会移回该提交，之后的更改保留在工作区（尚未暂存）。不会丢失文件内容。',
              confirmLabel: '软回退',
              action: async () => { await git.resetSoft(git.show!.commit.hash); await git.loadLog(true) },
            })} size="xs" variant="outline">软回退到此提交</Button>
          </div>
          {git.show.body ? <pre className="git-show-body">{git.show.body}</pre> : null}
          <div className="git-show-files">
            {git.show.files.map(file => <button
              className="git-file-main"
              key={`${file.path}:${file.originalPath ?? ''}`}
              onClick={() => void run(() => git.openDiff({ path: file.path, staged: false, commit: git.show!.commit.hash }))}
              title={file.originalPath ? `${file.originalPath} → ${file.path}` : file.path}
              type="button"
            >
              <span className="git-letter info">{file.status}</span>
              <FileTypeIcon name={file.path} size={14} />
              <span className="git-file-name">
                {file.path}
                {file.originalPath ? <span className="git-file-origin"> ← {file.originalPath}</span> : null}
              </span>
            </button>)}
            {!git.show.files.length ? <p className="git-note">这次提交没有文件变化（可能是合并或空提交）。</p> : null}
          </div>
        </> : null}
      </aside> : null}
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

/** One row of the lane drawing: pass-through lines, incoming stubs, parent curves and the dot. */
function GraphCell({ row, width }: { row: GraphRow; width: number }) {
  const laneWidth = 14
  const height = 30
  const mid = height / 2
  const spot = (lane: number) => 7 + lane * laneWidth
  return <svg aria-hidden className="git-graph-cell" height={height} width={width}>
    {row.through.map(item => <line key={`t${item.lane}`} stroke={graphColor(item.color)} strokeWidth={1.6} x1={spot(item.lane)} x2={spot(item.lane)} y1={0} y2={height} />)}
    {row.incoming.map(item => <line key={`i${item.lane}`} stroke={graphColor(item.color)} strokeWidth={1.6} x1={spot(item.lane)} x2={spot(item.lane)} y1={0} y2={mid} />)}
    {row.straight.map(item => <line key={`s${item.lane}`} stroke={graphColor(item.color)} strokeWidth={1.6} x1={spot(item.lane)} x2={spot(item.lane)} y1={0} y2={height} />)}
    {row.curves.map((item, index) => <path
      d={`M ${spot(row.lane)} ${mid} C ${spot(row.lane)} ${mid + 9}, ${spot(item.lane)} ${height - 9}, ${spot(item.lane)} ${height}`}
      fill="none"
      key={`c${index}`}
      stroke={graphColor(item.color)}
      strokeWidth={1.6}
    />)}
    {row.converge.map((item, index) => <path
      d={`M ${spot(item.from)} ${item.from === row.lane ? mid : 0} C ${spot(item.from)} ${mid - 4}, ${spot(item.to)} ${mid + 9}, ${spot(item.to)} ${height}`}
      fill="none"
      key={`v${index}`}
      stroke={graphColor(item.color)}
      strokeWidth={1.6}
    />)}
    <circle cx={spot(row.lane)} cy={mid} fill={graphColor(row.color)} r={3.4} stroke="var(--surface)" strokeWidth={1.4} />
  </svg>
}

/** Branch / remote / tag chips in front of a commit subject, coloured by kind. A ref counts as
 *  remote only when it starts with a configured remote name, so `feature/panels` stays local.
 *  Stash and symbolic `<remote>/HEAD` decorations are noise in the graph and stay hidden. */
function RefChips({ refs, remotes }: { refs: string[]; remotes: string[] }) {
  const visible = refs.filter(ref => !ref.includes('refs/stash') && !ref.endsWith('/HEAD'))
  return <>{visible.map(ref => {
    const kind = ref.startsWith('HEAD')
      ? 'head'
      : ref.startsWith('tag: ')
        ? 'tag'
        : remotes.some(name => ref.startsWith(name + '/')) ? 'remote' : 'local'
    const label = ref.startsWith('tag: ') ? ref.slice(5) : ref.replace(/^HEAD\s*(?:->|→)\s*/, '')
    return <span className={`git-ref-chip ${kind}`} key={ref}>
      {kind === 'tag' ? <Tag size={9} /> : kind === 'remote' ? <Cloud size={9} /> : <GitBranch size={9} />}
      {label}
    </span>
  })}</>
}
