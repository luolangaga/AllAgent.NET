import { useMemo } from 'react'
import {
  Code2, FilePen, FilePlus2, FileText, Folder, FolderTree, Globe, ListChecks, Link2, MessageCircleQuestion,
  Monitor, Plug, Search, Sparkles, SquareTerminal, Terminal, Users, Wand2, Wrench, type LucideIcon,
} from 'lucide-react'
import { Accordion, AccordionItem, AccordionTrigger, AccordionContent } from './ui/b-accordion'
import { Badge } from './ui/badge'
import { CodeBlock } from './ui/code-block'
import { Alert } from './ui/alert'
import { DiffLines } from './DiffLines'
import { PluginToolCard } from './plugins/PluginToolCard'
import { SubagentCard } from './SubagentCard'
import { useStore } from '@/lib/store'
import { useContributionsStore } from '@/stores/contributions'
import type { ToolCall, ToolCallStatus } from '@/types'
import { parseObject } from '@/utils/execution'
import { describeToolCard, type CardBadge, type SearchMatch, type ToolCard, type ToolCardBody } from '@/lib/toolCard'

const statusLabels: Record<ToolCallStatus, string> = { running: '执行中', done: '已完成', error: '失败', cancelled: '已停止' }

const TOOL_ICONS: Record<string, LucideIcon> = {
  execute_code: Code2,
  file_read: FileText,
  file_write: FilePlus2,
  file_edit: FilePen,
  list_directory: FolderTree,
  file_search: Search,
  everything_search: Search,
  run_shell: SquareTerminal,
  powershell_invoke: SquareTerminal,
  powershell_script: Terminal,
  powershell_store: Terminal,
  todo_write: ListChecks,
  todo_read: ListChecks,
  memory_update: Sparkles,
  ask_user: MessageCircleQuestion,
  subagent: Users,
  cancel_agent: Users,
}

function toolIcon(name: string): LucideIcon {
  if (TOOL_ICONS[name]) return TOOL_ICONS[name]
  if (name.startsWith('mcp')) return Plug
  if (name.startsWith('browser')) return Globe
  if (name.startsWith('computer')) return Monitor
  if (name.startsWith('skill')) return Wand2
  if (name.startsWith('web')) return Globe
  return Wrench
}

function statusLabel(call: ToolCall): string {
  if (call.status === 'cancelled') return '已停止'
  if (call.name === 'memory_update') {
    if (call.status === 'running') return '整理中'
    if (call.status === 'error') return '更新失败'
    return String(parseObject(call.output).data?.summary ?? '已完成')
  }
  if (call.rejected) return call.rejectionKind === 'protocol' ? '调用格式错误 · 未执行' : '已拒绝'
  return statusLabels[call.status]
}

const CODE_BUDGET = 20000
const OUTPUT_BUDGET = 12000

function clip(text: string, budget = CODE_BUDGET): string {
  return text.length > budget ? `${text.slice(0, budget)}\n… 已截断 ${text.length - budget} 个字符（完整内容可用「导出」查看）` : text
}

function Badges({ badges }: { badges: CardBadge[] }) {
  return <>{badges.map((badge, index) => <span className={`tool-metric ${badge.tone}`} key={`${badge.text}-${index}`}>{badge.text}</span>)}</>
}

function Hint({ text }: { text?: string }) {
  return text ? <p className="tool-hint">{text}</p> : null
}

function DiffBody({ body }: { body: Extract<ToolCardBody, { kind: 'diff' }> }) {
  const { diff, path } = body
  return <div className="tool-body">
    <div className="tool-diff">
      <div className="tool-diff-head">
        <span className="tool-diff-path" title={path}>{path ?? '文件内容'}</span>
        <span className="tool-metric add">+{diff.added}</span>
        <span className="tool-metric del">−{diff.removed}</span>
        {diff.preview ? <span className="tool-diff-tag">未应用 · 预览</span> : null}
      </div>
      {diff.lines.length > 0
        ? <DiffLines lines={diff.lines} />
        : <p className="tool-empty">没有行内容变化。</p>}
      {diff.truncated ? <p className="tool-hint">差异过大，仅显示前 {diff.lines.length} 行。</p> : null}
    </div>
    <Hint text={body.note} />
  </div>
}

function groupMatches(matches: SearchMatch[]): Array<{ path: string; rows: SearchMatch[] }> {
  const groups = new Map<string, SearchMatch[]>()
  for (const match of matches) {
    const rows = groups.get(match.path)
    if (rows) rows.push(match)
    else groups.set(match.path, [match])
  }
  return [...groups].map(([path, rows]) => ({ path, rows }))
}

function SearchBody({ body }: { body: Extract<ToolCardBody, { kind: 'search' }> }) {
  const groups = groupMatches(body.matches)
  return <div className="tool-body">
    <div className="tool-search">
      {groups.map(group => <div className="tool-search-file" key={group.path}>
        <div className="tool-search-head">
          <FileText aria-hidden className="tool-icon" size={13} />
          <span title={group.path}>{group.path}</span>
          <span className="tool-search-count">{group.rows.length}</span>
        </div>
        {group.rows.map(row => <div className="tool-search-hit" key={`${row.path}:${row.line}`}>
          <span className="tool-search-line">{row.line}</span>
          <code>{row.text || ' '}</code>
        </div>)}
      </div>)}
      {groups.length === 0 ? <p className="tool-empty">{body.note ?? '没有匹配结果。'}</p> : null}
    </div>
    {groups.length > 0
      ? <><p className="tool-hint">{body.pattern ? `模式 ${body.pattern}` : '搜索'} · {body.matches.length} 处匹配 · {groups.length} 个文件</p>
        {body.more ? <p className="tool-hint">{body.note ?? '结果已截断，可继续翻页查看。'}</p> : null}</>
      : null}
  </div>
}

function PathsBody({ body }: { body: Extract<ToolCardBody, { kind: 'paths' }> }) {
  return <div className="tool-body">
    <div className="tool-paths">
      {body.paths.map(path => <div className="tool-path" key={path}><FileText aria-hidden className="tool-icon" size={12} /><code>{path}</code></div>)}
      {body.paths.length === 0 ? <p className="tool-empty">{body.note ?? '没有匹配的文件。'}</p> : null}
    </div>
    {body.more ? <p className="tool-hint">{body.note ?? '结果已截断，可继续翻页查看。'}</p> : null}
  </div>
}

function EntriesBody({ body }: { body: Extract<ToolCardBody, { kind: 'entries' }> }) {
  return <div className="tool-body">
    <div className="tool-entries">
      {body.entries.map(entry => <div className="tool-entry" key={entry.name}>
        {entry.directory ? <Folder aria-hidden className="tool-icon" size={13} /> : <FileText aria-hidden className="tool-icon" size={13} />}
        <span>{entry.name}</span>
        {entry.directory ? <span className="tool-entry-kind">目录</span> : null}
        {entry.link ? <Link2 aria-hidden className="tool-icon" size={12} /> : null}
      </div>)}
      {body.entries.length === 0 ? <p className="tool-empty">{body.note ?? '目录为空。'}</p> : null}
    </div>
    <Hint text={body.more ? body.note ?? '结果已截断，可继续翻页查看。' : undefined} />
  </div>
}

function OutputBlock({ label, text, error }: { label: string; text: string; error?: boolean }) {
  return <div className={`tool-output-block${error ? ' error' : ''}`}>
    <span className="tool-output-label">{label}</span>
    <pre className="tool-output">{clip(text, OUTPUT_BUDGET)}</pre>
  </div>
}

function CommandBody({ body, outputLines }: { body: Extract<ToolCardBody, { kind: 'command' }>; outputLines?: string[] }) {
  const live = !body.stdout && outputLines?.length ? outputLines.slice(-200).join('\n') : undefined
  const script = body.command.includes('\n')
  return <div className="tool-body">
    {script
      ? <CodeBlock code={clip(body.command)} language={body.language} maxHeight={240} />
      : <pre className="tool-command"><span aria-hidden className="tool-command-mark">$</span> {body.command || '(空命令)'}</pre>}
    {body.stdout ? <OutputBlock label="stdout" text={body.stdout} /> : live ? <OutputBlock label="实时输出" text={live} /> : null}
    {body.stderr ? <OutputBlock error label="stderr" text={body.stderr} /> : null}
    {body.extra.map(item => <OutputBlock error={item.error} key={item.label} label={item.label} text={item.text} />)}
    <Hint text={body.note} />
  </div>
}

function ReadBody({ body }: { body: Extract<ToolCardBody, { kind: 'read' }> }) {
  return <div className="tool-body">
    {body.content
      ? <CodeBlock code={clip(body.content)} filename={body.path} language={body.language} maxHeight={380} />
      : <p className="tool-empty">没有文本内容可显示（可能已附加图片）。</p>}
    <Hint text={body.range} />
    <Hint text={body.note} />
  </div>
}

function CodeBody({ body }: { body: Extract<ToolCardBody, { kind: 'code' }> }) {
  return <div className="tool-body">
    <CodeBlock code={clip(body.code)} language={body.language} maxHeight={280} />
    {body.stdout ? <OutputBlock label="stdout" text={body.stdout} /> : null}
    {body.stderr ? <OutputBlock error label="stderr" text={body.stderr} /> : null}
    {body.exitCode !== undefined && body.exitCode !== 0 ? <p className="tool-hint">退出码 {body.exitCode}。</p> : null}
  </div>
}

function TodosBody({ body }: { body: Extract<ToolCardBody, { kind: 'todos' }> }) {
  const done = body.todos.filter(todo => todo.status === 'completed').length
  return <div className="tool-body">
    {body.todos.length > 0
      ? <>
        <ul className="tool-todos">{body.todos.map((todo, index) => <li className={`tool-todo ${todo.status}`} key={todo.id ?? index}>
          <span aria-hidden className="tool-todo-mark">{todo.status === 'completed' ? '✓' : todo.status === 'in_progress' ? '◐' : '○'}</span>
          <span>{todo.content}</span>
        </li>)}</ul>
        <p className="tool-hint">{done}/{body.todos.length} 已完成。</p>
      </>
      : <p className="tool-empty">清单为空。</p>}
  </div>
}

function AnswerBody({ body }: { body: Extract<ToolCardBody, { kind: 'answer' }> }) {
  return <div className="tool-body">
    {body.question ? <p className="tool-question">{body.question}</p> : null}
    {body.answer ? <p className="tool-answer">{body.answer}</p> : <p className="tool-empty">还没有回答。</p>}
  </div>
}

function ResultsBody({ body }: { body: Extract<ToolCardBody, { kind: 'results' }> }) {
  return <div className="tool-body">
    <div className="tool-results">
      {body.rows.map((row, index) => <div className="tool-result" key={`${row.url ?? row.title}-${index}`}>
        <strong>{row.title}</strong>
        {row.url ? <span className="tool-result-url">{row.url}</span> : null}
        {row.snippet ? <span className="tool-result-snippet">{row.snippet}</span> : null}
      </div>)}
    </div>
  </div>
}

function JsonBody({ body }: { body: Extract<ToolCardBody, { kind: 'json' }> }) {
  const args = body.args && body.args !== '{}' ? body.args : undefined
  return <div className="tool-body">
    {args ? <CodeBlock code={clip(args)} language="json" maxHeight={240} /> : null}
    {body.result ? <CodeBlock code={clip(body.result)} language="json" maxHeight={320} /> : null}
    {body.error ? <p className="tool-hint">{body.error}</p> : null}
  </div>
}

function MemoryBody({ call }: { call: ToolCall }) {
  const output = parseObject(call.output)
  const data = (output.data ?? {}) as Record<string, unknown>
  const learned = Array.isArray(data.learned)
    ? (data.learned as unknown[]).filter((item): item is string => typeof item === 'string')
    : []
  if (call.status === 'running') return <p>正在整理偏好、环境与经验…</p>
  if (call.status === 'cancelled') return <p>整理已停止</p>
  if (call.status === 'error') return <p role="alert">{String(output.error ?? '更新失败，原文件保留')}</p>
  if (learned.length > 0) return <ul>{learned.map((line, index) => <li key={index}>{line}</li>)}</ul>
  return <p>{String(data.summary ?? '本轮无新增')}</p>
}

function CardBody({ card, call }: { card: ToolCard; call: ToolCall }) {
  switch (card.body.kind) {
    case 'diff': return <DiffBody body={card.body} />
    case 'search': return <SearchBody body={card.body} />
    case 'paths': return <PathsBody body={card.body} />
    case 'entries': return <EntriesBody body={card.body} />
    case 'command': return <CommandBody body={card.body} outputLines={call.outputLines} />
    case 'read': return <ReadBody body={card.body} />
    case 'code': return <CodeBody body={card.body} />
    case 'todos': return <TodosBody body={card.body} />
    case 'answer': return <AnswerBody body={card.body} />
    case 'results': return <ResultsBody body={card.body} />
    case 'json': return <JsonBody body={card.body} />
    default: return null
  }
}

// Heavy JSON parsing and diff building happen once per call state — never per token.
const CARD_DEPS = (call: ToolCall) => [call.id, call.name, call.status, call.args, call.output, call.outputLines] as const

function TraceBody({ call }: { call: ToolCall }) {
  const contributions = useStore(useContributionsStore)
  const card = useMemo(() => describeToolCard(call), CARD_DEPS(call))
  const custom = contributions.toolCardFor(call.name)
  if (call.name === 'memory_update') return <div className="tool-body"><MemoryBody call={call} /></div>
  if (custom) return <div className="tool-body-stack">
    <PluginToolCard
      assetBaseUrl={contributions.assetBaseUrl}
      call={call}
      entry={custom.entry}
      plugin={custom.plugin}
    />
    {call.note ? <Alert appearance="warning" dismissible={false} message={call.note} timeout={0} title={call.rejected ? '未执行' : '执行状态'} /> : null}
  </div>
  return <div className="tool-body-stack">
    <CardBody call={call} card={card} />
    {call.note ? <Alert appearance="warning" dismissible={false} message={call.note} timeout={0} title={call.rejected ? '未执行' : '执行状态'} /> : null}
  </div>
}

function TraceItem({ call }: { call: ToolCall }) {
  const Icon = toolIcon(call.name)
  const card = useMemo(() => describeToolCard(call), CARD_DEPS(call))
  return <AccordionItem value={call.id} className={call.parentId ? 'tool-child' : undefined}>
    <AccordionTrigger>
      <span aria-label={card.label} className="tool-trigger" title={card.label}>
        <Icon aria-hidden className="tool-icon" size={14} />
        <span className="tool-name">{call.name === 'memory_update' ? '学习本轮经验' : call.name}</span>
        {card.target ? <span className="tool-target" title={card.target}>{card.target}</span> : null}
        <Badges badges={card.badges} />
        {call.parentId ? <Badge size="sm" variant="dot">嵌套</Badge> : null}
        <span className={`tool-status ${call.rejected ? 'error' : call.status}`}>{statusLabel(call)}</span>
      </span>
    </AccordionTrigger>
    {/* Collapsed traces stay unmounted: huge histories must not tokenize megabytes of code up front. */}
    <AccordionContent keepMounted={false}>
      <TraceBody call={call} />
    </AccordionContent>
  </AccordionItem>
}

export function ToolTrace({ calls, defaultOpen }: { calls: ToolCall[]; defaultOpen?: string[] }) {
  if (!calls.length) return null
  return <Accordion className="tool-trace" defaultValue={defaultOpen} multiple>
    {calls.map(call => call.name === 'subagent'
      ? <SubagentCard call={call} key={call.id} />
      : <TraceItem call={call} key={call.id} />)}
  </Accordion>
}
