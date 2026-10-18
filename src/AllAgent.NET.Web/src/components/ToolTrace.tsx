import {
  Code2, FilePen, FilePlus2, FileText, FolderTree, Globe, ListChecks, MessageCircleQuestion,
  Monitor, Plug, Search, Sparkles, SquareTerminal, Terminal, Users, Wand2, Wrench, type LucideIcon,
} from 'lucide-react'
import { Accordion, AccordionItem, AccordionTrigger, AccordionContent } from './ui/b-accordion'
import { Badge } from './ui/badge'
import { CodeBlock } from './ui/code-block'
import { Alert } from './ui/alert'
import { SubagentCard } from './SubagentCard'
import type { ToolCall, ToolCallStatus } from '@/types'
import { parseArguments, parseObject } from '@/utils/execution'

const statusLabels: Record<ToolCallStatus, string> = { running: '执行中', done: '已完成', error: '失败', cancelled: '已停止' }

const TOOL_ICONS: Record<string, LucideIcon> = {
  execute_code: Code2,
  file_read: FileText,
  file_write: FilePlus2,
  file_edit: FilePen,
  list_directory: FolderTree,
  run_shell: SquareTerminal,
  powershell_invoke: SquareTerminal,
  powershell_script: Terminal,
  powershell_store: Terminal,
  todo_write: ListChecks,
  todo_read: ListChecks,
  memory_update: Sparkles,
  ask_user: MessageCircleQuestion,
  everything_search: Search,
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

function resultView(result?: string): { language: string; code: string } {
  const parsed = parseObject(result)
  if (result && Object.keys(parsed).length > 0) return { language: 'json', code: JSON.stringify(parsed, null, 2) }
  return { language: 'text', code: result ?? '' }
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

const CODE_BUDGET = 20000
const LINE_BUDGET = 400

function clipCode(text: string): string {
  return text.length > CODE_BUDGET
    ? `${text.slice(0, CODE_BUDGET)}\n… 已截断 ${text.length - CODE_BUDGET} 个字符（完整内容可用「导出」查看）`
    : text
}

// Heavy JSON formatting and code preparation happen only after expansion.
function TraceBody({ call }: { call: ToolCall }) {
  const args = parseArguments(call.args)
  const script = call.name === 'powershell_script' ? args.script : args.code
  const source = clipCode(script ? String(script) : JSON.stringify(args, null, 2))
  const language = script ? call.name === 'powershell_script' ? 'powershell' : 'python' : 'json'
  const result = resultView(call.output)
  const lines = call.outputLines?.length ? clipCode(call.outputLines.slice(-LINE_BUDGET).join('\n')) : undefined
  const isMemory = call.name === 'memory_update'
  return <div className="tool-body">
    {isMemory ? <MemoryBody call={call} /> : <>
      <CodeBlock code={source} filename={call.name} language={language} maxHeight={280} />
      {lines ? <pre className="tool-lines">{lines}</pre> : null}
      {call.output ? <CodeBlock code={clipCode(result.code)} language={result.language} maxHeight={320} /> : null}
    </>}
    {call.note ? <Alert appearance="warning" dismissible={false} message={call.note} timeout={0} title={call.rejected ? '未执行' : '执行状态'} /> : null}
  </div>
}

function TraceItem({ call }: { call: ToolCall }) {
  const isMemory = call.name === 'memory_update'
  const Icon = toolIcon(call.name)
  return <AccordionItem value={call.id} className={call.parentId ? 'tool-child' : undefined}>
    <AccordionTrigger>
      <span className="tool-trigger">
        <Icon aria-hidden className="tool-icon" size={14} />
        <span className="tool-name">{isMemory ? '学习本轮经验' : call.name}</span>
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
