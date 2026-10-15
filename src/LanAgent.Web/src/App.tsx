import { useEffect, useState } from 'react'
import { MessageSquare, Plus, Blocks, Plug, Settings, SlidersHorizontal, PanelLeft, Pencil, Trash2, ArrowUpRight, Command } from 'lucide-react'
import { Button } from './components/ui/b-button'
import { Input } from './components/ui/input'
import { Badge } from './components/ui/badge'
import { Tooltip } from './components/ui/b-tooltip'
import { Alert, AlertTitle, AlertDescription } from './components/ui/alert'
import { Confirm, Modal, Field } from './components/shared'
import { useStore } from './lib/store'
import { useConversationsStore } from './stores/conversations'
import { useApiConfigsStore } from './stores/apiConfigs'
import { useSkillsStore } from './stores/skills'
import { useMcpServersStore } from './stores/mcpServers'
import { useSettingsStore } from './stores/settings'
import Chat from './views/Chat'
import { Models, Skills, Mcp, Preferences } from './views/Management'
import { isNativeHost } from './bridge'

const navigation = [
  { id: 'chat', label: '对话', icon: MessageSquare },
  { id: 'api-configs', label: '模型与 API', icon: SlidersHorizontal },
  { id: 'skills', label: '技能', icon: Blocks },
  { id: 'mcp', label: 'MCP 服务', icon: Plug },
  { id: 'settings', label: '设置', icon: Settings }
]
export default function App() {
  const conv = useStore(useConversationsStore)
  const settings = useStore(useSettingsStore)
  const [page, setPage] = useState(location.hash.split('/')[1] || 'chat')
  const [sidebar, setSidebar] = useState(false)
  const [query, setQuery] = useState('')
  const [notice, setNotice] = useState('')
  const [rename, setRename] = useState<string | null>(null)
  const [title, setTitle] = useState('')
  const [remove, setRemove] = useState<string | null>(null)
  const [ready, setReady] = useState(false)
  async function run(action: () => Promise<unknown>) { try { await action() } catch (e) { setNotice(e instanceof Error ? e.message : String(e)); throw e } }
  function go(id: string) { location.hash = `/${id}`; setPage(id); setSidebar(false) }
  async function newChat() { if (conv.streamingMessageId) return; await run(() => conv.createConversation()); go('chat') }
  useEffect(() => {
    const change = () => setPage(location.hash.split('/')[1] || 'chat')
    window.addEventListener('hashchange', change)
    Promise.allSettled([conv.loadAll(), useApiConfigsStore().loadAll(), useSkillsStore().loadAll(), useMcpServersStore().loadAll(), settings.load()]).then(results => {
      const failures = results.filter(r => r.status === 'rejected') as PromiseRejectedResult[]
      if (failures.length) setNotice(failures.map(r => String(r.reason)).join('；'))
      setReady(true)
    })
    return () => window.removeEventListener('hashchange', change)
  }, [])
  useEffect(() => { settings.applyTheme(settings.theme) }, [settings.theme])
  useEffect(() => {
    const key = (e: KeyboardEvent) => { if ((e.ctrlKey || e.metaKey) && e.key === 'n') { e.preventDefault(); void newChat().catch(() => {}) } }
    window.addEventListener('keydown', key); return () => window.removeEventListener('keydown', key)
  })
  const report = (error: unknown) => setNotice(error instanceof Error ? error.message : String(error))
  return <div className="workspace">
    {sidebar && <div className="sidebar-scrim" onClick={() => setSidebar(false)} />}
    <aside className={`sidebar ${sidebar ? 'is-open' : ''}`}>
      <div className="brand"><div className="brand-symbol"><Command size={21} /></div><strong>LanAgent<span>你的智能工作伙伴</span></strong><Badge className="ml-auto" size="sm">BETA</Badge></div>
      <Button className="new-chat" icon={<Plus size={17} />} onClick={() => void newChat().catch(() => {})} disabled={!!conv.streamingMessageId}>新建对话<span className="key-hint">Ctrl N</span></Button>
      <nav aria-label="主导航">{navigation.map(item => <Button key={item.id} variant="ghost" className={`nav-link ${page === item.id ? 'selected' : ''}`} onClick={() => go(item.id)} icon={<item.icon size={17} />}>{item.label}{item.id === 'skills' && <span className="nav-count">{useSkillsStore().skills.length}</span>}</Button>)}</nav>
      <div className="history-label">最近对话<span>{conv.conversations.length}</span></div>
      <Input aria-label="搜索对话" type="search" placeholder="搜索对话…" value={query} onValueChange={setQuery} />
      <div className="history">{conv.conversations.filter(c => c.title.toLowerCase().includes(query.toLowerCase())).map(c => <div key={c.id} className={`history-row ${conv.activeId === c.id && page === 'chat' ? 'active' : ''}`}>
        <Button variant="ghost" className="history-title" disabled={!!conv.streamingMessageId} onClick={() => { void run(() => conv.selectConversation(c.id)).then(() => go('chat')).catch(() => {}) }}><MessageSquare size={14} /><span>{c.title}</span></Button>
        <div className="history-actions"><Tooltip content="重命名"><Button variant="ghost" size="icon-xs" iconLabel="重命名" disabled={!!conv.streamingMessageId} onClick={() => { setRename(c.id); setTitle(c.title) }}><Pencil size={12} /></Button></Tooltip><Tooltip content="删除对话"><Button variant="ghost" size="icon-xs" iconLabel="删除对话" disabled={!!conv.streamingMessageId} onClick={() => setRemove(c.id)}><Trash2 size={12} /></Button></Tooltip></div>
      </div>)}{!conv.conversations.length && <p className="history-empty">你的下一段灵感，从这里开始。</p>}</div>
      <div className="sidebar-footer"><span className={`status-dot ${isNativeHost() ? 'connected' : ''}`} /><div>{isNativeHost() ? '本地工作空间' : '浏览器预览'}<small>{isNativeHost() ? '已连接桌面后端' : '演示数据 · 未连接后端'}</small></div><Tooltip content="工作空间设置"><Button variant="ghost" size="icon-sm" iconLabel="工作空间设置" onClick={() => go('settings')}><ArrowUpRight size={16} /></Button></Tooltip></div>
    </aside>
    <main className="main-panel"><header className="topbar"><Button variant="ghost" size="icon-sm" iconLabel="展开侧栏" className="mobile-menu" onClick={() => setSidebar(!sidebar)}><PanelLeft size={18} /></Button><span>工作空间</span><span className="breadcrumb-slash">/</span><strong>{navigation.find(n => n.id === page)?.label ?? '对话'}</strong><div className="topbar-right"><span className="quiet-label">LOCAL AGENT</span><Badge size="sm">{conv.streamingMessageId ? '正在执行' : '就绪'}</Badge></div></header>
      {notice && <div className="notice"><Alert><AlertTitle>操作提示</AlertTitle><AlertDescription>{notice}</AlertDescription></Alert><Button variant="ghost" size="sm" onClick={() => setNotice('')}>关闭</Button></div>}
      {page === 'api-configs' ? <Models report={report} /> : page === 'skills' ? <Skills report={report} /> : page === 'mcp' ? <Mcp report={report} /> : page === 'settings' ? <Preferences report={report} /> : <Chat ready={ready} onConfigure={() => go('api-configs')} report={report} />}
    </main>
    {rename && <Modal title="重命名对话" onClose={() => setRename(null)} footer={<Button onClick={() => { if (title.trim()) void run(() => conv.renameConversation(rename, title.trim())).then(() => setRename(null)).catch(() => {}) }}>保存</Button>}><Field label="对话名称" value={title} onChange={setTitle} required /></Modal>}
    {remove && <Confirm title="删除这段对话？" onClose={() => setRemove(null)} onConfirm={() => run(() => conv.deleteConversation(remove))} />}
  </div>
}

