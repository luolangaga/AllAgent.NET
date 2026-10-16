import { useEffect, useRef, useState } from 'react'
import { ArrowUpRight, Code2, FileText, Compass, Command, Square, Copy, Check } from 'lucide-react'
import { marked } from 'marked'
import hljs from 'highlight.js/lib/common'
import DOMPurify from 'dompurify'
import { AIInput } from '../components/ui/ai-input'
import { Button } from '../components/ui/b-button'
import { Avatar } from '../components/ui/avatar'
import { Badge } from '../components/ui/badge'
import { CodeBlock } from '../components/ui/code-block'
import { Skeleton } from '../components/ui/skeleton'
import { Accordion, AccordionItem, AccordionTrigger, AccordionContent } from '../components/ui/b-accordion'
import { useStore } from '../lib/store'
import { useConversationsStore } from '../stores/conversations'
import { useApiConfigsStore } from '../stores/apiConfigs'
import { useSettingsStore } from '../stores/settings'
import { parseObject } from '../utils/execution'
import { TodoPanel } from '../components/TodoPanel'
import type { Message, ToolCall } from '../types'

marked.use({ gfm: true, breaks: true, renderer: { code(code, info) {
  const language = (info ?? '').split(' ')[0]
  const safeLanguage = /^[\w-]+$/.test(language) ? language : 'plaintext'
  const highlighted = hljs.highlight(code, { language: hljs.getLanguage(safeLanguage) ? safeLanguage : 'plaintext' }).value
  return `<pre><code class="hljs language-${safeLanguage}">${highlighted}</code></pre>`
} } })
function Markdown({ content }: { content: string }) { return <div className="markdown" dangerouslySetInnerHTML={{ __html: DOMPurify.sanitize(marked.parse(content) as string) }} /> }
export function Execution({ call }: { call: ToolCall }) {
  const args = parseObject(call.args)
  if (call.name === 'memory_update') {
    const output = parseObject(call.output)
    const data = output.data ?? {}
    const title = '学习本轮经验'
    const learned: string[] = Array.isArray(data.learned) ? data.learned.filter((item: unknown): item is string => typeof item === 'string') : []
    return <AccordionItem value={call.id}><AccordionTrigger><span className={`execution-dot ${call.status}`} /><span>{title}</span><span className="ml-auto text-xs text-muted-foreground">{call.status === 'running' ? '整理中' : call.status === 'error' ? '更新失败' : data.summary ?? '已完成'}</span></AccordionTrigger><AccordionContent><div className="execution-body">{call.status === 'running' ? <p>正在整理偏好、环境与经验…</p> : call.status === 'error' ? <p role="alert">{String(output.error ?? '更新失败，原文件保留')}</p> : learned.length > 0 ? <ul>{learned.map((line, index) => <li key={index}>{line}</li>)}</ul> : <p>{data.summary ?? '本轮无新增'}</p>}</div></AccordionContent></AccordionItem>
  }
  return <AccordionItem value={call.id}><AccordionTrigger><span className={`execution-dot ${call.status}`} /><span className="mono">{call.name}</span><span className="ml-auto text-xs text-muted-foreground">{call.status === 'running' ? '执行中' : call.status === 'error' ? '失败' : '已完成'}</span></AccordionTrigger><AccordionContent><div className="execution-body"><CodeBlock code={args.code ? String(args.code) : JSON.stringify(args, null, 2)} language={args.code ? 'python' : 'json'} filename={call.name} />{!!call.outputLines?.length && <pre>{call.outputLines.join('\n')}</pre>}{call.output && <pre>{call.output}</pre>}</div></AccordionContent></AccordionItem>
}
function Bubble({ message, report }: { message: Message; report: (error: unknown) => void }) {
  const [copied, setCopied] = useState(false)
  const regularCalls = message.toolCalls?.filter(call => call.name !== 'memory_update') ?? []
  const learningCalls = message.toolCalls?.filter(call => call.name === 'memory_update') ?? []
  return <article className={`message ${message.role}`}><div className="message-identity"><Avatar name={message.role === 'user' ? '你' : 'LanAgent'} size="sm" /><strong>{message.role === 'user' ? '你' : 'LanAgent'}</strong><span>{message.status === 'streaming' ? '正在处理…' : new Date(message.createdAt).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })}</span>{message.status === 'error' && <Badge>发生错误</Badge>}</div>
    {message.reasoningContent && <Accordion><AccordionItem value="thinking"><AccordionTrigger>思考过程</AccordionTrigger><AccordionContent><Markdown content={message.reasoningContent} /></AccordionContent></AccordionItem></Accordion>}
    {regularCalls.length > 0 && <Accordion className="execution-list">{regularCalls.map(call => <Execution key={call.id} call={call} />)}</Accordion>}
    {message.content && <Markdown content={message.content} />}
    {learningCalls.length > 0 && <Accordion className="execution-list">{learningCalls.map(call => <Execution key={call.id} call={call} />)}</Accordion>}
    {!message.content && !message.toolCalls?.length && message.status === 'streaming' && <div className="stream-loading"><Skeleton className="h-3 w-3/4" /><Skeleton className="mt-3 h-3 w-1/2" /></div>}
    {message.content && <Button variant="ghost" size="sm" className="copy-message" icon={copied ? <Check size={13} /> : <Copy size={13} />} onClick={() => { navigator.clipboard.writeText(message.content).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500) }).catch(report) }}>{copied ? '已复制' : '复制'}</Button>}
  </article>
}
const ideas = [
  { icon: Code2, title: '一起写代码', description: '从想法到可以运行的实现', prompt: '请帮我实现一个功能。先了解当前项目的结构，再和我确认具体需求。' },
  { icon: FileText, title: '梳理与总结', description: '让复杂的信息变得清晰', prompt: '我想整理一份文档，请帮我梳理结构和重点。' },
  { icon: Compass, title: '探索一个问题', description: '拆解问题，找到下一步', prompt: '请和我一起分析一个问题，先询问我需要解决的问题是什么。' }
]
export default function Chat({ ready, onConfigure, report }: { ready: boolean; onConfigure: () => void; report: (error: unknown) => void }) {
  const conv = useStore(useConversationsStore)
  const api = useStore(useApiConfigsStore)
  const settings = useStore(useSettingsStore)
  const [draft, setDraft] = useState('')
  const [starting, setStarting] = useState(false)
  const [selected, setSelected] = useState('')
  const bottom = useRef<HTMLDivElement>(null)
  const scroll = useRef<HTMLDivElement>(null)
  const follow = useRef(true)
  const configs = api.configs.filter(c => c.isEnabled)
  const config = configs.find(c => c.id === selected) ?? configs.find(c => c.providerId === settings.defaultProviderId) ?? configs[0]
  const busy = starting || !!conv.streamingMessageId
  const messages = conv.messages.filter(m => m.role !== 'system' && m.role !== 'tool')
  useEffect(() => { if (follow.current) bottom.current?.scrollIntoView({ behavior: 'instant' as ScrollBehavior }) }, [messages.length, messages.at(-1)?.content, messages.at(-1)?.reasoningContent, messages.at(-1)?.toolCalls?.length])
  useEffect(() => { follow.current = true }, [conv.activeId])
  async function send(text: string, meta: { settings: Record<string, string> }) {
    if (!config || busy) return
    setStarting(true); follow.current = true
    try { await conv.sendMessage(text, config.id, meta.settings.thinking === 'on', Number(meta.settings.budget ?? config.thinkingBudget)); }
    catch (e) { setDraft(text); report(e) } finally { setStarting(false) }
  }
  return <section className={`chat ${messages.length ? 'has-messages' : ''}`}>
    <div className="chat-scroll" ref={scroll} onScroll={() => { const el = scroll.current!; follow.current = el.scrollHeight - el.scrollTop - el.clientHeight < 100 }}>
      {conv.isLoading || !ready ? <div className="messages"><Skeleton className="h-5 w-1/3" /><Skeleton className="mt-5 h-24 w-full" /></div> : messages.length ? <div className="messages"><div className="conversation-heading"><h2>{conv.active?.title}</h2><Badge>私密对话</Badge></div>{conv.activeId && <TodoPanel key={conv.activeId} todos={conv.todos} disabled={busy} onChange={conv.setTodos} report={report} />}{messages.map(m => <Bubble key={m.id} message={m} report={report} />)}<div ref={bottom} /></div> : <div className="welcome"><div className="welcome-mark"><Command size={35} strokeWidth={1.4} /></div><span className="eyebrow">A LITTLE LESS FRICTION. A LOT MORE POSSIBILITY.</span><h1>想法在这里，<br /><span>开始变成现实。</span></h1><p>写代码、整理资料、探索问题。<br className="small-only" />和 LanAgent 一起，把下一步做出来。</p><div className="ideas">{ideas.map(idea => <Button key={idea.title} variant="ghost" className="idea" onClick={() => { setDraft(idea.prompt); document.querySelector<HTMLTextAreaElement>('.composer textarea')?.focus() }}><div className="idea-content"><idea.icon size={21} strokeWidth={1.5} /><strong>{idea.title}</strong><span>{idea.description}</span><ArrowUpRight size={14} className="idea-arrow" /></div></Button>)}</div></div>}
    </div>
    <div className="composer-area"><div className="composer-meta"><span><span className="status-dot connected" />{config ? `${config.displayName} · ${config.defaultModel}` : '先连接一个模型，开启对话'}</span>{busy && <Button variant="outline" size="sm" icon={<Square size={12} />} disabled={starting} onClick={() => void conv.cancelStream().catch(report)}>停止生成</Button>}</div>
      <div className="composer"><AIInput key={`${conv.activeId ?? 'new'}:${config?.id ?? 'none'}`} value={draft} onValueChange={setDraft} disabled={!ready || busy || !config} placeholder={config ? '描述你的想法，或交给我一个任务…' : '请先添加模型配置'} showMessages={false} agents={configs.map(c => ({ value: c.id, label: c.displayName }))} defaultAgent={config?.id} onAgentChange={setSelected} defaultSettings={{ thinking: config?.thinkingEnabled ? 'on' : 'off', budget: String(config?.thinkingBudget ?? 8000) }} settingGroups={[{ id: 'thinking', label: '思考模式', options: [{ value: 'off', label: '标准' }, { value: 'on', label: '深度思考' }] }, { id: 'budget', label: '思考预算', options: Array.from(new Set([512, 4000, 8000, 16000, 32000, config?.thinkingBudget ?? 8000])).map(n => ({ value: String(n), label: `${n} tokens` })) }]} menuItems={[{ value: 'configure', label: '管理模型配置', icon: Command }]} onMenuSelect={onConfigure} onSend={(text, meta) => void send(text, meta)} /></div>
      <div className="composer-hint">{!config ? <Button variant="link" size="sm" onClick={onConfigure}>添加模型配置 <ArrowUpRight size={12} /></Button> : <span>Enter 发送 · Shift + Enter 换行</span>}<span>重要内容请核实</span></div>
    </div>
  </section>
}





