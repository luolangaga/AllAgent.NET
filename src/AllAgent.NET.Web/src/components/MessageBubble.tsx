import { memo, useState } from 'react'
import { AlertTriangle, Check, Command, Copy, Info, Loader2, X } from 'lucide-react'
import { Avatar, AvatarFallback } from './ui/avatar'
import { Badge } from './ui/badge'
import { Button } from './ui/b-button'
import { Alert } from './ui/alert'
import { ThinkingIndicator } from './ui/thinking-indicator'
import { ReasoningBlock } from './ReasoningBlock'
import { Markdown } from './Markdown'
import { ToolTrace } from './ToolTrace'
import { dataUrl } from '@/lib/attachments'
import type { Message, ToolCall, ReasoningPart, NoticePart, ImageAttachment } from '@/types'
import { formatClock } from '@/lib/format'
import { languageTag, useT } from '@/i18n'

const MAX_CONTENT = 150000
const MAX_REASONING = 120000

/** One inline run note. Retries settle into「连接已恢复」instead of lingering. */
function NoticeLine({ part }: { part: NoticePart }) {
  const tr = useT()
  const retrying = part.kind === 'retry' && part.status === 'pending'
  const failed = part.status === 'failed'
  const recovered = part.kind === 'retry' && part.status === 'settled'
  const text = recovered
    ? tr('连接已恢复', 'Connection restored') + (part.attempts > 0 ? tr(`（自动重试 ${part.attempts} 次）`, ` (${part.attempts} automatic retries)`) : '')
    : part.text
  return <p
    aria-live={retrying ? 'polite' : undefined}
    className={`message-notice is-${part.status} ${part.kind}`}
    role={retrying ? 'status' : undefined}
  >
    <span aria-hidden className="message-notice-icon">
      {retrying ? <Loader2 className="notice-spin" size={12} /> : failed ? <AlertTriangle size={12} /> : recovered ? <Check size={12} /> : <Info size={12} />}
    </span>
    <span>{text}</span>
  </p>
}

// The streaming store replaces the active message on every event. Completed
// bubbles keep their references and skip Markdown parsing on subsequent tokens.
export const MessageBubble = memo(function MessageBubble({ message, awaiting = false }: { message: Message; awaiting?: boolean }) {
  const tr = useT()
  const [copied, setCopied] = useState(false)
  const [preview, setPreview] = useState<ImageAttachment | null>(null)
  const isUser = message.role === 'user'
  const images = message.images ?? []
  const streaming = message.status === 'streaming'
  const regular = message.toolCalls?.filter(call => call.name !== 'memory_update') ?? []
  const memories = message.toolCalls?.filter(call => call.name === 'memory_update') ?? []
  const fullReasoning = message.reasoningContent ?? ''
  const reasoning = fullReasoning.length > MAX_REASONING ? fullReasoning.slice(0, MAX_REASONING) : fullReasoning
  const content = message.content.length > MAX_CONTENT ? message.content.slice(0, MAX_CONTENT) : message.content
  const hasReasoning = reasoning.trim().length > 0
  // Reasoner output streams into the panel while the answer is generated, so it opens
  // during generation and stays available afterwards.
  const retrying = message.parts?.some(part => part.type === 'notice' && part.status === 'pending') ?? false
  const showThinking = streaming && !awaiting && !retrying && !content && regular.length === 0 && message.status !== 'error'
  const hasReasoningParts = message.parts?.some(part => part.type === 'reasoning') ?? false
  const blocks: Array<{ type: 'text'; text: string } | { type: 'tools'; calls: ToolCall[] } | ReasoningPart | NoticePart> = []
  const callMap = new Map(regular.map(call => [call.id, call]))
  let remaining = MAX_CONTENT
  for (const part of message.parts ?? []) {
    if (part.type === 'text') {
      if (remaining > 0) blocks.push({ type: 'text', text: part.text.slice(0, remaining) })
      remaining = Math.max(0, remaining - part.text.length)
    }
    else if (part.type === 'tool') {
      const call = callMap.get(part.id)
      if (!call) continue
      const last = blocks.at(-1)
      if (last?.type === 'tools') last.calls.push(call)
      else blocks.push({ type: 'tools', calls: [call] })
    }
    else blocks.push(part)
  }
  const running = regular.slice().reverse().find(call => call.status === 'running')

  function copy() {
    navigator.clipboard.writeText(message.content)
      .then(() => { setCopied(true); window.setTimeout(() => setCopied(false), 1600) })
      .catch(() => {})
  }

  return <article className={`message ${isUser ? 'user' : 'assistant'}`}>
    {!isUser ? <div aria-hidden className="message-mark"><Command size={13} /></div> : null}
    <div className="message-column">
      <header className="message-identity">
        <strong>{isUser ? tr('你', 'You') : 'AllAgent.NET'}</strong>
        <span>{streaming ? awaiting ? tr('等待你的回复', 'Waiting for your reply') : running ? tr(`正在执行 ${running.name}`, `Running ${running.name}`) : content ? tr('正在输出…', 'Writing…') : hasReasoning ? tr('正在思考…', 'Thinking…') : tr('正在连接模型…', 'Connecting to the model…') : formatClock(message.createdAt)}</span>
        {message.iteration ? <Badge color="blue" size="sm" variant="dot">{tr(`第 ${message.iteration.current}${message.iteration.maximum > 0 ? `/${message.iteration.maximum}` : ''} 步`, `Step ${message.iteration.current}${message.iteration.maximum > 0 ? `/${message.iteration.maximum}` : ''}`)}</Badge> : null}
        {message.status === 'error' ? <Badge color="red" size="sm">{tr('执行失败', 'Execution failed')}</Badge> : null}
        {message.finishReason === 'cancelled' ? <Badge size="sm">{tr('已停止', 'Stopped')}</Badge> : null}
        {message.finishReason === 'tool_limit' || message.finishReason === 'context_limit' ? <Badge color="amber" size="sm">{tr('已达执行上限', 'Execution limit reached')}</Badge> : null}
      </header>
      <div className="message-surface">
        {images.length ? <div className="message-images">
          {images.map((image, index) => <button
            aria-label={tr(`查看 ${image.name}`, `View ${image.name}`)}
            className="message-image"
            key={`${image.name}-${index}`}
            onClick={() => setPreview(image)}
            type="button"
          >
            {/* Inline data URLs are already in memory: lazy loading would only risk
                never firing for a box that has no intrinsic size yet. */}
            <img alt={image.name} src={dataUrl(image)} />
          </button>)}
        </div> : null}
        {hasReasoning && !hasReasoningParts ? <ReasoningBlock part={{ type: 'reasoning', text: reasoning,
          iteration: message.iteration?.current ?? 1, status: streaming && !content && !regular.length ? 'streaming' : 'done' }} /> : null}
        {message.parts?.length ? blocks.map((block, index) => block.type === 'text'
          ? <Markdown key={`text-${index}`} className="message-content" content={block.text.slice(0, MAX_CONTENT)} />
          : block.type === 'tools' ? <ToolTrace key={`tools-${index}`} calls={block.calls} />
          : block.type === 'notice' ? <NoticeLine key={block.id} part={block} />
          : <ReasoningBlock key={`reasoning-${index}`} part={block} />)
          : <>
            {content ? <Markdown className="message-content" content={content} /> : null}
            {regular.length > 0 ? <ToolTrace calls={regular} /> : null}
          </>}
        {message.content.length > MAX_CONTENT ? <p className="message-truncated">{tr(`消息过长，已渲染前 ${MAX_CONTENT.toLocaleString(languageTag())} 个字符；完整内容请用「导出」查看。`, `The message is long; the first ${MAX_CONTENT.toLocaleString(languageTag())} characters are rendered. Use “Export” for the full content.`)}</p> : null}
        {memories.length > 0 ? <ToolTrace calls={memories} /> : null}
        {message.error ? <Alert appearance="destructive" dismissible={false} message={message.error} timeout={0} title={tr('执行中断', 'Execution interrupted')} /> : null}
        {!content && !message.toolCalls?.length && !hasReasoning && showThinking
          ? <div className="message-loading"><ThinkingIndicator words={[tr('正在思考', 'Thinking'), tr('拆解任务', 'Breaking down the task'), tr('准备工具', 'Preparing tools')]} /></div>
          : null}
        {awaiting && streaming ? <p className="reasoning-wait">{tr('已暂停 · 等待你的回答或授权', 'Paused · waiting for your answer or approval')}</p> : null}
        {content && !streaming ? <div className="message-footer">
          <Button
            className="message-copy"
            icon={copied ? <Check size={13} /> : <Copy size={13} />}
            onClick={copy}
            size="sm"
            variant="ghost"
          >{copied ? tr('已复制', 'Copied') : tr('复制', 'Copy')}</Button>
        </div> : null}
      </div>
    </div>
    {isUser ? <Avatar name={tr('你', 'You')} size="sm"><AvatarFallback /></Avatar> : null}
    {preview ? <div className="image-lightbox" onClick={() => setPreview(null)} role="presentation">
      <button aria-label={tr('关闭预览', 'Close preview')} className="image-lightbox-close" onClick={() => setPreview(null)} type="button"><X size={16} /></button>
      {/* Clicking the picture itself must not close the overlay. */}
      <img alt={preview.name} onClick={event => event.stopPropagation()} src={dataUrl(preview)} />
    </div> : null}
  </article>
})
