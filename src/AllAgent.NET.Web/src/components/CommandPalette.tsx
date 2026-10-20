import { useEffect, useMemo, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Input } from '@/components/ui/input'
import { Modal } from '@/components/shared'
import { useStore } from '@/lib/store'
import { useCommandsStore } from '@/stores/commands'
import { useContributionsStore } from '@/stores/contributions'
import { usePanelsStore } from '@/stores/panels'
import { useNoticesStore } from '@/stores/ui'
import { isTerminalOnly, matchCommands, runCommand, WEB_COMMANDS, type CommandHost } from '@/lib/commandRouter'
import type { CommandInfo } from '@/types'

// Global command palette (Ctrl+Shift+P). Every terminal slash command is listed; the ones
// with browser equivalents execute, the rest are marked 终端专属. Plugin-contributed pages
// surface as their own group.
export function CommandPalette({ open, host, onClose }: { open: boolean; host: CommandHost; onClose: () => void }) {
  const store = useStore(useCommandsStore)
  const contributions = useStore(useContributionsStore)
  const panels = useStore(usePanelsStore)
  const notices = useStore(useNoticesStore)
  const [query, setQuery] = useState('')

  useEffect(() => {
    if (open) {
      setQuery('')
      void store.loadAll()
      void contributions.loadAll()
    }
  }, [open])

  const matches = useMemo(() => {
    // Browser-only bubbles (文件 / Git / 终端) sit next to the host's slash catalog.
    const catalog = [...WEB_COMMANDS, ...store.commands]
    const trimmed = query.trim()
    if (!trimmed) return catalog
    const slash = trimmed.startsWith('/') ? trimmed : `/${trimmed}`
    const byName = matchCommands(catalog, slash)
    if (byName.length) return byName
    return catalog.filter(item => item.description.includes(trimmed))
  }, [query, store.commands])

  const webCommands = useMemo(() => {
    const needle = query.trim().toLowerCase()
    if (!needle) return contributions.commands
    return contributions.commands.filter(command =>
      command.title.toLowerCase().includes(needle)
      || command.pluginName.toLowerCase().includes(needle)
      || command.key.toLowerCase().includes(needle))
  }, [query, contributions.commands])

  async function execute(item: CommandInfo) {
    // Skills run from the composer: fill the draft so the user can add task arguments first.
    if (item.isSkill) {
      host.go('chat')
      store.queueDraft(`${item.text} `)
      notices.notify('技能已放入输入框', '补充任务内容后发送，宿主会把技能指令展开进这次任务。')
      onClose()
      return
    }
    if (isTerminalOnly(item.text)) {
      notices.notify('终端专属命令', `${item.text} · 请在终端中使用；命令面板中已标注。`)
      return
    }
    const consumed = await runCommand(item.text, host)
    if (!consumed) notices.notify('未知命令', `${item.text} · 没有可用的处理分支。`)
    onClose()
  }

  useEffect(() => {
    if (!open) return
    const key = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { event.preventDefault(); onClose() }
      if (event.key === 'Enter' && matches[0]) { event.preventDefault(); void execute(matches[0]) }
    }
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  })

  if (!open) return null
  const common = matches.filter(item => !item.advanced && !item.isSkill)
  const skills = matches.filter(item => item.isSkill)
  const advanced = matches.filter(item => item.advanced && !item.isSkill)

  function row(item: CommandInfo) {
    const terminal = isTerminalOnly(item.text)
    return <button
      className={`palette-row ${terminal ? 'is-terminal-only' : ''}`}
      key={item.text}
      onClick={() => void execute(item)}
      type="button"
    >
      <span className="palette-command mono">{item.text}</span>
      {item.isSkill ? <Badge color="blue" size="sm" variant="dot">技能</Badge> : null}
      {item.text.startsWith('/ext ') ? <Badge color="gray" size="sm" variant="dot">插件</Badge> : null}
      {terminal ? <Badge color="gray" size="sm">终端专属</Badge> : null}
      <span className="palette-desc">{item.description}{item.usage ? ` · ${item.usage}` : ''}</span>
      {item.aliases ? <span className="palette-alias mono">{item.aliases}</span> : null}
    </button>
  }

  return <Modal onClose={onClose} size="lg" title="命令面板">
    {!store.available && store.loaded ? <p className="list-description">当前宿主不提供终端命令目录；API 键、插件与对话功能仍可正常使用。</p> : <>
      <Input
        aria-label="搜索命令"
        onValueChange={setQuery}
        placeholder="输入命令名或描述，例如 /permissions、会话…"
        size="sm"
        value={query}
      />
      <p className="palette-hint">Enter 执行第一个匹配 · 「终端专属」命令请回终端使用</p>
      <div className="palette-list">
        {common.length ? <div className="palette-group"><span className="palette-group-title">常用</span>{common.map(row)}</div> : null}
        {skills.length ? <div className="palette-group"><span className="palette-group-title">技能 · {skills.length}</span>{skills.map(row)}</div> : null}
        {webCommands.length ? <div className="palette-group">
          <span className="palette-group-title">插件界面 · {webCommands.length}</span>
          {webCommands.map(command => <button
            className="palette-row"
            key={command.key}
            onClick={() => {
              if (command.panel) panels.openPanel(command.panel)
              else if (command.route) host.go(command.route)
              onClose()
            }}
            type="button"
          >
            <span className="palette-command mono">{command.title}</span>
            <Badge color="gray" size="sm" variant="dot">插件</Badge>
            <span className="palette-desc">{command.pluginName} 提供的 Web 界面</span>
          </button>)}
        </div> : null}
        {advanced.length ? <div className="palette-group"><span className="palette-group-title">全部命令</span>{advanced.map(row)}</div> : null}
        {!matches.length && !webCommands.length ? <p className="list-description">没有匹配“{query}”的命令。</p> : null}
      </div>
    </>}
  </Modal>
}
