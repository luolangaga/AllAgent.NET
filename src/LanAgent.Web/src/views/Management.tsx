import { useState, type ReactNode } from 'react'
import { Plus, Pencil, Trash2, Plug, Upload, CheckCircle2, Cpu, Blocks, Settings2 } from 'lucide-react'
import { Button } from '../components/ui/b-button'
import { Badge } from '../components/ui/badge'
import { Switch } from '../components/ui/b-switch'
import { Input } from '../components/ui/input'
import { Field, Picker, Modal, Confirm, Empty } from '../components/shared'
import { useStore } from '../lib/store'
import { useApiConfigsStore } from '../stores/apiConfigs'
import { useSkillsStore } from '../stores/skills'
import { useMcpServersStore } from '../stores/mcpServers'
import { useSettingsStore } from '../stores/settings'
import { invoke } from '../bridge'
import type { ApiConfig, McpServerConfig } from '../types'
import { AgentRoles } from './AgentRoles'

type Props = { report: (error: unknown) => void }
function useAction(report: Props['report']) {
  const [busy, setBusy] = useState(false)
  return { busy, run: async (action: () => Promise<unknown>) => { setBusy(true); try { await action(); return true } catch (e) { report(e); return false } finally { setBusy(false) } } }
}
function Page({ eyebrow, title, description, action, children }: { eyebrow: string; title: string; description: string; action?: ReactNode; children: ReactNode }) {
  return <section className="management"><div className="page-heading"><div><span className="eyebrow">{eyebrow}</span><h1>{title}</h1><p>{description}</p></div>{action}</div>{children}</section>
}
const providers = [
  { value: 'openai', label: 'OpenAI', url: 'https://api.openai.com/v1', model: '' },
  { value: 'anthropic', label: 'Anthropic', url: 'https://api.anthropic.com', model: '' },
  { value: 'gemini', label: 'Google Gemini', url: 'https://generativelanguage.googleapis.com', model: '' },
  { value: 'ollama', label: 'Ollama', url: 'http://localhost:11434/v1', model: '' }
]
const defaultConfig: ApiConfig = { id: '', providerId: 'openai', displayName: '', baseUrl: providers[0].url, apiKey: '', defaultModel: '', temperature: 0.7, maxTokens: 4096, isEnabled: true, thinkingEnabled: false, thinkingBudget: 8000 }
export function Models({ report }: Props) {
  const store = useStore(useApiConfigsStore)
  const action = useAction(report)
  const [editing, setEditing] = useState<ApiConfig | null>(null)
  const [remove, setRemove] = useState<string | null>(null)
  const update = (patch: Partial<ApiConfig>) => setEditing(c => c ? { ...c, ...patch } : c)
  return <Page eyebrow="MODEL CONNECTIONS" title="让合适的模型，加入工作。" description="连接云端模型或本地推理服务。每段对话都可以选择不同的配置。" action={<Button icon={<Plus size={16} />} onClick={() => setEditing({ ...defaultConfig })}>添加模型</Button>}>
    <div className="section-meta"><span>{store.configs.length} 个模型配置</span><span>{store.configs.filter(c => c.isEnabled).length} 个已启用</span></div>
    {!store.configs.length && <Empty title="连接你的第一个模型">添加 API 地址、密钥与模型名称，然后开始对话。</Empty>}
    <div className="resource-list">{store.configs.map(c => <article className="resource" key={c.id}><div className="resource-icon"><Cpu size={23} /></div><div className="resource-content"><div className="resource-title"><h3>{c.displayName}</h3><Badge>{c.providerId}</Badge>{!c.isEnabled && <Badge>已停用</Badge>}</div><p>{c.defaultModel}</p><small>{c.baseUrl}</small></div><div className="resource-actions"><Button variant="outline" size="sm" loading={store.testStatus[c.id] === 'testing'} disabled={action.busy} onClick={() => void action.run(() => store.testConnection(c.id))}>{store.testStatus[c.id] === 'ok' ? '连接成功' : store.testStatus[c.id] === 'error' ? '连接失败' : '测试连接'}</Button><Button variant="ghost" size="icon-sm" iconLabel="编辑模型" onClick={() => setEditing({ ...c })}><Pencil size={16} /></Button><Button variant="ghost" size="icon-sm" iconLabel="删除模型" onClick={() => setRemove(c.id)}><Trash2 size={16} /></Button></div></article>)}</div>
    {editing && <Modal title={editing.id ? '编辑模型配置' : '添加模型配置'} onClose={() => { if (!action.busy) setEditing(null) }} footer={<><Button variant="outline" disabled={action.busy} onClick={() => setEditing(null)}>取消</Button><Button loading={action.busy} disabled={!editing.displayName.trim() || !editing.baseUrl.trim() || !editing.defaultModel.trim() || editing.maxTokens <= 0 || editing.thinkingBudget < 512} onClick={() => void action.run(() => store.save({ ...editing, id: editing.id || undefined })).then(ok => { if (ok) setEditing(null) })}>保存配置</Button></>}>
      <label className="field"><span>提供商</span><Picker label="提供商" value={editing.providerId} options={providers} onChange={providerId => { const p = providers.find(p => p.value === providerId)!; update({ providerId, baseUrl: p.url }) }} /></label>
      <Field label="配置名称" value={editing.displayName} onChange={displayName => update({ displayName })} required />
      <Field label="API 地址" value={editing.baseUrl} onChange={baseUrl => update({ baseUrl })} required />
      <Field label="API Key" type="password" value={editing.apiKey} onChange={apiKey => update({ apiKey })} />
      <Field label="模型名称" value={editing.defaultModel} onChange={defaultModel => update({ defaultModel })} required />
      <div className="field-pair"><Field label="Temperature（0–2）" type="number" value={editing.temperature} onChange={v => update({ temperature: Number(v) })} /><Field label="最大输出 Tokens" type="number" value={editing.maxTokens} onChange={v => update({ maxTokens: Number(v) })} /></div>
      <Field label="额外请求头（JSON，可选）" value={editing.extraHeadersJson ?? ''} onChange={extraHeadersJson => update({ extraHeadersJson })} />
      <div className="setting-row"><span>启用此模型</span><Switch checked={editing.isEnabled} onCheckedChange={isEnabled => update({ isEnabled })} aria-label="启用此模型" /></div>
      <div className="setting-row"><span>默认深度思考</span><Switch checked={editing.thinkingEnabled} onCheckedChange={thinkingEnabled => update({ thinkingEnabled })} aria-label="默认深度思考" /></div>
      {editing.thinkingEnabled && <Field label="思考预算（512–32000）" type="number" value={editing.thinkingBudget} onChange={v => update({ thinkingBudget: Number(v) })} />}
    </Modal>}
    {remove && <Confirm title="删除模型配置？" onClose={() => setRemove(null)} onConfirm={async () => { if (!await action.run(() => store.remove(remove))) throw new Error('删除失败') }} />}
  </Page>
}
export function Skills({ report }: Props) {
  const store = useStore(useSkillsStore)
  const action = useAction(report)
  const [query, setQuery] = useState('')
  const [importing, setImporting] = useState(false)
  const [json, setJson] = useState('')
  const [remove, setRemove] = useState<string | null>(null)
  async function importFile(file?: File) {
    if (!file) return
    await action.run(async () => {
      if (file.name.toLowerCase().endsWith('.zip')) {
        const bytes = new Uint8Array(await file.arrayBuffer()); let binary = ''; for (const byte of bytes) binary += String.fromCharCode(byte)
        await invoke('skills.importZip', { base64Zip: btoa(binary) }); await store.loadAll()
      } else await store.importFromJson(await file.text())
    })
  }
  return <Page eyebrow="AGENT CAPABILITIES" title="为想法，配上行动力。" description="管理 Agent 可以使用的技能。启用的技能会在后端执行时加载。" action={<Button icon={<Upload size={16} />} onClick={() => setImporting(true)}>导入技能</Button>}>
    <div className="section-meta"><Input aria-label="搜索技能" placeholder="搜索技能名称或描述…" value={query} onValueChange={setQuery} /><span>{store.skills.filter(s => s.isEnabled).length} 个已启用</span></div>
    {!store.skills.length && <Empty title="暂时没有技能">桌面后端会加载内置技能，你也可以导入 JSON 或 ZIP 技能包。</Empty>}
    <div className="resource-list">{store.skills.filter(s => `${s.name} ${s.description}`.toLowerCase().includes(query.toLowerCase())).map(s => <article key={s.id} className="resource"><div className="resource-icon"><Blocks size={22} /></div><div className="resource-content"><div className="resource-title"><h3>{s.name}</h3><Badge>{s.type}</Badge></div><p>{s.description}</p><details><summary>参数定义</summary><pre>{s.parametersJson}</pre></details></div><div className="resource-actions"><Switch checked={s.isEnabled} disabled={action.busy} onCheckedChange={enabled => void action.run(() => store.toggle(s.id, enabled))} aria-label={`启用 ${s.name}`} /><Button variant="ghost" size="icon-sm" iconLabel={`删除 ${s.name}`} onClick={() => setRemove(s.id)}><Trash2 size={16} /></Button></div></article>)}</div>
    {importing && <Modal title="导入技能" description="粘贴技能 JSON，或选择 JSON / ZIP 文件。" onClose={() => setImporting(false)} footer={<Button loading={action.busy} disabled={!json.trim()} onClick={() => void action.run(() => store.importFromJson(json)).then(ok => { if (ok) { setImporting(false); setJson('') } })}>导入 JSON</Button>}><label className="field"><span>技能定义</span><textarea aria-label="技能 JSON" className="text-area" rows={8} value={json} onChange={e => setJson(e.target.value)} placeholder={'{"name": "my_skill", "description": "…"}'} /></label><label className="file-import">选择技能文件<input type="file" accept=".json,.zip" disabled={action.busy} onChange={e => void importFile(e.target.files?.[0])} /></label></Modal>}
    {remove && <Confirm title="删除这个技能？" onClose={() => setRemove(null)} onConfirm={async () => { if (!await action.run(() => store.remove(remove))) throw new Error('删除失败') }} />}
  </Page>
}
export function Mcp({ report }: Props) {
  const store = useStore(useMcpServersStore)
  const action = useAction(report)
  const [editing, setEditing] = useState<McpServerConfig | null>(null)
  const [remove, setRemove] = useState<string | null>(null)
  const [connected, setConnected] = useState<string[]>([])
  const update = (patch: Partial<McpServerConfig>) => setEditing(c => c ? { ...c, ...patch } : c)
  return <Page eyebrow="CONNECTED TOOLS" title="把工具，连接到工作流。" description="添加 MCP 服务，让 Agent 使用外部工具与数据。" action={<Button icon={<Plus size={16} />} onClick={() => setEditing({ id: '', name: '', command: '', arguments: '', isEnabled: true, createdAt: '' })}>添加服务</Button>}>
    <div className="section-meta"><span>{store.servers.length} 个服务</span><span>Model Context Protocol</span></div>
    {!store.servers.length && <Empty title="连接你的工具生态">使用可执行命令和参数添加一个 MCP 服务。</Empty>}
    <div className="resource-list">{store.servers.map(s => <article className="resource" key={s.id}><div className="resource-icon"><Plug size={22} /></div><div className="resource-content"><div className="resource-title"><h3>{s.name}</h3><Badge>{connected.includes(s.id) ? '已连接' : s.isEnabled ? '已启用' : '已停用'}</Badge></div><p className="mono">{s.command} {s.arguments}</p></div><div className="resource-actions"><Button variant="outline" size="sm" loading={store.connectingIds.has(s.id)} disabled={!s.isEnabled || action.busy} onClick={() => void action.run(async () => { if (!await store.connect(s.id)) throw new Error('服务连接失败'); setConnected(ids => [...ids, s.id]) })}>连接</Button><Button variant="ghost" size="icon-sm" iconLabel="编辑服务" onClick={() => setEditing({ ...s })}><Pencil size={16} /></Button><Button variant="ghost" size="icon-sm" iconLabel="删除服务" onClick={() => setRemove(s.id)}><Trash2 size={16} /></Button></div></article>)}</div>
    {editing && <Modal title={editing.id ? '编辑 MCP 服务' : '添加 MCP 服务'} onClose={() => setEditing(null)} footer={<Button loading={action.busy} disabled={!editing.name.trim() || !editing.command.trim()} onClick={() => void action.run(() => store.save(editing)).then(ok => { if (ok) { setConnected(ids => ids.filter(id => id !== editing.id)); setEditing(null) } })}>保存服务</Button>}><Field label="服务名称" value={editing.name} onChange={name => update({ name })} required /><Field label="启动命令" value={editing.command} onChange={command => update({ command })} required /><Field label="命令参数" value={editing.arguments} onChange={v => update({ arguments: v })} /><div className="setting-row"><span>启用服务</span><Switch checked={editing.isEnabled} onCheckedChange={isEnabled => update({ isEnabled })} aria-label="启用服务" /></div></Modal>}
    {remove && <Confirm title="删除 MCP 服务？" onClose={() => setRemove(null)} onConfirm={async () => { if (!await action.run(() => store.remove(remove))) throw new Error('删除失败') }} />}
  </Page>
}
export function Preferences({ report }: Props) {
  const settings = useStore(useSettingsStore)
  const action = useAction(report)
  const [saved, setSaved] = useState(false)
  return <Page eyebrow="YOUR WORKSPACE" title="按你的习惯，开始工作。" description="调整外观和默认模型，设置会保存在桌面后端。" action={<Button loading={action.busy} icon={saved ? <CheckCircle2 size={16} /> : <Settings2 size={16} />} onClick={() => void action.run(() => settings.save()).then(ok => { setSaved(ok); setTimeout(() => setSaved(false), 2000) })}>{saved ? '已保存' : '保存设置'}</Button>}>
    <div className="settings-group"><h3>Browser Use</h3><div className="setting-row"><div><strong>浏览器操作</strong><p>默认开启，内置窗口使用系统 WebView2 Runtime。保存设置后生效；关闭会中止操作。</p></div><Switch checked={settings.browserUseEnabled} onCheckedChange={v => { settings.browserUseEnabled = v }} aria-label="Browser Use" /></div><Picker label="浏览器模式" value={settings.browserUseMode} onChange={v => { settings.browserUseMode = v as 'builtin' | 'chromium' }} options={[{ value: 'builtin', label: '内置浏览器 · 使用时自动打开窗口' }, { value: 'chromium', label: '外置 Chromium · 手动连接' }]} />{settings.browserUseMode === 'chromium' && <><p>终端输入 /browser connect 获取逐步连接向导；输入 /browser tutorial 查看详细教程。外置浏览器需手动启动并连接 CDP 地址。</p><Field label="本机 CDP 地址（例如 http://127.0.0.1:9222）" value={settings.browserCdpUrl} onChange={v => { settings.browserCdpUrl = v }} /><Field label="浏览器路径（用于手动启动，连接请填写 CDP）" value={settings.browserExecutablePath} onChange={v => { settings.browserExecutablePath = v }} /></>}</div>
    <div className="settings-group"><h3>外观</h3><div className="setting-row"><div><strong>主题</strong><p>浅色、深色，或跟随系统外观。</p></div><div className="setting-control"><Picker label="主题" value={settings.theme} onChange={v => { settings.theme = v as typeof settings.theme }} options={[{ value: 'light', label: '浅色' }, { value: 'dark', label: '深色' }, { value: 'system', label: '跟随系统' }]} /></div></div></div>
    <div className="settings-group"><h3>语言偏好</h3><div className="setting-control"><Picker label="语言偏好" value={settings.language} onChange={v => { settings.language = v as typeof settings.language }} options={[{ value: 'zh', label: '中文' }, { value: 'en', label: 'English' }]} /></div></div><div className="settings-group"><h3>模型默认值</h3><Field label="默认提供商" value={settings.defaultProviderId} onChange={v => { settings.defaultProviderId = v }} /><Field label="默认模型" value={settings.defaultModel} onChange={v => { settings.defaultModel = v }} /><div className="setting-row"><div><strong>流式输出</strong><p>保存后由后端应用此偏好。</p></div><Switch checked={settings.streamingEnabled} onCheckedChange={v => { settings.streamingEnabled = v }} aria-label="流式输出" /></div></div>
    <AgentRoles report={report} />
    <div className="about-strip"><div className="brand-symbol"><Blocks size={20} /></div><div><strong>LanAgent</strong><p>本地 Agent 工作空间 · 界面由 Iconiq UI 构建</p></div><Badge>React + .NET</Badge></div>
  </Page>
}

