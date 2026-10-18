import { useEffect, useRef, useState } from 'react'
import {
  AlertTriangle, Blocks, ChevronRight, Command, FolderOpen, ListTodo, MessageSquare, Moon, PanelLeft,
  Pencil, Plug, Plus, Puzzle, Search, Settings as SettingsIcon, SlidersHorizontal, Sun, Trash2, Wrench,
} from 'lucide-react'
import { Badge } from './components/ui/badge'
import { Button } from './components/ui/b-button'
import { Input } from './components/ui/input'
import { StatusDot } from './components/ui/status-dot'
import { Tooltip } from './components/ui/b-tooltip'
import { Field, Confirm, Modal } from './components/shared'
import { CommandPalette } from './components/CommandPalette'
import { Notices } from './components/Notices'
import { PendingInteractions } from './components/PendingInteractions'
import { TaskDetailModal } from './components/TaskDetailModal'
import { WorkspaceSwitcher } from './components/WorkspaceSwitcher'
import { useStore } from './lib/store'
import { connectionLabel, useConnection } from './lib/useConnection'
import { formatRelative } from './lib/format'
import { bridgeEvents, onReconnect, reconnect } from './bridge'
import { useApiConfigsStore } from './stores/apiConfigs'
import { useApprovalsStore } from './stores/approvals'
import { useAgentsStore } from './stores/agents'
import { useConversationsStore } from './stores/conversations'
import { useMcpServersStore } from './stores/mcpServers'
import { usePluginsStore } from './stores/plugins'
import { useSettingsStore } from './stores/settings'
import { useSkillsStore } from './stores/skills'
import { useTasksStore } from './stores/tasks'
import { useNoticesStore } from './stores/ui'
import { useWorkspaceStore } from './stores/workspace'
import Chat from './views/Chat'
import Mcp from './views/Mcp'
import Models from './views/Models'
import Plugins from './views/Plugins'
import Settings from './views/Settings'
import Skills from './views/Skills'
import Tasks from './views/Tasks'
import Tools from './views/Tools'
import type { Conversation } from './types'

const navigation = [
  { id: 'chat', label: '对话', icon: MessageSquare },
  { id: 'models', label: '模型与 API', icon: SlidersHorizontal },
  { id: 'skills', label: '技能', icon: Blocks },
  { id: 'tools', label: '工具', icon: Wrench },
  { id: 'plugins', label: '插件', icon: Puzzle },
  { id: 'mcp', label: 'MCP 服务', icon: Plug },
  { id: 'tasks', label: '任务', icon: ListTodo },
  { id: 'settings', label: '设置', icon: SettingsIcon },
]

const GROUP_KEY = 'allagent.sidebar.groups'

function readGroups(): Record<string, boolean> {
  try { return JSON.parse(localStorage.getItem(GROUP_KEY) ?? '{}') as Record<string, boolean> } catch { return {} }
}

function useSystemDark(): boolean {
  const [dark, setDark] = useState(() => window.matchMedia('(prefers-color-scheme: dark)').matches)
  useEffect(() => {
    const query = window.matchMedia('(prefers-color-scheme: dark)')
    const update = () => setDark(query.matches)
    query.addEventListener('change', update)
    return () => query.removeEventListener('change', update)
  }, [])
  return dark
}

export default function App() {
  const conv = useStore(useConversationsStore)
  const settings = useStore(useSettingsStore)
  const skills = useStore(useSkillsStore)
  const mcp = useStore(useMcpServersStore)
  const api = useStore(useApiConfigsStore)
  const tasks = useStore(useTasksStore)
  const agents = useStore(useAgentsStore)
  const plugins = useStore(usePluginsStore)
  const workspace = useStore(useWorkspaceStore)
  const notices = useStore(useNoticesStore)
  const connection = useConnection()
  const approvals = useStore(useApprovalsStore)
  const systemDark = useSystemDark()
  const search = useRef<HTMLInputElement>(null)

  const [page, setPage] = useState(() => location.hash.split('/')[1] || 'chat')
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [ready, setReady] = useState(false)
  const [palette, setPalette] = useState(false)
  const [rename, setRename] = useState<Conversation | null>(null)
  const [renameValue, setRenameValue] = useState('')
  const [remove, setRemove] = useState<Conversation | null>(null)
  const [groups, setGroups] = useState<Record<string, boolean>>(readGroups)
  const [reconnectStalled, setReconnectStalled] = useState(false)

  const busy = conv.sending || conv.streamingMessageId !== null
  const dark = settings.theme === 'dark' || (settings.theme === 'system' && systemDark)
  const workspaceName = workspace.workspace.split(/[\\/]/).filter(Boolean).at(-1) ?? workspace.workspace

  function go(id: string) {
    location.hash = `/${id}`
    setPage(id)
    setSidebarOpen(false)
  }

  function toggleGroup(path: string, fallback: boolean) {
    setGroups(current => {
      const next = { ...current, [path]: !(current[path] ?? fallback) }
      localStorage.setItem(GROUP_KEY, JSON.stringify(next))
      return next
    })
  }

  async function newChat() {
    if (busy) return
    try {
      await conv.createConversation()
      go('chat')
    } catch (error) {
      notices.report(error)
    }
  }

  async function switchWorkspace(target: string, name: string) {
    if (target === workspace.workspace) return
    try {
      await workspace.switchTo(target)
      notices.notify('已切换工作区', name, 'success')
      await conv.loadAll()
      go('chat')
    } catch (error) {
      notices.report(error)
    }
  }

  function cycleTheme() {
    const next = settings.theme === 'light' ? 'dark' : settings.theme === 'dark' ? 'system' : 'light'
    settings.theme = next
    settings.applyTheme(next)
    void settings.save().catch(() => {})
  }

  useEffect(() => {
    const change = () => setPage(location.hash.split('/')[1] || 'chat')
    window.addEventListener('hashchange', change)
    Promise.allSettled([
      conv.loadAll(), api.loadAll(), skills.loadAll(), mcp.loadAll(),
      settings.load(), agents.loadAll(), tasks.loadAll(),
      plugins.loadAll(), workspace.load(), approvals.refresh(),
    ]).then(results => {
      const failures = results.filter((result): result is PromiseRejectedResult => result.status === 'rejected')
      if (failures.length) {
        notices.notify('部分数据加载失败', failures.map(failure => String(failure.reason instanceof Error ? failure.reason.message : failure.reason)).join('；'), 'destructive')
      }
      setReady(true)
    })
    return () => window.removeEventListener('hashchange', change)
  }, [])

  useEffect(() => { settings.applyTheme(settings.theme) }, [settings.theme])

  // A dead backend on an old port can be retried forever; after a few seconds make
  // the situation explicit and offer retry / reload instead of a silent spinner.
  useEffect(() => {
    if (connection !== 'connecting') { setReconnectStalled(false); return }
    const timer = window.setTimeout(() => setReconnectStalled(true), 10000)
    return () => window.clearTimeout(timer)
  }, [connection])

  // Backend restarted or switched workspace: reload everything it serves.
  useEffect(() => {
    async function reloadAll() {
      await Promise.allSettled([
        conv.refreshAfterBackendChange(), api.loadAll(), skills.loadAll(), mcp.loadAll(),
        tasks.loadAll(), settings.load(), agents.loadAll(), plugins.loadAll(), workspace.load(), approvals.refresh(),
      ])
    }
    const offReconnect = onReconnect(() => { void reloadAll() })
    const onWorkspaceChanged = () => { void reloadAll() }
    bridgeEvents.on('workspace.changed', onWorkspaceChanged)
    return () => {
      offReconnect()
      bridgeEvents.off('workspace.changed', onWorkspaceChanged)
    }
  }, [])

  useEffect(() => {
    const key = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key === 'n') {
        event.preventDefault()
        void newChat()
      }
      if ((event.ctrlKey || event.metaKey) && event.key === 'k') {
        event.preventDefault()
        search.current?.focus()
      }
      if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === 'p') {
        event.preventDefault()
        setPalette(open => !open)
      }
    }
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  })

  const connectionTone = connection === 'native' || connection === 'connected'
    ? 'success' as const
    : connection === 'connecting' ? 'active' as const : 'neutral' as const

  const filtered = conv.conversations.filter(item => item.title.toLowerCase().includes(query.toLowerCase()))
  const activeTitle = conv.active?.title
    || (conv.messages.length ? '对话' : '新对话')
  const otherWorkspaces = workspace.list.filter(entry => !entry.isCurrent)

  return <div className="workspace">
    {sidebarOpen ? <div className="sidebar-scrim" onClick={() => setSidebarOpen(false)} /> : null}
    <aside className={`sidebar ${sidebarOpen ? 'is-open' : ''}`}>
      <div className="brand">
        <div className="brand-symbol"><Command size={18} /></div>
        <div className="brand-copy"><strong>AllAgent.NET</strong><span>你的智能工作伙伴</span></div>
        <Badge size="sm">BETA</Badge>
      </div>
      <Button className="new-chat" disabled={busy} icon={<Plus size={15} />} onClick={() => void newChat()}>
        新建对话<span className="key-hint">Ctrl N</span>
      </Button>
      <nav aria-label="主导航">{navigation.map(item => <Button
        className={`nav-link ${page === item.id ? 'selected' : ''}`}
        icon={<item.icon size={15} />}
        key={item.id}
        onClick={() => go(item.id)}
        variant="ghost"
      >
        {item.label}
        {item.id === 'skills' && skills.skills.length ? <span className="nav-count">{skills.skills.length}</span> : null}
        {item.id === 'mcp' && mcp.servers.length ? <span className="nav-count">{mcp.servers.length}</span> : null}
        {item.id === 'tasks' && tasks.activeTasks.length ? <span className="nav-count active">{tasks.activeTasks.length}</span> : null}
      </Button>)}</nav>

      <div className="sidebar-section">
        <div className="section-row">
          <span>工作区</span>
          <Tooltip content="打开或新建工作区"><Button
            iconLabel="打开或新建工作区"
            onClick={() => workspace.openSwitcher()}
            size="icon-xs"
            variant="ghost"
          ><Plus size={13} /></Button></Tooltip>
        </div>
        <Input
          aria-label="搜索对话"
          onValueChange={setQuery}
          placeholder="搜索对话…  Ctrl K"
          ref={search}
          size="sm"
          startAdornment={<Search size={13} />}
          value={query}
        />
        <div className="groups">
          {workspace.available && workspace.workspace ? <div className="ws-group">
            <button
              className="ws-group-head"
              onClick={() => toggleGroup(workspace.workspace, true)}
              type="button"
            >
              <ChevronRight className={`ws-group-chevron ${(groups[workspace.workspace] ?? true) ? 'is-open' : ''}`} size={13} />
              <FolderOpen size={13} />
              <span className="ws-group-name">{workspaceName || '当前工作区'}</span>
              <Badge size="sm" variant="dot">当前</Badge>
              <span className="ws-group-count">{conv.conversations.length}</span>
            </button>
            {(groups[workspace.workspace] ?? true) ? <div className="history">
              {filtered.map(item => <div
                className={`history-row ${conv.activeId === item.id && page === 'chat' ? 'active' : ''}`}
                key={item.id}
              >
                <Button
                  className="history-title"
                  disabled={busy}
                  onClick={() => { void conv.selectConversation(item.id).then(() => go('chat')).catch(notices.report) }}
                  variant="ghost"
                ><span>{item.title}</span></Button>
                <span className="history-time">{formatRelative(item.updatedAt)}</span>
                <div className="history-actions">
                  <Tooltip content="重命名"><Button
                    disabled={busy}
                    iconLabel="重命名"
                    onClick={() => { setRename(item); setRenameValue(item.title) }}
                    size="icon-xs"
                    variant="ghost"
                  ><Pencil size={12} /></Button></Tooltip>
                  <Tooltip content="删除对话"><Button
                    disabled={busy}
                    iconLabel="删除对话"
                    onClick={() => setRemove(item)}
                    size="icon-xs"
                    variant="ghost"
                  ><Trash2 size={12} /></Button></Tooltip>
                </div>
              </div>)}
              {!conv.conversations.length ? <p className="history-empty">你的下一段灵感，从这里开始。</p> : null}
              {conv.conversations.length > 0 && !filtered.length ? <p className="history-empty">没有匹配的对话。</p> : null}
            </div> : null}
          </div> : null}
          {otherWorkspaces.map(entry => <div className="ws-group" key={entry.workspace}>
            <button
              className="ws-group-head"
              onClick={() => toggleGroup(entry.workspace, false)}
              type="button"
            >
              <ChevronRight className={`ws-group-chevron ${groups[entry.workspace] ? 'is-open' : ''}`} size={13} />
              <FolderOpen size={13} />
              <span className="ws-group-name">{entry.name}</span>
              <span className="ws-group-count">{entry.sessionCount}</span>
            </button>
            {groups[entry.workspace] ? <div className="ws-group-body">
              <p className="mono ws-group-path">{entry.workspace}</p>
              <div className="ws-group-actions">
                <Button
                  disabled={workspace.switching}
                  onClick={() => void switchWorkspace(entry.workspace, entry.name)}
                  size="xs"
                  variant="outline"
                >切换到此工作区</Button>
                <span>{entry.sessionCount} 个会话</span>
              </div>
            </div> : null}
          </div>)}
          {!workspace.available ? <div className="history">
            {filtered.map(item => <div
              className={`history-row ${conv.activeId === item.id && page === 'chat' ? 'active' : ''}`}
              key={item.id}
            >
              <Button
                className="history-title"
                disabled={busy}
                onClick={() => { void conv.selectConversation(item.id).then(() => go('chat')).catch(notices.report) }}
                variant="ghost"
              ><span>{item.title}</span></Button>
              <span className="history-time">{formatRelative(item.updatedAt)}</span>
            </div>)}
            {!conv.conversations.length ? <p className="history-empty">你的下一段灵感，从这里开始。</p> : null}
          </div> : null}
        </div>
      </div>

      <div className="sidebar-footer">
        <StatusDot animate={connection === 'connecting'} size="sm" tone={connectionTone} />
        <div><strong>{connectionLabel(connection)}</strong><small>{workspace.available
          ? `工作区 ${workspaceName || '未设置'}`
          : mcp.connectedIds.length ? `${mcp.connectedIds.length} 个 MCP 服务已连接` : '本地工作空间 · 数据不出本机'}</small></div>
        <Tooltip content="工作区设置"><Button iconLabel="工作区设置" onClick={() => go('settings')} size="icon-sm" variant="ghost"><SettingsIcon size={14} /></Button></Tooltip>
      </div>
    </aside>

    <main className="main-column">
      <header className="topbar">
        <Button className="mobile-menu" iconLabel="展开侧栏" onClick={() => setSidebarOpen(!sidebarOpen)} size="icon-sm" variant="ghost"><PanelLeft size={16} /></Button>
        {workspace.available ? <button className="topbar-workspace" onClick={() => workspace.openSwitcher()} type="button">
          <FolderOpen size={13} /><span>{workspaceName || '工作区'}</span>
        </button> : <span className="topbar-scope">工作空间</span>}
        <ChevronRight className="breadcrumb-slash" size={13} />
        <strong>{navigation.find(item => item.id === page)?.id === 'chat' ? activeTitle : navigation.find(item => item.id === page)?.label ?? '对话'}</strong>
        <div className="topbar-right">
          <Tooltip content="命令面板 · Ctrl+Shift+P">
            <Button iconLabel="命令面板" onClick={() => setPalette(true)} size="icon-sm" variant="ghost"><Command size={14} /></Button>
          </Tooltip>
          {tasks.activeTasks.length ? <Button
            icon={<StatusDot animate size="sm" tone="active" />}
            onClick={() => go('tasks')}
            size="sm"
            variant="ghost"
          >{tasks.activeTasks.length} 个任务</Button> : null}
          <Tooltip content={dark ? '切换到浅色' : '切换到深色'}>
            <Button iconLabel="切换主题" onClick={cycleTheme} size="icon-sm" variant="ghost">{dark ? <Sun size={14} /> : <Moon size={14} />}</Button>
          </Tooltip>
          <Badge color={busy ? 'blue' : 'green'} size="sm" variant="dot">{busy ? '正在执行' : '就绪'}</Badge>
        </div>
      </header>
      {connection === 'offline' ? <div className="connection-strip is-warning">
        <AlertTriangle size={14} />
        <span>未连接本地后端</span>
        <small>请在桌面应用中打开，或在终端运行 allagent 后使用 /web-ui 启动本地工作台。</small>
        <Button onClick={() => reconnect()} size="xs" variant="outline">重试连接</Button>
      </div> : null}
      {connection === 'connecting' && !reconnectStalled ? <div className="connection-strip">
        <StatusDot animate size="sm" tone="active" />
        <span>正在连接本地后端…</span>
        <small>界面上的操作会自动排队等待。</small>
      </div> : null}
      {connection === 'connecting' && reconnectStalled ? <div className="connection-strip is-warning">
        <AlertTriangle size={14} />
        <span>仍未连接上本地后端</span>
        <small>后端重启后会换端口：请在终端查看 /web-ui 输出的地址，或点击重试。</small>
        <Button onClick={() => reconnect()} size="xs" variant="outline">重试连接</Button>
        <Button onClick={() => location.reload()} size="xs" variant="ghost">刷新页面</Button>
      </div> : null}
      {page === 'models' ? <Models />
        : page === 'skills' ? <Skills />
          : page === 'tools' ? <Tools />
            : page === 'plugins' ? <Plugins />
              : page === 'mcp' ? <Mcp />
                : page === 'tasks' ? <Tasks />
                  : page === 'settings' ? <Settings />
                    : <Chat onNavigate={go} onPalette={() => setPalette(true)} ready={ready} />}
    </main>

    {rename ? <Modal
      footer={<Button onClick={() => {
        const title = renameValue.trim()
        if (!title) return
        void conv.renameConversation(rename.id, title).then(() => setRename(null)).catch(notices.report)
      }}>保存</Button>}
      onClose={() => setRename(null)}
      size="sm"
      title="重命名对话"
    >
      <Field label="对话名称" onChange={setRenameValue} required value={renameValue} />
    </Modal> : null}
    {remove ? <Confirm
      onClose={() => setRemove(null)}
      onConfirm={() => conv.deleteConversation(remove.id)}
      title={`删除「${remove.title}」？`}
      description="这段对话及其历史记录将从工作区移除。"
    /> : null}
    <Notices />
    <PendingInteractions />
    <WorkspaceSwitcher />
    <TaskDetailModal />
    <CommandPalette host={{ go, openPalette: () => setPalette(true) }} onClose={() => setPalette(false)} open={palette} />
  </div>
}
