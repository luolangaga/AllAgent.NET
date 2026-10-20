import { useEffect, useMemo, useRef, useState } from 'react'
import { AtSign, FileText, Folder, ImagePlus, Settings2, SlidersHorizontal, Square, X } from 'lucide-react'
import { AIInput, type PromptSettingGroup } from './ui/ai-input'
import { Button } from './ui/b-button'
import { PermissionPicker } from './PermissionPicker'
import { StatusDot } from './ui/status-dot'
import { invoke } from '@/bridge'
import { groupCommands, matchCommands } from '@/lib/commandRouter'
import { MAX_IMAGES, assertCapacity, base64Bytes, dataUrl, droppedFiles, formatBytes, imageFilesFrom, prepareImage, totalBytes } from '@/lib/attachments'
import { completeMention, findMention, parentPath, type MentionQuery } from '@/lib/mentions'
import { useT } from '@/i18n'
import type { ApiConfig, CommandInfo, ImageAttachment, InputDelivery, PendingInput, ReasoningOptions, WorkspaceFileEntry } from '@/types'

export interface PromptMeta {
  thinkingEnabled: boolean
  thinkingBudget: number
  model?: string
  reasoningEffort?: string
  images?: ImageAttachment[]
  /** How a prompt submitted during a run is delivered. */
  delivery?: InputDelivery
}

/** Mirrors the textarea cap inside the Iconiq composer. */
const MAX_TEXTAREA_HEIGHT = 132

export function PromptComposer({ draft, onDraftChange, attachments, onAttachmentsChange, busy, pending, configs, selectedId, onSelect, onSend, onStop, onCancelPending, onConfigure, onSettings, onNotify, onReport, models, reasoning, commands, onCommand }: {
  draft: string
  onDraftChange: (value: string) => void
  attachments: ImageAttachment[]
  onAttachmentsChange: (images: ImageAttachment[]) => void
  busy: boolean
  pending: PendingInput[]
  onCancelPending: (id: string) => void
  configs: ApiConfig[]
  selectedId: string | null
  onSelect: (id: string) => void
  onSend: (text: string, meta: PromptMeta) => void
  onStop: () => void
  onConfigure: () => void
  onSettings: () => void
  onNotify: (title: string, message?: string, appearance?: 'default' | 'success' | 'info' | 'destructive' | 'warning') => void
  onReport: (error: unknown) => void
  models: string[]
  reasoning: ReasoningOptions | null
  commands: CommandInfo[]
  onCommand: (text: string, delivery?: InputDelivery) => Promise<boolean>
}) {
  const tr = useT()
  const config = configs.find(item => item.id === selectedId) ?? configs[0]
  const areaRef = useRef<HTMLDivElement>(null)
  const fileInput = useRef<HTMLInputElement>(null)

  // Slash menu: the full terminal catalog — built-ins, plugin commands and user-invocable
  // skills — filtered by prefix while the draft is still a single command word.
  const suggestions = useMemo(() => {
    const trimmed = draft.trimStart()
    if (!trimmed.startsWith('/') || trimmed.includes(' ')) return []
    return matchCommands(commands, trimmed)
  }, [draft, commands])
  const commandGroups = useMemo(() => groupCommands(suggestions), [suggestions])
  const flat = useMemo(() => commandGroups.flatMap(group => group.items), [commandGroups])
  const indexOf = useMemo(() => new Map(flat.map((item, index) => [item, index])), [flat])
  const [activeIndex, setActiveIndex] = useState(0)
  const [dismissed, setDismissed] = useState(false)
  const listRef = useRef<HTMLDivElement>(null)

  useEffect(() => { setActiveIndex(0); setDismissed(false) }, [draft])
  useEffect(() => {
    const item = listRef.current?.querySelectorAll('.slash-item')[activeIndex] as HTMLElement | undefined
    item?.scrollIntoView({ block: 'nearest' })
  }, [activeIndex, flat.length])

  // `@` references: the token under the caret drives a workspace file lookup.
  const [mention, setMention] = useState<MentionQuery | null>(null)
  const [files, setFiles] = useState<WorkspaceFileEntry[]>([])
  const [fileIndex, setFileIndex] = useState(0)
  const [loadingFiles, setLoadingFiles] = useState(false)
  const fileList = useRef<HTMLDivElement>(null)
  // Query accepted a moment ago: deleting the trailing space must not reopen the picker.
  const accepted = useRef<string | null>(null)
  const mentionQuery = mention?.query ?? null

  const [dragging, setDragging] = useState(false)
  const [preparing, setPreparing] = useState(false)
  // While a turn is running, plain text steers it by default; the toggle lets it wait instead.
  const [delivery, setDelivery] = useState<InputDelivery>('steer')
  useEffect(() => { if (!busy) setDelivery('steer') }, [busy])

  useEffect(() => {
    if (mentionQuery === null) { setFiles([]); return }
    let active = true
    setLoadingFiles(true)
    // A tiny debounce keeps fast typing from flooding the bridge.
    const timer = window.setTimeout(() => {
      invoke<WorkspaceFileEntry[]>('workspace.files', { query: mentionQuery, limit: 40 })
        .then(entries => { if (active) setFiles(entries) })
        .catch(() => { if (active) setFiles([]) })
        .finally(() => { if (active) setLoadingFiles(false) })
    }, 90)
    return () => { active = false; window.clearTimeout(timer) }
  }, [mentionQuery])

  useEffect(() => { setFileIndex(0) }, [mentionQuery])
  useEffect(() => {
    const item = fileList.current?.children[fileIndex] as HTMLElement | undefined
    item?.scrollIntoView({ block: 'nearest' })
  }, [fileIndex, files.length])

  const open = suggestions.length > 0 && !dismissed
  const mentionOpen = mention !== null
  // A fully typed reference is sent as-is instead of asking for a second Enter.
  const exact = useMemo(() => mentionQuery === null ? undefined
    : files.find(entry => !entry.isDirectory && entry.path.toLowerCase() === mentionQuery.toLowerCase()),
  [files, mentionQuery])

  function textarea(): HTMLTextAreaElement | null {
    return areaRef.current?.querySelector('textarea') ?? null
  }

  /** Programmatic edits bypass the composer's own height sync, so redo it here. */
  function focusCaret(position: number) {
    window.requestAnimationFrame(() => {
      const element = textarea()
      if (!element) return
      element.focus()
      element.setSelectionRange(position, position)
      element.style.height = 'auto'
      element.style.height = `${Math.min(element.scrollHeight, MAX_TEXTAREA_HEIGHT)}px`
    })
  }

  function changeDraft(next: string) {
    onDraftChange(next)
    setDismissed(false)
    const element = textarea()
    if (!element || element.selectionStart !== element.selectionEnd) { setMention(null); return }
    const found = findMention(next, element.selectionStart ?? next.length)
    setMention(found && found.query !== accepted.current ? found : null)
  }

  function acceptFile(entry: WorkspaceFileEntry) {
    if (!mention) return
    const element = textarea()
    const caret = element?.selectionStart ?? draft.length
    const next = completeMention(draft, caret, mention, entry)
    accepted.current = entry.isDirectory ? null : entry.path
    onDraftChange(next.text)
    focusCaret(next.caret)
    // Directories keep the picker open so the user can drill into them.
    setFiles([])
    setMention(entry.isDirectory ? { start: mention.start, query: `${entry.path}/` } : null)
  }

  function insertMention() {
    const element = textarea()
    const caret = element?.selectionStart ?? draft.length
    onDraftChange(`${draft.slice(0, caret)}@${draft.slice(caret)}`)
    focusCaret(caret + 1)
    setMention({ start: caret, query: '' })
  }

  // Attachments are normalized one by one so a rejected image never drops the batch.
  async function attachFiles(incoming: File[]) {
    if (!incoming.length) return
    setPreparing(true)
    try {
      const next = [...attachments]
      for (const file of incoming.slice(0, MAX_IMAGES)) {
        try {
          const image = await prepareImage(file)
          assertCapacity([...next, image])
          next.push(image)
        } catch (error) {
          onReport(error)
        }
      }
      if (incoming.length > MAX_IMAGES) onNotify(tr('图片数量已达上限', 'Image limit reached'), tr(`每条消息最多 ${MAX_IMAGES} 张，多余的图片未添加。`, `At most ${MAX_IMAGES} images per message; the extra images were not added.`), 'warning')
      if (next.length !== attachments.length) onAttachmentsChange(next)
    } finally {
      setPreparing(false)
    }
  }

  function removeAttachment(index: number) {
    onAttachmentsChange(attachments.filter((_, position) => position !== index))
  }

  // Tab completion fills the command and opens a trailing space for its arguments.
  function complete(item: CommandInfo) { changeDraft(`${item.text} `) }

  function accept(item: CommandInfo) {
    // Parameterised built-ins wait for their arguments; skills run right away — the terminal
    // executes a bare skill command on Enter too.
    if (item.usage && !item.isSkill) { complete(item); return }
    void onCommand(item.text).then(consumed => { if (!consumed) complete(item) })
  }

  // Arrow keys, Tab, Enter and Esc are captured before the textarea sees them so the menu
  // behaves like a proper completer instead of scrolling the caret or sending the draft.
  function handleKeys(event: React.KeyboardEvent) {
    if (mentionOpen) {
      const count = files.length
      if (event.key === 'ArrowDown') {
        event.preventDefault(); event.stopPropagation()
        if (count) setFileIndex(index => (index + 1) % count)
        return
      }
      if (event.key === 'ArrowUp') {
        event.preventDefault(); event.stopPropagation()
        if (count) setFileIndex(index => (index - 1 + count) % count)
        return
      }
      if (event.key === 'Tab' || (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing)) {
        // An exact match means the reference is complete: Enter sends the message.
        if (event.key === 'Enter' && exact) return
        const entry = count ? files[Math.min(fileIndex, count - 1)] : undefined
        if (entry) {
          event.preventDefault(); event.stopPropagation()
          acceptFile(entry)
        }
        return
      }
      if (event.key === 'Escape') {
        event.preventDefault(); event.stopPropagation()
        accepted.current = mentionQuery
        setMention(null)
        return
      }
    }
    if (!open) return
    if (event.key === 'ArrowDown') {
      event.preventDefault(); event.stopPropagation()
      setActiveIndex(index => (index + 1) % flat.length)
      return
    }
    if (event.key === 'ArrowUp') {
      event.preventDefault(); event.stopPropagation()
      setActiveIndex(index => (index - 1 + flat.length) % flat.length)
      return
    }
    if (event.key === 'Tab') {
      event.preventDefault(); event.stopPropagation()
      complete(flat[activeIndex] ?? flat[0])
      return
    }
    if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault(); event.stopPropagation()
      accept(flat[activeIndex] ?? flat[0])
      return
    }
    if (event.key === 'Escape') {
      event.preventDefault(); event.stopPropagation()
      setDismissed(true)
    }
  }

  const modelOptions = useMemo(() => {
    const list = models.length ? models : config?.defaultModel ? [config.defaultModel] : []
    const current = config?.defaultModel
    return [...new Set([...(current ? [current] : []), ...list])].map(id => ({ value: id, label: id }))
  }, [models, config?.defaultModel])

  const thinkingOptions = [
    { value: 'off', label: tr('标准回答', 'Standard answer') },
    { value: 'on', label: tr('深度思考', 'Deep thinking') },
    ...(reasoning?.efforts ?? []).filter(effort => effort !== 'default').map(effort => ({ value: `effort:${effort}`, label: tr(`等级 · ${effort}`, `Level · ${effort}`) })),
  ]

  // Iconiq's settings dropdown only renders groups that declare a display mode:
  // submenu groups get a label, a live value and a flyout list.
  const groups: PromptSettingGroup[] = [
    ...(modelOptions.length > 0 ? [{ id: 'model', label: tr('模型', 'Model'), display: 'submenu' as const, options: modelOptions }] : []),
    { id: 'thinking', label: tr('思考等级', 'Thinking level'), display: 'submenu' as const, options: thinkingOptions },
  ]

  const attached = attachments.length > 0

  return <div
    className={`composer-area ${dragging ? 'is-dropping' : ''}`}
    onDragLeave={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setDragging(false) }}
    onDragOver={event => {
      if (!Array.from(event.dataTransfer?.types ?? []).includes('Files')) return
      event.preventDefault()
      setDragging(true)
    }}
    onDrop={event => {
      const dropped = droppedFiles(event.dataTransfer)
      if (!dropped.length) return
      event.preventDefault()
      setDragging(false)
      const images = dropped.filter(file => file.type.toLowerCase().startsWith('image/'))
      if (!images.length) { onNotify(tr('暂时无法引用这些文件', 'These files cannot be referenced'), tr('网页只能把图片作为附件；其他文件请在输入框用 @ 引用工作区路径。', 'The browser can attach images only; reference other files by workspace path with @ in the composer.'), 'warning'); return }
      void attachFiles(images)
    }}
    onKeyDownCapture={handleKeys}
    onPaste={event => {
      const images = imageFilesFrom(event.clipboardData)
      if (!images.length) return
      event.preventDefault()
      void attachFiles(images)
    }}
    ref={areaRef}
  >
    <div className="composer-meta">
      <span className="composer-model">
        <StatusDot animate={busy} size="sm" tone={config ? (busy ? 'active' : 'success') : 'neutral'} />
        {config ? `${config.displayName} · ${config.defaultModel}` : tr('先连接一个模型，开启对话', 'Connect a model to start chatting')}
      </span>
      <span className="composer-meta-actions">
        <PermissionPicker />
        {busy ? <div aria-label={tr('运行中输入的处理方式', 'Delivery while running')} className="delivery-toggle" role="group">
          <button
            aria-pressed={delivery === 'steer'}
            className={delivery === 'steer' ? 'is-active' : ''}
            onClick={() => setDelivery('steer')}
            title={tr('在下一次模型步骤插入指导，立即影响当前任务', 'Insert guidance at the next model step and affect the current task immediately')}
            type="button"
          >{tr('插队', 'Steer')}</button>
          <button
            aria-pressed={delivery === 'queue'}
            className={delivery === 'queue' ? 'is-active' : ''}
            onClick={() => setDelivery('queue')}
            title={tr('当前任务结束后，再把这句作为新任务发送', 'Send this as a new task after the current one finishes')}
            type="button"
          >{tr('排队', 'Queue')}</button>
        </div> : null}
        {busy ? <Button icon={<Square size={12} />} onClick={onStop} size="sm" variant="outline">{tr('停止生成', 'Stop')}</Button> : null}
      </span>
    </div>
    {pending.length ? <div className="composer-pending">
      <span className="pending-label">{tr('待处理', 'Pending')} {pending.length}</span>
      {pending.map(item => <span className="pending-chip" key={item.id} title={item.draft}>
        <span className={`pending-mode ${item.delivery === 'queue' ? 'is-queue' : ''}`}>{item.delivery === 'queue' ? tr('排队', 'Queue') : tr('插队', 'Steer')}</span>
        <span className="pending-text">{item.draft}</span>
        <button aria-label={tr(`取消待处理输入：${item.draft}`, `Cancel pending input: ${item.draft}`)} className="pending-remove" onClick={() => onCancelPending(item.id)} type="button"><X size={11} /></button>
      </span>)}
    </div> : null}
    {attached || preparing ? <div className="composer-attachments">
      {attachments.map((image, index) => <div className="attachment-chip" key={`${image.name}-${index}`}>
        <img alt={image.name} src={dataUrl(image)} />
        <span className="attachment-name" title={image.name}>{image.name}</span>
        <span className="attachment-size">{formatBytes(base64Bytes(image.base64))}</span>
        <button aria-label={tr(`移除 ${image.name}`, `Remove ${image.name}`)} className="attachment-remove" onClick={() => removeAttachment(index)} type="button"><X size={12} /></button>
      </div>)}
      {preparing ? <span className="attachment-chip is-busy">{tr('正在读取图片…', 'Reading images…')}</span> : null}
      <span className="attachment-total">{tr(`${attachments.length} 张`, `${attachments.length} images`)} · {formatBytes(totalBytes(attachments))}</span>
    </div> : null}
    <div className="composer-input">
      {mentionOpen ? <div className="slash-pop">
        <div aria-label={tr('工作区文件建议', 'Workspace file suggestions')} className="slash-menu" ref={fileList} role="listbox">
          {files.map((entry, index) => <button
            aria-selected={index === fileIndex}
            className={`slash-item ${index === fileIndex ? 'is-active' : ''}`}
            key={entry.path}
            onClick={() => acceptFile(entry)}
            onMouseDown={event => event.preventDefault()}
            onMouseEnter={() => setFileIndex(index)}
            role="option"
            type="button"
          >
            <span className="mention-icon">{entry.isDirectory ? <Folder size={13} /> : <FileText size={13} />}</span>
            <span className="slash-name mono">{entry.name}</span>
            <span className="slash-desc">{entry.isDirectory ? tr('文件夹', 'Folder') : parentPath(entry.path) || tr('工作区根目录', 'Workspace root')}</span>
          </button>)}
          {files.length ? null : <div className="mention-empty">{loadingFiles ? tr('正在读取工作区…', 'Reading the workspace…') : tr('没有匹配的工作区文件', 'No matching workspace files')}</div>}
        </div>
        <div className="slash-hint"><span>↑↓</span> {tr('选择', 'select')} · <span>Tab</span> {tr('补全', 'complete')} · <span>Enter</span> {tr('引用', 'reference')} · <span>Esc</span> {tr('关闭', 'close')}</div>
      </div> : null}
      {open ? <div className="slash-pop">
        <div aria-label={tr('命令建议', 'Command suggestions')} className="slash-menu" ref={listRef} role="listbox">
          {commandGroups.map(group => <div className="slash-group" key={group.id}>
            <span className="slash-group-title">{group.title}</span>
            {group.items.map(item => <button
              aria-selected={indexOf.get(item) === activeIndex}
              className={`slash-item ${indexOf.get(item) === activeIndex ? 'is-active' : ''}`}
              key={item.text}
              onClick={() => accept(item)}
              onMouseDown={event => event.preventDefault()}
              onMouseEnter={() => setActiveIndex(indexOf.get(item) ?? 0)}
              role="option"
              type="button"
            >
              <span className={`slash-dot ${item.isSkill ? 'is-skill' : ''}`} />
              <span className="slash-name mono">{item.text}</span>
              <span className="slash-desc">{item.description}</span>
              {item.usage ? <span className="slash-usage mono">{item.usage}</span> : null}
            </button>)}
          </div>)}
        </div>
        <div className="slash-hint"><span>↑↓</span> {tr('选择', 'select')} · <span>Tab</span> {tr('补全', 'complete')} · <span>Enter</span> {tr('运行', 'run')} · <span>Esc</span> {tr('关闭', 'close')}</div>
      </div> : null}
      <AIInput
        agents={configs.map(item => ({ value: item.id, label: item.displayName }))}
        defaultAgent={config?.id}
        defaultSettings={{
          model: config?.defaultModel ?? '',
          thinking: config?.thinkingEnabled ? 'on' : 'off',
          budget: String(config?.thinkingBudget ?? 8000),
        }}
        disabled={!config}
        key={config?.id ?? 'none'}
        menuItems={[
          { value: 'attach', label: tr('添加图片', 'Add images'), icon: ImagePlus, shortcut: 'Ctrl+V', onClick: () => fileInput.current?.click() },
          { value: 'mention', label: tr('引用工作区文件', 'Reference a workspace file'), icon: AtSign, shortcut: '@', onClick: insertMention },
          { value: 'models', label: tr('模型与 API 配置', 'Models & API'), icon: SlidersHorizontal, onClick: onConfigure },
          { value: 'settings', label: tr('工作空间设置', 'Workspace settings'), icon: Settings2, onClick: onSettings },
        ]}
        onAgentChange={onSelect}
        onSend={(text, meta) => {
          const thinking = meta.settings.thinking ?? 'off'
          const effort = thinking.startsWith('effort:') ? thinking.slice('effort:'.length) : undefined
          const payload: PromptMeta = {
            thinkingEnabled: thinking !== 'off',
            thinkingBudget: Number(meta.settings.budget ?? config?.thinkingBudget ?? 8000),
            model: meta.settings.model || undefined,
            reasoningEffort: effort,
            images: attached ? attachments : undefined,
            delivery,
          }
          // Slash input is routed as a command; unmatched skill text falls through as a task.
          if (text.trim().startsWith('/')) {
            void onCommand(text.trim(), delivery).then(consumed => {
              if (consumed) return
              onSend(text, payload)
              onDraftChange('')
            })
            return
          }
          onSend(text, payload)
        }}
        onValueChange={changeDraft}
        placeholder={!config
          ? tr('请先在「模型与 API」中添加配置', 'Add a configuration in Models & API first')
          : busy
            ? delivery === 'queue'
              ? tr('输入下一条任务；当前任务结束后自动发送…', 'Type the next task; it sends when the current one finishes…')
              : tr('输入指导内容，Enter 插队到当前任务…', 'Type guidance; Enter steers the current task…')
            : tr('描述你的想法、输入 / 唤起命令、@ 引用文件，或直接粘贴图片…', 'Describe your idea, type / for commands, @ to reference files, or paste an image…')}
        settingGroups={groups}
        showMessages={false}
        value={draft}
      />
      <input
        accept="image/png,image/jpeg,image/gif,image/webp"
        className="composer-file"
        multiple
        onChange={event => {
          const picked = Array.from(event.target.files ?? [])
          event.target.value = ''
          void attachFiles(picked)
        }}
        ref={fileInput}
        type="file"
      />
      {dragging ? <div className="composer-drop">{tr('松开即可添加图片', 'Drop to add images')}</div> : null}
    </div>
    <div className="composer-hint">
      <span>{!config ? '' : busy
        ? delivery === 'queue'
          ? tr('Enter 排队 · 当前任务结束后自动发送 · Shift + Enter 换行', 'Enter queues · sends after the current task · Shift + Enter for a newline')
          : tr('Enter 插队 · 在下一个模型步骤生效 · Shift + Enter 换行', 'Enter steers · applies at the next model step · Shift + Enter for a newline')
        : tr('Enter 发送 · Shift + Enter 换行 · / 命令 · @ 引用文件', 'Enter sends · Shift + Enter for a newline · / commands · @ references files')}</span>
      <span>{attached && !draft.trim() ? tr('图片会随消息一起发送，请再补充一句说明', 'Images are sent with the message; add a short description') : tr('重要内容请核实', 'Verify important information')}</span>
    </div>
  </div>
}
