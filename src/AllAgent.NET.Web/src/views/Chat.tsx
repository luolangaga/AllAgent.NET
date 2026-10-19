import { useEffect, useRef, useState } from 'react'
import { ArrowUpRight, Code2, Command, Compass, Download, FileText } from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Progress } from '@/components/ui/b-progress'
import { Skeleton } from '@/components/ui/skeleton'
import { MessageBubble } from '@/components/MessageBubble'
import { PromptComposer, type PromptMeta } from '@/components/PromptComposer'
import { SessionFooter } from '@/components/SessionFooter'
import { TodoPanel } from '@/components/TodoPanel'
import { invoke } from '@/bridge'
import { useStore } from '@/lib/store'
import { formatTokens } from '@/lib/format'
import { matchCommands, runCommand } from '@/lib/commandRouter'
import { useApiConfigsStore } from '@/stores/apiConfigs'
import { useApprovalsStore } from '@/stores/approvals'
import { useCommandsStore } from '@/stores/commands'
import { useConversationsStore } from '@/stores/conversations'
import { useModelSelectionStore } from '@/stores/modelSelection'
import { useSettingsStore } from '@/stores/settings'
import { useNoticesStore } from '@/stores/ui'
import { useWorkspaceStore } from '@/stores/workspace'
import type { ImageAttachment, ReasoningOptions } from '@/types'

const ideas = [
  { icon: Code2, title: '一起写代码', description: '从想法到可以运行的实现', prompt: '请帮我实现一个功能。先了解当前项目的结构，再和我确认具体需求。' },
  { icon: FileText, title: '梳理与总结', description: '让复杂的信息变得清晰', prompt: '我想整理一份文档，请帮我梳理结构和重点。' },
  { icon: Compass, title: '探索一个问题', description: '拆解问题，找到下一步', prompt: '请和我一起分析一个问题，先询问我需要解决的问题是什么。' },
]

export default function Chat({ ready, onNavigate, onPalette }: { ready: boolean; onNavigate: (page: string) => void; onPalette: () => void }) {
  const conv = useStore(useConversationsStore)
  const approvals = useStore(useApprovalsStore)
  const api = useStore(useApiConfigsStore)
  const settings = useStore(useSettingsStore)
  const notices = useStore(useNoticesStore)
  const workspace = useStore(useWorkspaceStore)
  const commands = useStore(useCommandsStore)
  const modelSelection = useStore(useModelSelectionStore)
  const [draft, setDraft] = useState('')
  const [attachments, setAttachments] = useState<ImageAttachment[]>([])
  const [visibleCount, setVisibleCount] = useState(30)
  const [reasoning, setReasoning] = useState<ReasoningOptions | null>(null)
  const scroll = useRef<HTMLDivElement>(null)
  const follow = useRef(true)

  const enabledConfigs = api.configs.filter(config => config.isEnabled)
  const config = enabledConfigs.find(item => item.id === modelSelection.configId)
    ?? enabledConfigs.find(item => item.id === settings.defaultProviderId)
    ?? enabledConfigs[0]
  const busy = conv.sending || conv.streamingMessageId !== null
  // A blocked run is waiting for the user, not thinking: the stream stays open while a
  // question or an approval request is pending.
  const awaiting = approvals.approvals.length > 0 || approvals.questions.length > 0
  const messages = conv.messages.filter(message => message.role !== 'system' && message.role !== 'tool')
  const usage = conv.contextUsage
  const stored = conv.conversationUsage
  // The live event only arrives during a turn; a reloaded page falls back to the persisted
  // counters with the selected configuration's capacity, like the terminal restores its meter.
  const capacity = config?.effectiveContextTokens || config?.contextWindowTokens || 0
  const meter = usage ?? (stored?.contextUsedTokens != null && capacity > 0
    ? { usedTokens: stored.contextUsedTokens, capacityTokens: capacity, estimated: stored.contextEstimated }
    : null)
  // Cache counters accumulate on the conversation record (DataStore.RecordTokenUsage), so the
  // hit rate survives reloads; a conversation without provider-reported cache usage shows none.
  const cache = stored && stored.cacheRequests > 0 && stored.cacheHitRate !== null
    ? { ...stored, hitRate: stored.cacheHitRate }
    : null

  // Pin to the newest message on the next frame: the composer can still be resizing (the
  // draft just cleared), and setting scrollTop directly never scrolls ancestor containers
  // the way a sentinel + scrollIntoView would.
  useEffect(() => {
    if (!follow.current) return
    const element = scroll.current
    if (!element) return
    const frame = window.requestAnimationFrame(() => { element.scrollTop = element.scrollHeight })
    return () => window.cancelAnimationFrame(frame)
  }, [messages.length, messages.at(-1)])

  useEffect(() => { follow.current = true }, [conv.activeId])
  useEffect(() => { setVisibleCount(30) }, [conv.activeId])
  // Command catalog powers the slash menu and prefix completion in the composer.
  useEffect(() => { void commands.loadAll() }, [])
  // The command palette hands picked skills over as a one-shot draft.
  useEffect(() => {
    if (!commands.pendingDraft) return
    setDraft(commands.pendingDraft)
    commands.clearDraft()
  }, [commands.pendingDraft])
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

  async function send(text: string, meta: PromptMeta, options?: { display?: string; prompt?: string }) {
    if (!config || busy) return
    follow.current = true
    try {
      await conv.sendMessage(options?.display ?? text, config.id, meta.thinkingEnabled, meta.thinkingBudget, meta.model, meta.reasoningEffort, meta.images, options?.prompt)
      setAttachments([])
    } catch (error) {
      // The draft and its images stay put so the user can fix and resend.
      setDraft(options?.display ?? text)
      notices.report(error)
    }
  }

  // Slash input runs through the shared command router; returns true when consumed.
  // User-invocable skills expand to their instructions and run as a task — the terminal's
  // default branch — while everything else stays a plain command.
  async function handleCommand(text: string): Promise<boolean> {
    const trimmed = text.trim()
    const word = trimmed.split(/\s+/)[0] ?? ''
    const skill = commands.commands.find(item => item.isSkill && item.text.toLowerCase() === word.toLowerCase())
    if (skill) {
      if (!config || busy) { notices.notify('技能暂不可用', '先连接一个模型配置，并等待当前任务结束。'); return true }
      try {
        const expanded = await commands.invokeSkill(skill.text.replace(/^\//, ''), trimmed.slice(word.length).trim())
        setDraft('')
        // The transcript keeps the original slash command; the expanded instructions plus
        // referenced images only travel to the model — the same split as the terminal.
        await send(trimmed, {
          thinkingEnabled: config.thinkingEnabled,
          thinkingBudget: config.thinkingBudget,
          model: config.defaultModel,
          images: [...attachments, ...expanded.images],
        }, { prompt: expanded.text })
      } catch (error) {
        notices.report(error)
      }
      return true
    }
    if (!trimmed.includes(' ') && word.length > 1 && !commands.commands.some(item => item.text.toLowerCase() === word.toLowerCase())) {
      const matches = matchCommands(commands.commands, word)
      if (matches.length === 1) {
        const consumed = await runCommand(matches[0].text, { go: onNavigate, openPalette: onPalette }, { apiConfigId: config?.id })
        if (consumed) setDraft('')
        return consumed
      }
    }
    const consumed = await runCommand(trimmed, { go: onNavigate, openPalette: onPalette }, { apiConfigId: config?.id })
    if (consumed) setDraft('')
    return consumed
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
      {meter || cache ? <div className="context-usage">
        {meter ? <>
          <span className="context-label">
            上下文 {meter.estimated ? '≈' : ''}{formatTokens(meter.usedTokens)} / {formatTokens(meter.capacityTokens)}
          </span>
          <Progress
            ariaLabel="上下文占用"
            max={meter.capacityTokens}
            size="sm"
            tone={meter.usedTokens / meter.capacityTokens > 0.85 ? 'destructive' : 'default'}
            value={meter.usedTokens}
          />
        </> : null}
        {cache ? <span
          className="context-label cache-chip"
          title={`缓存读取 ${formatTokens(cache.cachedInputTokens)} / 提示 ${formatTokens(cache.cacheInputTokens)} tok · 写入 ${formatTokens(cache.cacheWriteInputTokens)} tok · ${cache.cacheRequests} 次上报`}
        >
          缓存命中 {cache.hitRate.toFixed(1)}%{cache.cacheWriteInputTokens > 0 ? `（写入 ${formatTokens(cache.cacheWriteInputTokens)} tok）` : ''}
        </span> : null}
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
        attachments={attachments}
        busy={busy}
        commands={commands.commands}
        configs={enabledConfigs}
        draft={draft}
        models={config?.models ?? []}
        onAttachmentsChange={setAttachments}
        onCommand={handleCommand}
        onConfigure={() => onNavigate('models')}
        onDraftChange={setDraft}
        onNotify={notices.notify}
        onReport={notices.report}
        onSelect={id => modelSelection.select(id)}
        onSend={(text, meta) => void send(text, meta)}
        onSettings={() => onNavigate('settings')}
        onStop={() => void conv.cancelStream().catch(notices.report)}
        reasoning={reasoning}
        selectedId={modelSelection.configId ?? config?.id ?? null}
      />
      <SessionFooter configId={config?.id ?? null} model={conv.active?.modelId || config?.defaultModel || null} />
    </div>
  </section>
}
