import { useEffect, useRef, useState } from 'react'
import {
  AlertTriangle, Blocks, ChevronRight, Command, FolderOpen, FolderPlus, FolderTree, GitBranch, ListTodo, MessageSquare, Moon,
  PanelLeft, Pencil, Plug, Plus, Puzzle, Search, Settings as SettingsIcon, SlidersHorizontal, SquareTerminal, Sun,
  Terminal as TerminalIcon, Trash2, Wrench,
} from 'lucide-react'
import { Badge } from './components/ui/badge'
import { Button } from './components/ui/b-button'
import { Input } from './components/ui/input'
import { StatusDot } from './components/ui/status-dot'
import { Tooltip } from './components/ui/b-tooltip'
import { Field, Confirm, Modal } from './components/shared'
import { CommandPalette } from './components/CommandPalette'
import { GitPanel } from './components/GitPanel'
import { Notices } from './components/Notices'
import { PendingInteractions } from './components/PendingInteractions'
import { pluginIcon } from './components/plugins/icons'
import { PluginPage } from './components/plugins/PluginPage'
import { PluginPanel } from './components/plugins/PluginPanel'
import { RunDock } from './components/RunDock'
import { RunMarker } from './components/RunMarker'
import { TaskDetailModal } from './components/TaskDetailModal'
import { TerminalPanel } from './components/TerminalPanel'
import { UpdateBanner } from './components/UpdateBanner'
import { WorkspacePanel } from './components/WorkspacePanel'
import { WorkspaceSwitcher } from './components/WorkspaceSwitcher'
import { useStore } from './lib/store'
import { useT } from './i18n'
import { connectionLabel, useConnection } from './lib/useConnection'
import { formatRelative } from './lib/format'
import { bridgeEvents, onReconnect, reconnect } from './bridge'
import { useApiConfigsStore } from './stores/apiConfigs'
import { useApprovalsStore } from './stores/approvals'
import { useAgentsStore } from './stores/agents'
import { useAuthStore } from './stores/auth'
import { useContributionsStore } from './stores/contributions'
import { useConversationsStore } from './stores/conversations'
import { useGitStore } from './stores/git'
import { useMcpServersStore } from './stores/mcpServers'
import { usePanelsStore } from './stores/panels'
import { usePluginsStore } from './stores/plugins'
import { useSettingsStore } from './stores/settings'
import { useSkillsStore } from './stores/skills'
import { useTasksStore } from './stores/tasks'
import { useTerminalStore } from './stores/terminal'
import { useNoticesStore } from './stores/ui'
import { useWorkspaceBrowserStore } from './stores/workspaceBrowser'
import { useWorkspaceStore } from './stores/workspace'
import Chat from './views/Chat'
import Mcp from './views/Mcp'
import Models from './views/Models'
import Plugins from './views/Plugins'
import PowerShell from './views/PowerShell'
import Settings from './views/Settings'
import Skills from './views/Skills'
import Tasks from './views/Tasks'
import Tools from './views/Tools'
import type { Conversation } from './types'

function navigationItems(tr: (zh: string, en: string) => string) {
  return [
    { id: 'chat', label: tr('对话', 'Chat'), icon: MessageSquare },
    { id: 'models', label: tr('模型与 API', 'Models & API'), icon: SlidersHorizontal },
    { id: 'skills', label: tr('技能', 'Skills'), icon: Blocks },
    { id: 'tools', label: tr('工具', 'Tools'), icon: Wrench },
    { id: 'powershell', label: 'PowerShell', icon: SquareTerminal },
    { id: 'plugins', label: tr('插件', 'Plugins'), icon: Puzzle },
    { id: 'mcp', label: tr('MCP 服务', 'MCP servers'), icon: Plug },
    { id: 'tasks', label: tr('任务', 'Tasks'), icon: ListTodo },
    { id: 'settings', label: tr('设置', 'Settings'), icon: SettingsIcon },
  ]
}

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
  const auth = useStore(useAuthStore)
  const tasks = useStore(useTasksStore)
  const agents = useStore(useAgentsStore)
  const plugins = useStore(usePluginsStore)
  const contributions = useStore(useContributionsStore)
  const workspace = useStore(useWorkspaceStore)
  const notices = useStore(useNoticesStore)
  const panels = useStore(usePanelsStore)
  const git = useStore(useGitStore)
  const tr = useT()
  const browser = useStore(useWorkspaceBrowserStore)
  const terminal = useStore(useTerminalStore)
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
  // Topbar Git badge: uncommitted changes in the current workspace's repository.
  const gitBadge = git.summary?.repo
    ? git.summary.staged + git.summary.unstaged + git.summary.untracked + git.summary.conflicted
    : 0

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

  function newChat() {
    try {
      // Draft only: the session is created when the first message is sent.
      conv.newConversation()
      go('chat')
    } catch (error) {
      notices.report(error)
    }
  }

  async function switchWorkspace(target: string, name: string) {
    if (target === workspace.workspace) return
    try {
      await workspace.switchTo(target)
      notices.notify(tr('已切换工作区', 'Workspace switched'), name, 'success')
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
      conv.loadAll(), conv.loadRunning(), api.loadAll(), skills.loadAll(), mcp.loadAll(),
      settings.load(), agents.loadAll(), tasks.loadAll(),
      plugins.loadAll(), contributions.loadAll(), workspace.load(), approvals.refresh(), git.load(),
    ]).then(results => {
      const failures = results.filter((result): result is PromiseRejectedResult => result.status === 'rejected')
      if (failures.length) {
        notices.notify(tr('部分数据加载失败', 'Some data failed to load'), failures.map(failure => String(failure.reason instanceof Error ? failure.reason.message : failure.reason)).join(tr('；', '; ')), 'destructive')
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

  // Backend restarted or switched workspace: reload everything it serves. The file tree and
  // Git status are workspace-scoped; terminals are host-level and only need re-syncing.
  useEffect(() => {
    async function reloadAll() {
      browser.reset()
      await Promise.allSettled([
        conv.refreshAfterBackendChange(), api.loadAll(), auth.loadAll(), skills.loadAll(), mcp.loadAll(),
        tasks.loadAll(), settings.load(), agents.loadAll(), plugins.loadAll(), contributions.loadAll(),
        workspace.load(), approvals.refresh(), git.load(), terminal.sync(),
      ])
    }
    const offReconnect = onReconnect(() => { void reloadAll() })
    const onWorkspaceChanged = () => { void reloadAll() }
    const onSettingsChanged = (next: { language: typeof settings.language; languageResolved: typeof settings.languageResolved }) => {
      settings.applyFromBridge(next)
    }
    bridgeEvents.on('workspace.changed', onWorkspaceChanged)
    bridgeEvents.on('settings.changed', onSettingsChanged)
    return () => {
      offReconnect()
      bridgeEvents.off('workspace.changed', onWorkspaceChanged)
      bridgeEvents.off('settings.changed', onSettingsChanged)
    }
  }, [])

  useEffect(() => {
    document.title = tr('AllAgent.NET · 本地 Agent 工作台', 'AllAgent.NET · Local Agent workspace')
  }, [settings.languageResolved])

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
      // Bubble shortcuts mirror VSCode: explorer, source control and the integrated terminal.
      if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === 'e') {
        event.preventDefault()
        panels.togglePanel('files')
      }
      if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === 'g') {
        event.preventDefault()
        panels.togglePanel('git')
      }
      if ((event.ctrlKey || event.metaKey) && !event.shiftKey && (event.key === '`' || event.key === '~')) {
        event.preventDefault()
        panels.togglePanel('terminal')
        // Toggling stores the new state synchronously; opening takes focus right away.
        if (panels.open.terminal) terminal.requestFocus()
      }
    }
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  })

  const connectionTone = connection === 'native' || connection === 'connected'
    ? 'success' as const
    : connection === 'connecting' ? 'active' as const : 'neutral' as const

  const filtered = conv.conversations.filter(item => item.title.toLowerCase().includes(query.toLowerCase()))
  const navigation = navigationItems(tr)
  const activeTitle = conv.active?.title
    || (conv.messages.length ? tr('对话', 'Chat') : tr('新对话', 'New conversation'))
  const otherWorkspaces = workspace.list.filter(entry => !entry.isCurrent)
  // Plugin pages slot into the sidebar before Settings, right after the built-in entries.
  const navItems = [
    ...navigation.slice(0, -1),
    ...contributions.pages.map(entry => ({ id: entry.route, label: entry.title, icon: pluginIcon(entry.icon) })),
    navigation[navigation.length - 1],
  ]

  return <div className="workspace">
    {sidebarOpen ? <div className="sidebar-scrim" onClick={() => setSidebarOpen(false)} /> : null}
    <aside className={`sidebar ${sidebarOpen ? 'is-open' : ''}`}>
      <div className="brand">
        <div className="brand-symbol"><Command size={18} /></div>
        <div className="brand-copy"><strong>AllAgent.NET</strong><span>{tr('你的智能工作伙伴', 'Your intelligent work partner')}</span></div>
        <Badge size="sm">BETA</Badge>
      </div>
      <div className="new-chat-row">
        <Tooltip content={tr('新建对话 · Ctrl N', 'New conversation · Ctrl N')}>
          <Button className="new-chat" icon={<Plus size={15} />} onClick={() => void newChat()}>
            {tr('新建对话', 'New conversation')}
          </Button>
        </Tooltip>
        <Tooltip content={tr('打开或新建工作区', 'Open or create a workspace')}>
          <Button
            className="new-chat"
            icon={<FolderPlus size={15} />}
            onClick={() => workspace.openSwitcher()}
            variant="outline"
          >
            {tr('新建工作区', 'New workspace')}
          </Button>
        </Tooltip>
      </div>
      <nav aria-label={tr('主导航', 'Main navigation')} className="sidebar-nav">{navItems.map(item => <Button
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
          <span>{tr('工作区', 'Workspace')}</span>
        </div>
        <Input
          aria-label={tr('搜索对话', 'Search conversations')}
          onValueChange={setQuery}
          placeholder={tr('搜索对话…  Ctrl K', 'Search conversations…  Ctrl K')}
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
              <span className="ws-group-name">{workspaceName || tr('当前工作区', 'Current workspace')}</span>
              <Badge size="sm" variant="dot">{tr('当前', 'Current')}</Badge>
              <span className="ws-group-count">{conv.conversations.length}</span>
            </button>
            {(groups[workspace.workspace] ?? true) ? <div className="history">
              {filtered.map(item => <div
                className={`history-row ${conv.activeId === item.id && page === 'chat' ? 'active' : ''}`}
                key={item.id}
              >
                <Button
                  className="history-title"
                  onClick={() => { void conv.selectConversation(item.id).then(() => go('chat')).catch(notices.report) }}
                  variant="ghost"
                ><span>{item.title}</span></Button>
                <RunMarker state={conv.runState(item.id)} />
                <span className="history-time">{formatRelative(item.updatedAt)}</span>
                <div className="history-actions">
                  <Tooltip content={tr('重命名', 'Rename')}><Button
                    disabled={busy}
                    iconLabel={tr('重命名', 'Rename')}
                    onClick={() => { setRename(item); setRenameValue(item.title) }}
                    size="icon-xs"
                    variant="ghost"
                  ><Pencil size={12} /></Button></Tooltip>
                  <Tooltip content={tr('删除对话', 'Delete conversation')}><Button
                    disabled={busy}
                    iconLabel={tr('删除对话', 'Delete conversation')}
                    onClick={() => setRemove(item)}
                    size="icon-xs"
                    variant="ghost"
                  ><Trash2 size={12} /></Button></Tooltip>
                </div>
              </div>)}
              {!conv.conversations.length ? <p className="history-empty">{tr('你的下一段灵感，从这里开始。', 'Your next idea starts here.')}</p> : null}
              {conv.conversations.length > 0 && !filtered.length ? <p className="history-empty">{tr('没有匹配的对话。', 'No matching conversations.')}</p> : null}
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
              {conv.runsIn(entry.dataDirectory).length
                ? <span className="run-badge" title={tr('此工作区有会话正在运行', 'A conversation in this workspace is running')}>{tr(`${conv.runsIn(entry.dataDirectory).length} 运行中`, `${conv.runsIn(entry.dataDirectory).length} running`)}</span>
                : null}
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
                >{tr('切换到此工作区', 'Switch to this workspace')}</Button>
                <span>{tr(`${entry.sessionCount} 个会话`, `${entry.sessionCount} conversations`)}</span>
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
                onClick={() => { void conv.selectConversation(item.id).then(() => go('chat')).catch(notices.report) }}
                variant="ghost"
              ><span>{item.title}</span></Button>
              <RunMarker state={conv.runState(item.id)} />
              <span className="history-time">{formatRelative(item.updatedAt)}</span>
            </div>)}
            {!conv.conversations.length ? <p className="history-empty">{tr('你的下一段灵感，从这里开始。', 'Your next idea starts here.')}</p> : null}
          </div> : null}
        </div>
      </div>

      <div className="sidebar-footer">
        <StatusDot animate={connection === 'connecting'} size="sm" tone={connectionTone} />
        <div><strong>{connectionLabel(connection)}</strong><small>{workspace.available
          ? tr(`工作区 ${workspaceName || '未设置'}`, `Workspace ${workspaceName || 'not set'}`)
          : mcp.connectedIds.length ? tr(`${mcp.connectedIds.length} 个 MCP 服务已连接`, `${mcp.connectedIds.length} MCP servers connected`) : tr('本地工作空间 · 数据不出本机', 'Local workspace · data stays on this machine')}</small></div>
        <Tooltip content={tr('工作区设置', 'Workspace settings')}><Button iconLabel={tr('工作区设置', 'Workspace settings')} onClick={() => go('settings')} size="icon-sm" variant="ghost"><SettingsIcon size={14} /></Button></Tooltip>
      </div>
    </aside>

    <main className="main-column">
      <header className="topbar">
        <Button className="mobile-menu" iconLabel={tr('展开侧栏', 'Expand sidebar')} onClick={() => setSidebarOpen(!sidebarOpen)} size="icon-sm" variant="ghost"><PanelLeft size={16} /></Button>
        {workspace.available ? <button className="topbar-workspace" onClick={() => workspace.openSwitcher()} type="button">
          <FolderOpen size={13} /><span>{workspaceName || tr('工作区', 'Workspace')}</span>
        </button> : <span className="topbar-scope">{tr('工作空间', 'Workspace')}</span>}
        <ChevronRight className="breadcrumb-slash" size={13} />
        <strong>{navItems.find(item => item.id === page)?.id === 'chat' ? activeTitle : navItems.find(item => item.id === page)?.label ?? tr('对话', 'Chat')}</strong>
        <div className="topbar-right">
          <Tooltip content={tr('工作区文件 · Ctrl+Shift+E', 'Workspace files · Ctrl+Shift+E')}>
            <Button
              className={panels.open.files ? 'is-active' : undefined}
              iconLabel={tr('工作区文件', 'Workspace files')}
              onClick={() => panels.togglePanel('files')}
              size="icon-sm"
              variant="ghost"
            ><FolderTree size={14} /></Button>
          </Tooltip>
          <Tooltip content={gitBadge ? tr(`Git 管理 · ${gitBadge} 项更改 · Ctrl+Shift+G`, `Git · ${gitBadge} changes · Ctrl+Shift+G`) : tr('Git 管理 · Ctrl+Shift+G', 'Git · Ctrl+Shift+G')}>
            <Button
              className={panels.open.git ? 'is-active' : undefined}
              iconLabel={tr('Git 管理', 'Git')}
              onClick={() => panels.togglePanel('git')}
              size="icon-sm"
              variant="ghost"
            ><span className="topbar-icon-badge"><GitBranch size={14} />{gitBadge ? <i>{gitBadge > 99 ? '99+' : gitBadge}</i> : null}</span></Button>
          </Tooltip>
          <Tooltip content={tr('集成终端 · Ctrl+`', 'Integrated terminal · Ctrl+`')}>
            <Button
              className={panels.open.terminal ? 'is-active' : undefined}
              iconLabel={tr('集成终端', 'Integrated terminal')}
              onClick={() => {
                panels.togglePanel('terminal')
                if (panels.open.terminal) terminal.requestFocus()
              }}
              size="icon-sm"
              variant="ghost"
            ><TerminalIcon size={14} /></Button>
          </Tooltip>
          {contributions.panels.map(entry => {
            const Icon = pluginIcon(entry.icon)
            return <Tooltip content={entry.title} key={entry.key}>
              <Button
                className={panels.open[entry.key] ? 'is-active' : undefined}
                iconLabel={entry.title}
                onClick={() => panels.togglePanel(entry.key)}
                size="icon-sm"
                variant="ghost"
              ><Icon size={14} /></Button>
            </Tooltip>
          })}
          <Tooltip content={tr('命令面板 · Ctrl+Shift+P', 'Command palette · Ctrl+Shift+P')}>
            <Button iconLabel={tr('命令面板', 'Command palette')} onClick={() => setPalette(true)} size="icon-sm" variant="ghost"><Command size={14} /></Button>
          </Tooltip>
          {tasks.activeTasks.length ? <Button
            icon={<StatusDot animate size="sm" tone="active" />}
            onClick={() => go('tasks')}
            size="sm"
            variant="ghost"
          >{tr(`${tasks.activeTasks.length} 个任务`, `${tasks.activeTasks.length} tasks`)}</Button> : null}
          <Tooltip content={dark ? tr('切换到浅色', 'Switch to light') : tr('切换到深色', 'Switch to dark')}>
            <Button iconLabel={tr('切换主题', 'Switch theme')} onClick={cycleTheme} size="icon-sm" variant="ghost">{dark ? <Sun size={14} /> : <Moon size={14} />}</Button>
          </Tooltip>
          <Badge color={busy ? 'blue' : 'green'} size="sm" variant="dot">{busy ? tr('正在执行', 'Working') : tr('就绪', 'Ready')}</Badge>
        </div>
      </header>
      {connection === 'offline' ? <div className="connection-strip is-warning">
        <AlertTriangle size={14} />
        <span>{tr('未连接本地后端', 'Local backend not connected')}</span>
        <small>{tr('请在桌面应用中打开，或在终端运行 allagent 后使用 /web-ui 启动本地工作台。', 'Open it from the desktop app, or run allagent in a terminal and start the workspace with /web-ui.')}</small>
        <Button onClick={() => reconnect()} size="xs" variant="outline">{tr('重试连接', 'Retry connection')}</Button>
      </div> : null}
      {connection === 'connecting' && !reconnectStalled ? <div className="connection-strip">
        <StatusDot animate size="sm" tone="active" />
        <span>{tr('正在连接本地后端…', 'Connecting to the local backend…')}</span>
        <small>{tr('界面上的操作会自动排队等待。', 'Actions in the UI will queue automatically.')}</small>
      </div> : null}
      {connection === 'connecting' && reconnectStalled ? <div className="connection-strip is-warning">
        <AlertTriangle size={14} />
        <span>{tr('仍未连接上本地后端', 'Still not connected to the local backend')}</span>
        <small>{tr('后端重启后会换端口：请在终端查看 /web-ui 输出的地址，或点击重试。', 'The backend uses a new port after a restart: check the address printed by /web-ui, or retry.')}</small>
        <Button onClick={() => reconnect()} size="xs" variant="outline">{tr('重试连接', 'Retry connection')}</Button>
        <Button onClick={() => location.reload()} size="xs" variant="ghost">{tr('刷新页面', 'Reload page')}</Button>
      </div> : null}
      <UpdateBanner />
      {page.startsWith('plugin:') ? <PluginPage onNavigate={go} route={page} />
        : page === 'models' ? <Models />
        : page === 'skills' ? <Skills />
          : page === 'tools' ? <Tools />
            : page === 'powershell' ? <PowerShell />
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
      }}>{tr('保存', 'Save')}</Button>}
      onClose={() => setRename(null)}
      size="sm"
      title={tr('重命名对话', 'Rename conversation')}
    >
      <Field label={tr('对话名称', 'Conversation name')} onChange={setRenameValue} required value={renameValue} />
    </Modal> : null}
    {remove ? <Confirm
      onClose={() => setRemove(null)}
      onConfirm={() => conv.deleteConversation(remove.id)}
      title={tr(`删除「${remove.title}」？`, `Delete “${remove.title}”?`)}
      description={tr('这段对话及其历史记录将从工作区移除。', 'This conversation and its history are removed from the workspace.')}
    /> : null}
    <Notices />
    <RunDock />
    <WorkspacePanel />
    <GitPanel />
    <TerminalPanel />
    {contributions.panels.filter(entry => panels.open[entry.key]).map(entry => <PluginPanel entry={entry} key={entry.key} />)}
    <PendingInteractions />
    <WorkspaceSwitcher />
    <TaskDetailModal />
    <CommandPalette host={{ go, openPalette: () => setPalette(true) }} onClose={() => setPalette(false)} open={palette} />
  </div>
}
