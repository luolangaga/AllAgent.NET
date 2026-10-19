import type { ToolCall } from '@/types'
import { parseArguments, parseObject } from '@/utils/execution'

// Rich, render-ready descriptions of a tool call. Everything here is pure so the
// collapsed row and the expanded body can share one analysis and tests can assert
// on it without touching the DOM.

export type DiffLineKind = 'context' | 'added' | 'removed'

export interface DiffLine {
  kind: DiffLineKind
  oldLine?: number | null
  newLine?: number | null
  text: string
}

export interface ToolDiff {
  added: number
  removed: number
  lines: DiffLine[]
  truncated: boolean
  /** True when the diff was derived from the call arguments instead of the result. */
  preview: boolean
}

export interface SearchMatch {
  path: string
  line: number
  text: string
  context: { line: number; text: string; match: boolean }[]
}

export interface DirectoryEntry {
  name: string
  directory: boolean
  link: boolean
}

export interface TodoRow {
  id?: string
  content: string
  status: string
}

export type BadgeTone = 'add' | 'del' | 'ok' | 'error' | 'muted'

export interface CardBadge {
  text: string
  tone: BadgeTone
}

export type ToolCardBody =
  | { kind: 'diff'; path?: string; diff: ToolDiff; note?: string }
  | { kind: 'search'; pattern?: string; matches: SearchMatch[]; more: boolean; note?: string }
  | { kind: 'paths'; paths: string[]; more: boolean; note?: string }
  | { kind: 'entries'; path?: string; entries: DirectoryEntry[]; more: boolean; note?: string }
  | { kind: 'command'; command: string; language: string; exitCode?: number; stdout?: string; stderr?: string; note?: string; extra: { label: string; text: string; error?: boolean }[] }
  | { kind: 'read'; path?: string; content: string; language: string; range?: string; note?: string }
  | { kind: 'code'; language: string; code: string; stdout?: string; stderr?: string; exitCode?: number }
  | { kind: 'todos'; todos: TodoRow[] }
  | { kind: 'answer'; question?: string; answer?: string }
  | { kind: 'results'; rows: SearchResultRow[] }
  | { kind: 'text'; text: string }
  | { kind: 'json'; args?: string; result?: string; error?: string }

export interface SearchResultRow {
  title: string
  url?: string
  snippet?: string
}

export interface ToolCard {
  label: string
  /** The one thing the card is about: a path, a query, an executable… */
  target?: string
  badges: CardBadge[]
  body: ToolCardBody
}

const COMMAND_TOOLS = new Set(['run_shell', 'powershell_invoke', 'powershell_script', 'execute_code', 'powershell_store'])

export function isCommandTool(name: string): boolean {
  return COMMAND_TOOLS.has(name)
}

function textOf(value: unknown): string {
  if (value == null) return ''
  if (typeof value === 'string') return value
  try { return JSON.stringify(value, null, 2) } catch { return String(value) }
}

function number(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

function bool(value: unknown): boolean {
  return value === true
}

function mapArray<T>(value: unknown, map: (item: Record<string, unknown>) => T | null): T[] {
  if (!Array.isArray(value)) return []
  const rows: T[] = []
  for (const item of value) {
    if (!item || typeof item !== 'object') continue
    const row = map(item as Record<string, unknown>)
    if (row) rows.push(row)
  }
  return rows
}

export function lineCount(value: string): number {
  if (!value) return 0
  return value.replace(/\r\n/g, '\n').split('\n').length
}

/** Lowercased language hint for the code block, from a workspace path. */
export function languageOf(path?: string): string {
  const extension = (path ?? '').split('.').pop()?.toLowerCase() ?? ''
  switch (extension) {
    case 'ts': case 'tsx': return 'typescript'
    case 'js': case 'jsx': case 'mjs': case 'cjs': return 'javascript'
    case 'cs': return 'csharp'
    case 'py': return 'python'
    case 'ps1': case 'psm1': return 'powershell'
    case 'json': return 'json'
    case 'md': case 'mdx': return 'markdown'
    case 'css': return 'css'
    case 'html': case 'htm': return 'html'
    case 'sh': return 'bash'
    case 'xml': case 'csproj': case 'props': case 'targets': return 'xml'
    case 'yml': case 'yaml': return 'yaml'
    case 'sql': return 'sql'
    default: return 'text'
  }
}

/** Splits text into lines without inventing a trailing empty line. */
function splitLines(value: string): string[] {
  if (!value) return []
  const normalized = value.replace(/\r\n/g, '\n')
  const lines = normalized.split('\n')
  if (lines.length > 0 && lines[lines.length - 1] === '') lines.pop()
  return lines
}

const MAX_DIFF_LINES = 240

/**
 * Diff two strings. Small regions get a real LCS so added/removed pairs line up;
 * a huge region degrades to a plain replacement block instead of eating CPU.
 */
export function diffTexts(before: string, after: string, preview = false): ToolDiff {
  const oldLines = splitLines(before)
  const newLines = splitLines(after)
  let prefix = 0
  while (prefix < oldLines.length && prefix < newLines.length && oldLines[prefix] === newLines[prefix]) prefix++
  let suffix = 0
  while (suffix < oldLines.length - prefix && suffix < newLines.length - prefix &&
    oldLines[oldLines.length - 1 - suffix] === newLines[newLines.length - 1 - suffix]) suffix++
  const oldCount = oldLines.length - prefix - suffix
  const newCount = newLines.length - prefix - suffix
  const lines: DiffLine[] = []
  let added = 0
  let removed = 0
  let truncated = false
  const push = (line: DiffLine) => {
    if (lines.length >= MAX_DIFF_LINES) { truncated = true; return }
    lines.push(line)
  }
  for (let index = Math.max(0, prefix - 2); index < prefix; index++)
    push({ kind: 'context', oldLine: index + 1, newLine: index + 1, text: oldLines[index] })

  if ((oldCount + 1) * (newCount + 1) > 250_000) {
    removed = oldCount
    added = newCount
    for (let index = prefix; index < oldLines.length - suffix; index++)
      push({ kind: 'removed', oldLine: index + 1, newLine: null, text: oldLines[index] })
    for (let index = prefix; index < newLines.length - suffix; index++)
      push({ kind: 'added', oldLine: null, newLine: index + 1, text: newLines[index] })
  } else {
    const common: number[][] = Array.from({ length: oldCount + 1 }, () => new Array<number>(newCount + 1).fill(0))
    for (let i = oldCount - 1; i >= 0; i--)
      for (let j = newCount - 1; j >= 0; j--)
        common[i][j] = oldLines[prefix + i] === newLines[prefix + j]
          ? common[i + 1][j + 1] + 1
          : Math.max(common[i + 1][j], common[i][j + 1])
    let x = 0
    let y = 0
    while (x < oldCount || y < newCount) {
      if (x < oldCount && y < newCount && oldLines[prefix + x] === newLines[prefix + y]) {
        push({ kind: 'context', oldLine: prefix + x + 1, newLine: prefix + y + 1, text: oldLines[prefix + x] })
        x++
        y++
      } else if (x < oldCount && (y === newCount || common[x + 1][y] >= common[x][y + 1])) {
        push({ kind: 'removed', oldLine: prefix + x + 1, newLine: null, text: oldLines[prefix + x] })
        removed++
        x++
      } else {
        push({ kind: 'added', oldLine: null, newLine: prefix + y + 1, text: newLines[prefix + y] })
        added++
        y++
      }
    }
  }
  for (let index = 0; index < Math.min(2, suffix); index++)
    push({ kind: 'context', oldLine: oldLines.length - suffix + index + 1, newLine: newLines.length - suffix + index + 1,
      text: newLines[newLines.length - suffix + index] })
  return { added, removed, lines, truncated, preview }
}

function editsOf(args: Record<string, unknown>): Array<{ old_string: string; new_string: string }> {
  if (!Array.isArray(args.edits)) return []
  const rows: Array<{ old_string: string; new_string: string }> = []
  for (const item of args.edits) {
    if (!item || typeof item !== 'object') continue
    const edit = item as Record<string, unknown>
    if (typeof edit.old_string !== 'string' || typeof edit.new_string !== 'string') continue
    rows.push({ old_string: edit.old_string, new_string: edit.new_string })
  }
  return rows
}

/** Result diff for file_write/file_edit, or a preview built from the edit arguments. */
export function diffOfCall(call: ToolCall, args: Record<string, unknown>): ToolDiff | null {
  const data = (parseObject(call.output).data ?? {}) as Record<string, unknown>
  const host = data.diff
  if (host && typeof host === 'object') {
    const value = host as Record<string, unknown>
    const rows = mapArray<DiffLine>(value.lines, line => ({
      kind: line.kind === 'added' ? 'added' : line.kind === 'removed' ? 'removed' : 'context',
      oldLine: typeof line.oldLine === 'number' ? line.oldLine : null,
      newLine: typeof line.newLine === 'number' ? line.newLine : null,
      text: typeof line.text === 'string' ? line.text : '',
    }))
    return {
      added: number(value.added) ?? rows.filter(row => row.kind === 'added').length,
      removed: number(value.removed) ?? rows.filter(row => row.kind === 'removed').length,
      lines: rows,
      truncated: bool(value.truncated),
      preview: false,
    }
  }
  const edits = editsOf(args)
  if (edits.length === 1) return diffTexts(edits[0].old_string, edits[0].new_string, true)
  if (edits.length > 1) {
    const parts = edits.map(edit => diffTexts(edit.old_string, edit.new_string, true))
    return {
      added: parts.reduce((total, part) => total + part.added, 0),
      removed: parts.reduce((total, part) => total + part.removed, 0),
      lines: parts.flatMap(part => part.lines),
      truncated: parts.some(part => part.truncated),
      preview: true,
    }
  }
  // A rejected file_write still shows what was about to be written.
  const content = typeof args.content === 'string' ? args.content : ''
  if (content) return diffTexts('', content, true)
  return null
}

function searchMatchesOf(data: Record<string, unknown>): SearchMatch[] {
  return mapArray<SearchMatch>(data.matches, row => ({
    path: typeof row.path === 'string' ? row.path : '',
    line: number(row.line) ?? 0,
    text: typeof row.text === 'string' ? row.text : '',
    context: Array.isArray(row.context)
      ? row.context.flatMap(item => {
        if (!item || typeof item !== 'object') return []
        const entry = item as Record<string, unknown>
        return [{ line: number(entry.line) ?? 0, text: typeof entry.text === 'string' ? entry.text : '', match: bool(entry.match) }]
      })
      : [],
  })).filter(row => row.path.length > 0)
}

function pathListOf(data: Record<string, unknown>): string[] {
  return Array.isArray(data.paths) ? data.paths.filter((row): row is string => typeof row === 'string') : []
}

function entriesOf(data: Record<string, unknown>): DirectoryEntry[] {
  return mapArray<DirectoryEntry>(data.entries, row => {
    const name = typeof row.name === 'string' ? row.name : ''
    if (!name) return null
    return { name, directory: bool(row.directory), link: bool(row.link) }
  })
}

function todosOf(value: unknown): TodoRow[] {
  return mapArray<TodoRow>(value, row => ({
    id: typeof row.id === 'string' ? row.id : undefined,
    content: typeof row.content === 'string' ? row.content : '',
    status: typeof row.status === 'string' ? row.status : 'pending',
  }))
}

function searchResultsOf(value: unknown): SearchResultRow[] {
  return mapArray<SearchResultRow>(value, (row) => {
    const title = typeof row.title === 'string' ? row.title : typeof row.name === 'string' ? row.name : ''
    const url = typeof row.url === 'string' ? row.url : typeof row.link === 'string' ? row.link : undefined
    const snippet = typeof row.snippet === 'string' ? row.snippet
      : typeof row.description === 'string' ? row.description
        : typeof row.text === 'string' ? row.text : undefined
    if (!title && !url) return null
    return { title: title || url || '', url, snippet }
  })
}

function answerOf(data: Record<string, unknown>): string {
  const answer = data.answer
  if (Array.isArray(answer)) return answer.filter(item => typeof item === 'string').join('、')
  return textOf(answer)
}

function exitBadge(call: ToolCall, exitCode: number | undefined, success: boolean | undefined): CardBadge {
  if (exitCode !== undefined) return { text: `exit ${exitCode}`, tone: exitCode === 0 ? 'ok' : 'error' }
  if (call.status === 'running') return { text: '运行中', tone: 'muted' }
  if (call.status === 'cancelled') return { text: '已停止', tone: 'muted' }
  if (success === false || call.status === 'error') return { text: '失败', tone: 'error' }
  return { text: '完成', tone: 'ok' }
}

function streamLines(outputLines?: string[]): string | undefined {
  if (!outputLines?.length) return undefined
  return outputLines.slice(-200).join('\n')
}

/** One-line, human summary used by the collapsed row. */
export function describeToolCard(call: ToolCall): ToolCard {
  const args = parseArguments(call.args)
  const output = parseObject(call.output)
  const data = (output.data && typeof output.data === 'object' ? output.data : {}) as Record<string, unknown>
  const error = typeof output.error === 'string' && output.error ? output.error : undefined
  const note = typeof data.notice === 'string' ? data.notice : undefined
  const path = typeof args.path === 'string' ? args.path : undefined

  switch (call.name) {
    case 'file_edit':
    case 'file_write': {
      const diff = diffOfCall(call, args) ?? undefined
      const editCount = editsOf(args).length
      const badges: CardBadge[] = []
      if (diff) {
        badges.push({ text: `+${diff.added}`, tone: 'add' }, { text: `−${diff.removed}`, tone: 'del' })
        if (diff.preview) badges.push({ text: '预览', tone: 'muted' })
      }
      if (call.name === 'file_edit' && editCount > 1) badges.push({ text: `${editCount} 处`, tone: 'muted' })
      if (!diff && error) badges.push({ text: error.split('\n')[0].slice(0, 60), tone: 'error' })
      return {
        label: call.name === 'file_write' ? '写入文件' : '编辑文件',
        target: path,
        badges,
        body: { kind: 'diff', path, diff: diff ?? { added: 0, removed: 0, lines: [], truncated: false, preview: true }, note: note ?? error },
      }
    }
    case 'file_read': {
      const total = number(data.totalLines)
      const shown = number(data.outputLines)
      const start = number(data.offset) ?? 1
      const content = typeof data.content === 'string' ? data.content : ''
      const badges: CardBadge[] = []
      if (total !== undefined) badges.push({ text: `${total} 行`, tone: 'muted' })
      else if (shown !== undefined) badges.push({ text: `${shown} 行`, tone: 'muted' })
      if (bool(data.image)) badges.push({ text: '图片', tone: 'muted' })
      if (typeof data.truncatedBy === 'string' && data.truncatedBy) badges.push({ text: '未读完', tone: 'muted' })
      const range = shown !== undefined && shown > 0
        ? `${start}–${start + shown - 1}${total ? ` / ${total}` : ''} 行`
        : total ? `共 ${total} 行` : undefined
      return {
        label: '读取文件',
        target: path,
        badges,
        body: { kind: 'read', path, content: content || streamLines(call.outputLines) || '', language: languageOf(path), range,
          note: note ?? error },
      }
    }
    case 'file_search': {
      const matches = searchMatchesOf(data)
      const pattern = typeof data.pattern === 'string' ? data.pattern : typeof args.pattern === 'string' ? args.pattern : undefined
      const scope = typeof data.path === 'string' ? data.path : typeof args.path === 'string' ? args.path : undefined
      const more = bool(data.outputTruncated) || bool(data.matchLimitReached) || bool(data.linesTruncated) || bool(data.contextTruncated)
      return {
        label: '搜索内容',
        target: pattern ? `${pattern}${scope && scope !== '.' ? ` · ${scope}` : ''}` : scope,
        badges: [
          { text: matches.length ? `${matches.length} 处匹配` : call.status === 'running' ? '搜索中' : '无匹配', tone: matches.length ? 'ok' : 'muted' },
          ...(more ? [{ text: '已截断', tone: 'muted' as BadgeTone }] : []),
        ],
        body: { kind: 'search', pattern, matches, more, note: note ?? error },
      }
    }
    case 'everything_search': {
      const paths = pathListOf(data)
      const query = typeof data.query === 'string' ? data.query : typeof args.query === 'string' ? args.query : undefined
      return {
        label: '搜索文件名',
        target: query,
        badges: [
          { text: paths.length ? `${paths.length} 个文件` : call.status === 'running' ? '搜索中' : '无结果', tone: paths.length ? 'ok' : 'muted' },
          ...(bool(data.hasMore) ? [{ text: '已截断', tone: 'muted' as BadgeTone }] : []),
        ],
        body: { kind: 'paths', paths, more: bool(data.hasMore), note: note ?? error },
      }
    }
    case 'list_directory': {
      const entries = entriesOf(data)
      return {
        label: '列出目录',
        target: path,
        badges: [{ text: `${entries.length} 项`, tone: entries.length ? 'ok' : 'muted' }],
        body: { kind: 'entries', path, entries, more: bool(data.entryLimitReached), note: note ?? error },
      }
    }
    case 'run_shell': {
      const exe = typeof args.exe === 'string' ? args.exe : ''
      const argv = Array.isArray(args.args) ? args.args.filter(item => typeof item === 'string') as string[] : []
      const command = [exe, ...argv].join(' ').trim() || 'run_shell'
      const exitCode = number(data.exitCode)
      const success = typeof data.success === 'boolean' ? bool(data.success) : undefined
      return {
        label: '运行命令',
        target: command,
        badges: [exitBadge(call, exitCode, success)],
        body: {
          kind: 'command', command, language: 'powershell', exitCode,
          stdout: typeof data.stdout === 'string' ? data.stdout : streamLines(call.outputLines),
          stderr: typeof data.stderr === 'string' ? data.stderr : undefined,
          note: note ?? error,
          extra: typeof data.stdoutPath === 'string' ? [{ label: '完整日志', text: data.stdoutPath }] : [],
        },
      }
    }
    case 'powershell_invoke': {
      const module = typeof args.module === 'string' ? args.module : ''
      const commandName = typeof args.command === 'string' ? args.command : ''
      const exitCode = number(data.exitCode)
      const records = data.records
      return {
        label: 'PowerShell 命令',
        target: [module, commandName].filter(Boolean).join(' · ') || commandName,
        badges: [exitBadge(call, exitCode, typeof data.success === 'boolean' ? bool(data.success) : undefined)],
        body: {
          kind: 'command', command: `${module} ${commandName}`.trim(), language: 'powershell', exitCode,
          stdout: records === undefined ? streamLines(call.outputLines) : textOf(records),
          stderr: typeof data.stderr === 'string' ? data.stderr : undefined,
          note: note ?? error, extra: [],
        },
      }
    }
    case 'powershell_script': {
      const script = typeof args.script === 'string' ? args.script : ''
      const exitCode = number(data.exitCode)
      const streams = (data.streams && typeof data.streams === 'object' ? data.streams : {}) as Record<string, unknown>
      const extra: { label: string; text: string; error?: boolean }[] = []
      if (streams.errors !== undefined) extra.push({ label: '错误流', text: textOf(streams.errors), error: true })
      if (streams.warnings !== undefined) extra.push({ label: '警告流', text: textOf(streams.warnings) })
      const rendered = data.records !== undefined ? textOf(data.records) : typeof data.text === 'string' ? data.text : undefined
      return {
        label: 'PowerShell 脚本',
        target: typeof args.session === 'string' && args.session !== 'main' ? `会话 ${args.session}` : undefined,
        badges: [exitBadge(call, exitCode, typeof data.success === 'boolean' ? bool(data.success) : undefined),
          ...(streamLines(call.outputLines) && rendered === undefined && call.status === 'running' ? [{ text: '输出中', tone: 'muted' as BadgeTone }] : [])],
        body: {
          kind: 'command', command: script, language: 'powershell', exitCode,
          stdout: rendered ?? streamLines(call.outputLines),
          stderr: typeof data.stderr === 'string' ? data.stderr : undefined,
          note: note ?? error, extra,
        },
      }
    }
    case 'execute_code': {
      const code = typeof args.code === 'string' ? args.code : ''
      const exitCode = number(data.exitCode) ?? number(data.exit_code)
      const stdout = typeof data.stdout === 'string' ? data.stdout : streamLines(call.outputLines)
      const stderr = typeof data.stderr === 'string' ? data.stderr : undefined
      const summary = (data.hostCallSummary && typeof data.hostCallSummary === 'object' ? data.hostCallSummary : {}) as Record<string, unknown>
      const hostCalls = number(summary.total)
      const hostFailures = number(summary.failed)
      const badges: CardBadge[] = [exitBadge(call, exitCode, typeof data.ok === 'boolean' ? bool(data.ok) : undefined)]
      if (hostCalls !== undefined && hostCalls > 0) badges.push({ text: `${hostCalls} 次宿主调用`, tone: 'muted' })
      if (hostFailures) badges.push({ text: `${hostFailures} 失败`, tone: 'error' })
      return {
        label: '执行 Python',
        target: undefined,
        badges,
        body: { kind: 'code', language: 'python', code, stdout, stderr, exitCode },
      }
    }
    case 'web_fetch': {
      const url = typeof data.url === 'string' ? data.url : typeof args.url === 'string' ? args.url : undefined
      const title = typeof data.title === 'string' ? data.title : undefined
      const content = typeof data.content === 'string' ? data.content : ''
      return {
        label: '抓取网页',
        target: title || url,
        badges: [{ text: content ? `${content.length} 字` : call.status === 'running' ? '抓取中' : '无内容', tone: content ? 'ok' : 'muted' }],
        body: { kind: 'read', path: url, content, language: 'text', range: typeof data.contentType === 'string' ? data.contentType : undefined,
          note: note ?? error },
      }
    }
    case 'web_search': {
      const query = typeof data.query === 'string' ? data.query : typeof args.query === 'string' ? args.query : undefined
      const rows = searchResultsOf(data.results)
      return {
        label: '网页搜索',
        target: query,
        badges: [{ text: rows.length ? `${rows.length} 条结果` : call.status === 'running' ? '搜索中' : '无结果', tone: rows.length ? 'ok' : 'muted' }],
        body: rows.length ? { kind: 'results', rows } : { kind: 'json', args: call.args, result: call.output, error },
      }
    }
    case 'todo_write':
    case 'todo_read': {
      const todos = todosOf(data.todos).length ? todosOf(data.todos) : todosOf(args.todos)
      const done = todos.filter(row => row.status === 'completed').length
      return {
        label: call.name === 'todo_write' ? '更新 TODO' : '查看 TODO',
        target: undefined,
        badges: [{ text: `${done}/${todos.length} 完成`, tone: todos.length ? (done === todos.length ? 'ok' : 'muted') : 'muted' }],
        body: { kind: 'todos', todos },
      }
    }
    case 'ask_user': {
      const question = typeof args.question === 'string' ? args.question : undefined
      const answer = answerOf(data)
      return {
        label: '询问你',
        target: question,
        badges: [{ text: answer ? '已回答' : call.status === 'running' ? '等待回答' : '无回答', tone: answer ? 'ok' : 'muted' }],
        body: { kind: 'answer', question, answer: answer || undefined },
      }
    }
    case 'memory_update':
      return { label: '学习本轮经验', badges: [], body: { kind: 'json', args: call.args, result: call.output, error } }
    default: {
      const target = firstString(args, ['path', 'url', 'query', 'pattern', 'command', 'name', 'module', 'question', 'id'])
      const badges: CardBadge[] = []
      if (error) badges.push({ text: error.split('\n')[0].slice(0, 60), tone: 'error' })
      else if (call.outputLines?.length) badges.push({ text: `${call.outputLines.length} 行输出`, tone: 'muted' })
      return { label: call.name, target, badges, body: { kind: 'json', args: call.args, result: call.output, error } }
    }
  }
}

function firstString(args: Record<string, unknown>, keys: string[]): string | undefined {
  for (const key of keys) {
    const value = args[key]
    if (typeof value === 'string' && value.trim()) return value
  }
  return undefined
}
