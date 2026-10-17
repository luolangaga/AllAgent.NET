import { useEffect, useRef, useState } from 'react'
import { ArrowUpRight, Code2, Command, Compass, Download, FileText } from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Progress } from '@/components/ui/b-progress'
import { Skeleton } from '@/components/ui/skeleton'
import { MessageBubble } from '@/components/MessageBubble'
import { PromptComposer, type PromptMeta } from '@/components/PromptComposer'
import { TodoPanel } from '@/components/TodoPanel'
import { invoke } from '@/bridge'
import { useStore } from '@/lib/store'
import { formatTokens } from '@/lib/format'
import { useApiConfigsStore } from '@/stores/apiConfigs'
import { useApprovalsStore } from '@/stores/approvals'
import { useConversationsStore } from '@/stores/conversations'
import { useSettingsStore } from '@/stores/settings'
import { useNoticesStore } from '@/stores/ui'
import { useWorkspaceStore } from '@/stores/workspace'
import type { ReasoningOptions } from '@/types'

const ideas = [
  { icon: Code2, title: '一起写代码', description: '从想法到可以运行的实现', prompt: '请帮我实现一个功能。先了解当前项目的结构，再和我确认具体需求。' },
  { icon: FileText, title: '梳理与总结', description: '让复杂的信息变得清晰', prompt: '我想整理一份文档，请帮我梳理结构和重点。' },
  { icon: Compass, title: '探索一个问题', description: '拆解问题，找到下一步', prompt: '请和我一起分析一个问题，先询问我需要解决的问题是什么。' },
]

export default function Chat({ ready, onNavigate }: { ready: boolean; onNavigate: (page: string) => void }) {
  const conv = useStore(useConversationsStore)
  const approvals = useStore(useApprovalsStore)
  const api = useStore(useApiConfigsStore)
  const settings = useStore(useSettingsStore)
  const notices = useStore(useNoticesStore)
  const workspace = useStore(useWorkspaceStore)
  const [draft, setDraft] = useState('')
  const [selected, setSelected] = useState<string | null>(null)
  const [visibleCount, setVisibleCount] = useState(30)
  const [reasoning, setReasoning] = useState<ReasoningOptions | null>(null)
  const bottom = useRef<HTMLDivElement>(null)
  const scroll = useRef<HTMLDivElement>(null)
  const follow = useRef(true)

  const enabledConfigs = api.configs.filter(config => config.isEnabled)
  const config = enabledConfigs.find(item => item.id === selected)
    ?? enabledConfigs.find(item => item.id === settings.defaultProviderId)
    ?? enabledConfigs[0]
  const busy = conv.sending || conv.streamingMessageId !== null
  // A blocked run is waiting for the user, not thinking: the stream stays open while a
  // question or an approval request is pending.
  const awaiting = approvals.approvals.length > 0 || approvals.questions.length > 0
  const messages = conv.messages.filter(message => message.role !== 'system' && message.role !== 'tool')
  const usage = conv.contextUsage

  useEffect(() => {
    if (follow.current) bottom.current?.scrollIntoView({ behavior: 'instant' as ScrollBehavior })
  }, [messages.length, messages.at(-1)])

  useEffect(() => { follow.current = true }, [conv.activeId])
  useEffect(() => { setVisibleCount(30) }, [conv.activeId])
  // Real thinking levels come from the provider's reasoning profile, not a fixed on/off list.
  useEffect(() => {
    if (!config?.id) { setReasoning(null); return }
    let active = true
    void api.reasoningOptions(config.id)
      .then(options => { if (active) setReasoning(options) })
      .catch(() => { if (active) setReasoning(null) })
    return () => { active = false }
  }, [config?.id])
  // Long histories render the newest slice first; earlier messages mount on demand.
  const visibleMessages = messages.length > visibleCount ? messages.slice(messages.length - visibleCount) : messages

  async function send(text: string, meta: PromptMeta) {
    if (!config || busy) return
    follow.current = true
    try {
      await conv.sendMessage(text, config.id, meta.thinkingEnabled, meta.thinkingBudget, meta.model, meta.reasoningEffort)
    } catch (error) {
      setDraft(text)
      notices.report(error)
    }
  }

  async function exportConversation() {
    if (!conv.activeId) return
    try {
      const markdown = await invoke<string>('conversations.export', { id: conv.activeId })
      const blob = new Blob([markdown], { type: 'text/markdown;charset=utf-8' })
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = `${(conv.active?.title ?? 'conversation').replace(/[\\/:*?"<>|]/g, '_')}.md`
      anchor.click()
      URL.revokeObjectURL(url)
      notices.notify('会话已导出', anchor.download, 'success')
    } catch (error) {
      notices.report(error)
    }
  }

  return <section className={`chat ${messages.length ? 'has-messages' : ''}`}>
    {conv.activeId && conv.todos.length > 0 ? <TodoPanel
      disabled={busy}
      key={conv.activeId}
      onChange={conv.setTodos}
      report={notices.report}
      todos={conv.todos}
    /> : null}
    <div
      className="chat-scroll"
      onScroll={() => {
        const element = scroll.current
        if (element) follow.current = element.scrollHeight - element.scrollTop - element.clientHeight < 120
      }}
      ref={scroll}
    >
      {!ready || conv.isLoading
        ? <div className="messages"><Skeleton className="h-5 w-1/3" /><Skeleton className="mt-5 h-24 w-full" /></div>
        : messages.length
          ? <div className="messages">
            <div className="conversation-heading">
              <div>
                <span className="eyebrow">CONVERSATION</span>
                <h2>{conv.active?.title ?? '新对话'}</h2>
              </div>
              <div className="conversation-actions">
                {workspace.available ? <Button
                  disabled={busy}
                  icon={<Download size={14} />}
                  onClick={() => void exportConversation()}
                  size="sm"
                  variant="ghost"
                >导出</Button> : null}
                <Badge size="sm" variant="dot">本地工作区 · 私密</Badge>
              </div>
            </div>
            {visibleMessages.length < messages.length ? <div className="messages-earlier">
              <Button
                onClick={() => setVisibleCount(count => count + 30)}
                size="sm"
                variant="outline"
              >加载更早的 {Math.min(30, messages.length - visibleMessages.length)} 条消息</Button>
            </div> : null}
            {visibleMessages.map(message => <MessageBubble awaiting={awaiting && message.status === 'streaming'} key={message.id} message={message} />)}
            <div ref={bottom} />
          </div>
          : <div className="welcome">
            <div className="welcome-mark"><Command size={34} strokeWidth={1.4} /></div>
            <span className="eyebrow">A LITTLE LESS FRICTION. A LOT MORE POSSIBILITY.</span>
            <h1>想法在这里，<br /><span>开始变成现实。</span></h1>
            <p>写代码、整理资料、探索问题。<br className="small-only" />和 AllAgent.NET 一起，把下一步做出来。</p>
            <div className="ideas">{ideas.map(idea => <button
              className="idea"
              key={idea.title}
              onClick={() => {
                setDraft(idea.prompt)
                document.querySelector<HTMLTextAreaElement>('.composer-area textarea')?.focus()
              }}
              type="button"
            >
              <idea.icon size={18} strokeWidth={1.6} />
              <strong>{idea.title}</strong>
              <span>{idea.description}</span>
              <ArrowUpRight className="idea-arrow" size={14} />
            </button>)}</div>
          </div>}
    </div>
    <div className="composer-wrap">
      {usage && usage.capacityTokens > 0 ? <div className="context-usage">
        <span className="context-label">
          上下文 {usage.estimated ? '≈' : ''}{formatTokens(usage.usedTokens)} / {formatTokens(usage.capacityTokens)}
        </span>
        <Progress
          ariaLabel="上下文占用"
          max={usage.capacityTokens}
          size="sm"
          tone={usage.usedTokens / usage.capacityTokens > 0.85 ? 'destructive' : 'default'}
          value={usage.usedTokens}
        />
      </div> : null}
      {ready && !enabledConfigs.length ? <Alert
        action={<Button onClick={() => onNavigate('models')} size="sm" variant="outline">前往配置</Button>}
        appearance="info"
        dismissible={false}
        message="添加 API 地址与密钥后即可开始第一段对话。"
        timeout={0}
        title="还没有可用的模型配置"
      /> : null}
      <PromptComposer
        busy={busy}
        configs={enabledConfigs}
        draft={draft}
        models={config?.models ?? []}
        onConfigure={() => onNavigate('models')}
        onDraftChange={setDraft}
        onSelect={setSelected}
        onSend={(text, meta) => void send(text, meta)}
        onSettings={() => onNavigate('settings')}
        onStop={() => void conv.cancelStream().catch(notices.report)}
        reasoning={reasoning}
        selectedId={selected ?? config?.id ?? null}
      />
    </div>
  </section>
}
