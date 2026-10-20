import { useEffect, useState } from 'react'
import {
  ArrowUpCircle, BarChart3, Blocks, CheckCircle2, Compass, Copy, ExternalLink, FolderOpen, GitBranch,
  HardDrive, Info, Palette, QrCode, RefreshCw, Settings2, ShieldCheck, Sparkles, Users, Wifi,
  type LucideIcon,
} from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dropdown, DropdownContent, DropdownGroup, DropdownItem, DropdownTrigger, DropdownValue } from '@/components/ui/r-dropdown'
import { Separator } from '@/components/ui/b-separator'
import { Switch } from '@/components/ui/b-switch'
import { Field, Picker } from '@/components/shared'
import { PageShell } from '@/components/PageShell'
import { PluginSettingsSection } from '@/components/plugins/PluginSettingsSection'
import { ModelUsagePanel } from '@/components/usage/ModelUsagePanel'
import { invoke } from '@/bridge'
import { useStore } from '@/lib/store'
import { permissionOptions, permissionLabel } from '@/lib/permissions'
import { useT } from '@/i18n'
import { useApiConfigsStore } from '@/stores/apiConfigs'
import { useContributionsStore } from '@/stores/contributions'
import { useSettingsStore } from '@/stores/settings'
import { useNoticesStore } from '@/stores/ui'
import { useUpdateStore } from '@/stores/update'
import { useWorkspaceStore } from '@/stores/workspace'
import { AgentRoles } from './AgentRoles'
import type { AppSettings, UpdateSettings, WebUiNetworkInfo } from '@/types'

type SettingsCategoryId =
  | 'appearance'
  | 'conversation'
  | 'git'
  | 'browser'
  | 'execution'
  | 'workspace'
  | 'network'
  | 'updates'
  | 'usage'
  | 'agents'
  | 'plugins'
  | 'about'

type CategoryGroup = 'preferences' | 'runtime' | 'connectivity' | 'extensions'

interface SettingsCategory {
  id: SettingsCategoryId
  label: string
  description: string
  icon: LucideIcon
  group: CategoryGroup
  badge?: number
  dot?: boolean
}

const CATEGORY_KEY = 'allagent.settings.category'
const CATEGORY_IDS: SettingsCategoryId[] = [
  'appearance', 'conversation', 'git', 'browser', 'execution', 'workspace',
  'network', 'updates', 'usage', 'agents', 'plugins', 'about',
]

function readStoredCategory(): SettingsCategoryId {
  try {
    const stored = localStorage.getItem(CATEGORY_KEY)
    if (stored && CATEGORY_IDS.includes(stored as SettingsCategoryId)) return stored as SettingsCategoryId
  } catch {
    // Storage can be unavailable in locked-down webviews; fall back to the first category.
  }
  return 'appearance'
}

export default function Settings() {
  const tr = useT()
  const settings = useStore(useSettingsStore)
  const api = useStore(useApiConfigsStore)
  const notices = useStore(useNoticesStore)
  const workspace = useStore(useWorkspaceStore)
  const contributions = useStore(useContributionsStore)
  const update = useStore(useUpdateStore)
  const [category, setCategory] = useState<SettingsCategoryId>(readStoredCategory)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [network, setNetwork] = useState<WebUiNetworkInfo | null>(null)
  const [networkLan, setNetworkLan] = useState(false)
  const [networkPort, setNetworkPort] = useState('')
  const [networkPassword, setNetworkPassword] = useState('')
  const [clearNetworkPassword, setClearNetworkPassword] = useState(false)
  const [networkBusy, setNetworkBusy] = useState(false)

  useEffect(() => {
    void update.loadSettings()
    void update.load(false)
  }, [])

  useEffect(() => {
    let cancelled = false
    invoke<WebUiNetworkInfo>('webui.network.get')
      .then(value => {
        if (cancelled) return
        setNetwork(value)
        setNetworkLan(value.lan)
        setNetworkPort(String(value.port))
      })
      // Older hosts do not expose the bridge method; the card simply stays hidden.
      .catch(() => { })
    return () => { cancelled = true }
  }, [])

  function selectCategory(id: SettingsCategoryId) {
    setCategory(id)
    try { localStorage.setItem(CATEGORY_KEY, id) } catch { /* storage may be unavailable */ }
  }

  async function saveNetwork() {
    setNetworkBusy(true)
    try {
      const previous = network
      const next = await invoke<WebUiNetworkInfo>('webui.network.save', {
        lan: networkLan,
        port: Number(networkPort) || 0,
        password: clearNetworkPassword ? null : (networkPassword.trim() || null),
        clearPassword: clearNetworkPassword,
      })
      setNetwork(next)
      setNetworkPassword('')
      setClearNetworkPassword(false)
      if (next.lan && !next.passwordConfigured) {
        notices.notify(tr('局域网访问已开启', 'LAN access enabled'), tr('尚未设置访问密码：其他设备请扫描二维码进入。建议设置密码，方便没有二维码时登录。', 'No access password yet: other devices should scan the QR code. Setting one is recommended so they can sign in without it.'), 'warning')
      } else {
        notices.notify(tr('网络设置已保存', 'Network settings saved'), next.lan ? tr('其他设备可在同一局域网内访问。', 'Other devices on the same LAN can connect.') : tr('已恢复为仅本机访问。', 'Listening on this machine only again.'), 'success')
      }
      // A bind or port change restarts the listener shortly after this response. The local
      // window follows the new address; a remote page keeps its URL unless it still matches.
      if (previous && (previous.lan !== next.lan || previous.port !== next.port)) {
        const host = window.location.hostname
        const local = ['127.0.0.1', 'localhost', '::1', '[::1]'].includes(host)
        if (local) window.setTimeout(() => window.location.assign(next.localUrl), 1200)
      }
    } catch (error) {
      notices.report(error)
    } finally {
      setNetworkBusy(false)
    }
  }

  async function copyAccessUrl() {
    const value = network?.accessUrl ?? network?.url
    if (!value) return
    try {
      await navigator.clipboard.writeText(value)
      notices.notify(tr('链接已复制', 'Link copied'), tr('链接包含访问令牌，请只发送给信任的设备。', 'The link contains an access token; share it only with trusted devices.'), 'success')
    } catch (error) {
      notices.report(error)
    }
  }

  async function setPermission(mode: string) {
    try {
      await workspace.setPermission(mode)
      notices.notify(tr('权限模式已切换', 'Permission mode changed'), permissionLabel(mode), 'success')
    } catch (error) {
      notices.report(error)
    }
  }

  async function setSandbox(mode: string) {
    try {
      await workspace.setSandbox(mode)
      notices.notify(tr('沙箱选择已保存', 'Sandbox choice saved'), mode, 'success')
    } catch (error) {
      notices.report(error)
    }
  }

  async function saveUpdateSettings(patch: Partial<UpdateSettings>) {
    update.settings = { ...update.settings, ...patch }
    try {
      await update.saveSettings()
    } catch (error) {
      notices.report(error)
    }
  }

  async function installUpdate() {
    try {
      const result = await update.install()
      notices.notify(
        result.started ? tr('更新已开始', 'Update started') : tr('未安装更新', 'Update not installed'),
        result.message,
        result.started ? 'success' : 'warning',
      )
    } catch (error) {
      notices.report(error)
    }
  }

  async function save() {
    setBusy(true)
    try {
      await settings.save()
      setSaved(true)
      window.setTimeout(() => setSaved(false), 2200)
      notices.notify(tr('设置已保存', 'Settings saved'), tr('全部偏好已写入本机。', 'All preferences were written to this machine.'), 'success')
    } catch (error) {
      notices.report(error)
    } finally {
      setBusy(false)
    }
  }

  // Empty strings mean "follow", but the select needs a real value to show its label.
  const providerOptions = [
    { value: 'follow', label: tr('跟随对话选择', 'Follow the composer') },
    ...api.configs.map(config => ({ value: config.id, label: `${config.displayName} · ${config.defaultModel}` })),
  ]
  // Git AI drafting: default is whatever model the composer currently has selected.
  const commitModelOptions = [
    { value: 'follow', label: tr('跟随当前选择的模型（推荐）', 'Follow the currently selected model (recommended)') },
    ...api.configs.filter(config => config.isEnabled).map(config => ({ value: config.id, label: `${config.displayName} · ${config.defaultModel}` })),
  ]
  const nugetInstall = update.status?.installKind === 'NuGet'
  const updateSourceOptions = nugetInstall
    ? [{ value: 'nuget', label: 'NuGet' }]
    : [
      { value: 'auto', label: tr('自动 · GitCode 优先，失败回退 GitHub', 'Auto · GitCode first, GitHub fallback') },
      { value: 'gitcode', label: 'GitCode' },
      { value: 'github', label: 'GitHub' },
    ]

  const categories: SettingsCategory[] = [
    {
      id: 'appearance', group: 'preferences', icon: Palette,
      label: tr('外观与语言', 'Appearance & language'),
      description: tr('主题、语言与界面显示偏好。', 'Theme, language and display preferences.'),
    },
    {
      id: 'conversation', group: 'preferences', icon: Sparkles,
      label: tr('对话默认值', 'Conversation defaults'),
      description: tr('新对话默认使用的模型、流式输出与上下文行为。', 'Default model, streaming and context behavior for new conversations.'),
    },
    {
      id: 'git', group: 'preferences', icon: GitBranch,
      label: tr('Git 提交信息', 'Git commit messages'),
      description: tr('AI 生成提交信息时使用的模型。', 'Model used when AI drafts commit messages.'),
    },
    {
      id: 'browser', group: 'runtime', icon: Compass,
      label: 'Browser Use',
      description: tr('内置浏览器或外置 Chromium 的连接方式。', 'Connect the built-in browser or an external Chromium instance.'),
    },
    {
      id: 'execution', group: 'runtime', icon: ShieldCheck,
      label: tr('权限与执行', 'Permissions & execution'),
      description: tr('工具授权模式与沙箱选择。', 'Tool approval mode and sandbox selection.'),
    },
    {
      id: 'workspace', group: 'runtime', icon: FolderOpen,
      label: tr('工作区', 'Workspace'),
      description: tr('当前工作区、数据目录与最近打开的项目。', 'Current workspace, data directory and recent projects.'),
    },
    {
      id: 'usage', group: 'runtime', icon: BarChart3,
      label: tr('模型用量', 'Model usage'),
      description: tr('各模型的累计 Token、每日趋势与全年活跃度。', 'Lifetime tokens per model, daily trends and a year of activity.'),
    },
    {
      id: 'network', group: 'connectivity', icon: Wifi,
      label: tr('局域网访问', 'LAN access'),
      description: tr('手机、平板与其他电脑的访问地址、密码和二维码。', 'Addresses, password and QR code for phones, tablets and other computers.'),
    },
    {
      id: 'updates', group: 'connectivity', icon: ArrowUpCircle,
      label: tr('更新', 'Updates'),
      description: tr('启动检查、自动安装、更新来源与签名校验。', 'Startup checks, automatic installs, sources and signature verification.'),
      dot: update.status?.updateAvailable === true,
    },
    {
      id: 'agents', group: 'extensions', icon: Users,
      label: tr('子代理角色', 'Child-agent roles'),
      description: tr('按角色设置 provider、模型、指令、工具与执行预算。', 'Provider, model, instructions, tools and budget per role.'),
    },
    {
      id: 'plugins', group: 'extensions', icon: Blocks,
      label: tr('插件设置', 'Plugin settings'),
      description: tr('插件注册的设置分组集中显示在这里。', 'Settings sections registered by plugins appear here.'),
      badge: contributions.settings.length || undefined,
    },
    {
      id: 'about', group: 'extensions', icon: Info,
      label: tr('关于', 'About'),
      description: tr('版本、运行环境与组件信息。', 'Version, runtime and component information.'),
    },
  ]
  const groups: { id: CategoryGroup; label: string }[] = [
    { id: 'preferences', label: tr('偏好', 'Preferences') },
    { id: 'runtime', label: tr('运行', 'Runtime') },
    { id: 'connectivity', label: tr('连接', 'Connectivity') },
    { id: 'extensions', label: tr('扩展与关于', 'Extensions & about') },
  ]
  const active = categories.find(item => item.id === category) ?? categories[0]
  const ActiveIcon = active.icon

  return <PageShell
    action={<Button
      icon={saved ? <CheckCircle2 size={16} /> : <Settings2 size={16} />}
      loading={busy}
      onClick={() => void save()}
    >{saved ? tr('已保存', 'Saved') : tr('保存设置', 'Save settings')}</Button>}
    description={tr('按分类浏览外观、模型、执行、网络与扩展设置。设置保存在本机工作区，并立刻生效。', 'Browse appearance, models, execution, network and extension settings by category. Settings are stored in the local workspace and apply immediately.')}
    eyebrow="YOUR WORKSPACE"
    title={tr('按你的习惯，开始工作。', 'Work the way you prefer.')}
  >
    <div className="settings-layout">
      <aside className="settings-rail">
        <div className="settings-rail-head">
          <span className="eyebrow">{tr('设置分类', 'SETTINGS')}</span>
          <Dropdown
            className="settings-category-dropdown"
            onValueChange={value => { if (value) selectCategory(value as SettingsCategoryId) }}
            value={category}
          >
            <DropdownTrigger className="settings-category-trigger">
              <DropdownValue placeholder={tr('选择分类', 'Choose a category')} />
            </DropdownTrigger>
            <DropdownContent className="w-full">
              {groups.map(group => <DropdownGroup key={group.id} label={group.label}>
                {categories.filter(item => item.group === group.id).map(item => <DropdownItem key={item.id} value={item.id}>
                  <item.icon size={14} />
                  {item.label}
                  {item.badge ? <span className="settings-nav-badge">{item.badge}</span> : null}
                </DropdownItem>)}
              </DropdownGroup>)}
            </DropdownContent>
          </Dropdown>
        </div>
        <nav aria-label={tr('设置分类', 'Settings categories')} className="settings-nav">
          {groups.map(group => <div className="settings-nav-group" key={group.id}>
            <span className="settings-nav-label">{group.label}</span>
            {categories.filter(item => item.group === group.id).map(item => <button
              className={`settings-nav-item ${category === item.id ? 'is-active' : ''}`}
              key={item.id}
              onClick={() => selectCategory(item.id)}
              type="button"
            >
              <item.icon size={14} />
              <span>{item.label}</span>
              {item.badge ? <span className="settings-nav-badge">{item.badge}</span> : null}
              {item.dot ? <span className="settings-nav-dot" /> : null}
            </button>)}
          </div>)}
        </nav>
      </aside>

      <div className="settings-panel">
        <header className="settings-panel-head">
          <div className="settings-panel-icon"><ActiveIcon size={18} /></div>
          <div>
            <h2>{active.label}</h2>
            <p>{active.description}</p>
          </div>
        </header>

        <div className="settings-cards">
          {category === 'appearance' ? <Card>
            <CardHeader>
              <CardTitle>{tr('外观', 'Appearance')}</CardTitle>
              <CardDescription>{tr('浅色、深色，或跟随系统外观。', 'Light, dark, or follow the system.')}</CardDescription>
            </CardHeader>
            <CardContent className="settings-fields">
              <label className="field"><span>{tr('主题', 'Theme')}</span>
                <Picker
                  label={tr('主题', 'Theme')}
                  onChange={value => { settings.theme = value as AppSettings['theme']; settings.applyTheme(settings.theme) }}
                  options={[{ value: 'light', label: tr('浅色', 'Light') }, { value: 'dark', label: tr('深色', 'Dark') }, { value: 'system', label: tr('跟随系统', 'Follow system') }]}
                  value={settings.theme}
                />
              </label>
              <label className="field"><span>{tr('语言偏好', 'Language')}</span>
                <Picker
                  label={tr('语言偏好', 'Language')}
                  onChange={value => { void settings.setLanguage(value as AppSettings['language']).catch(notices.report) }}
                  options={[
                    { value: 'auto', label: tr('跟随系统', 'Follow system') },
                    { value: 'zh', label: '中文' },
                    { value: 'en', label: 'English' },
                  ]}
                  value={settings.language}
                />
              </label>
            </CardContent>
          </Card> : null}

          {category === 'conversation' ? <Card>
            <CardHeader>
              <CardTitle>{tr('对话默认值', 'Conversation defaults')}</CardTitle>
              <CardDescription>{tr('新对话默认使用的模型与流式输出偏好。', 'Default model and streaming preference for new conversations.')}</CardDescription>
            </CardHeader>
            <CardContent className="settings-fields">
              <label className="field"><span>{tr('默认提供商与模型', 'Default provider and model')}</span>
                <Picker
                  label={tr('默认提供商', 'Default provider')}
                  onChange={value => { settings.defaultProviderId = value === 'follow' ? '' : value }}
                  options={providerOptions}
                  value={settings.defaultProviderId || 'follow'}
                />
              </label>
              <Field label={tr('默认模型名称（可留空，跟随配置）', 'Default model name (leave empty to follow the configuration)')} onChange={value => { settings.defaultModel = value }} placeholder={tr('例如 gpt-5.1', 'for example gpt-5.1')} value={settings.defaultModel} />
              <div className="setting-row"><div>
                <strong>{tr('流式输出', 'Streaming output')}</strong>
                <p>{tr('逐字显示模型输出，并实时渲染工具执行轨迹。', 'Show model output token by token and render tool activity live.')}</p>
              </div>
                <Switch aria-label={tr('流式输出', 'Streaming output')} checked={settings.streamingEnabled} onCheckedChange={value => { settings.streamingEnabled = value }} size="sm" />
              </div>
            </CardContent>
          </Card> : null}

          {category === 'git' ? <Card>
            <CardHeader>
              <CardTitle>{tr('Git 提交信息', 'Git commit messages')}</CardTitle>
              <CardDescription>{tr('AI 生成提交信息时使用的模型。默认跟随输入框当前选择的模型；保存后立即生效。', 'Model used for AI commit messages. Follows the model selected in the composer by default; applies immediately after saving.')}</CardDescription>
            </CardHeader>
            <CardContent className="settings-fields">
              <label className="field"><span>{tr('AI 生成提交信息使用的模型', 'Model for AI commit messages')}</span>
                <Picker
                  label={tr('AI 提交信息模型', 'AI commit-message model')}
                  onChange={value => { settings.gitCommitConfigId = value === 'follow' ? '' : value }}
                  options={commitModelOptions}
                  value={settings.gitCommitConfigId || 'follow'}
                />
              </label>
              <p className="list-description">{tr('「跟随当前选择的模型」会使用聊天输入框里正在使用的配置；也可在这里固定一个配置（例如专门用于提交信息的快模型）。', '“Follow the currently selected model” uses the configuration active in the composer; you can also pin a configuration here (for example a fast model for commit messages).')}</p>
            </CardContent>
          </Card> : null}

          {category === 'browser' ? <Card>
            <CardHeader>
              <CardTitle>Browser Use</CardTitle>
              <CardDescription>{tr('浏览器操作默认开启。内置模式使用系统 WebView2，外置模式手动连接 Chromium。', 'Browser use is enabled by default. Built-in mode uses the system WebView2; external mode connects to Chromium manually.')}</CardDescription>
            </CardHeader>
            <CardContent className="settings-fields">
              <div className="setting-row"><div>
                <strong>{tr('浏览器操作', 'Browser use')}</strong>
                <p>{tr('保存设置后生效；关闭会中止正在进行的浏览器操作。', 'Applies after saving; disabling stops browser operations in progress.')}</p>
              </div>
                <Switch aria-label="Browser Use" checked={settings.browserUseEnabled} onCheckedChange={value => { settings.browserUseEnabled = value }} size="sm" />
              </div>
              <label className="field"><span>{tr('浏览器模式', 'Browser mode')}</span>
                <Picker
                  label={tr('浏览器模式', 'Browser mode')}
                  onChange={value => { settings.browserUseMode = value as AppSettings['browserUseMode'] }}
                  options={[
                    { value: 'builtin', label: tr('内置浏览器 · 使用时自动打开窗口', 'Built-in browser · opens automatically when used') },
                    { value: 'chromium', label: tr('外置 Chromium · 手动连接', 'External Chromium · connect manually') },
                  ]}
                  value={settings.browserUseMode}
                />
              </label>
              {settings.browserUseMode === 'chromium' ? <>
                <Field
                  description={tr('终端输入 /browser connect 获取逐步连接向导。', 'Run /browser connect in the terminal for a step-by-step guide.')}
                  label={tr('本机 CDP 地址', 'Local CDP address')}
                  onChange={value => { settings.browserCdpUrl = value }}
                  placeholder="http://127.0.0.1:9222"
                  value={settings.browserCdpUrl}
                />
                <Field
                  label={tr('浏览器路径（用于手动启动，连接请填写 CDP）', 'Browser path (for manual launch; use CDP for connecting)')}
                  onChange={value => { settings.browserExecutablePath = value }}
                  value={settings.browserExecutablePath}
                />
              </> : null}
            </CardContent>
          </Card> : null}

          {category === 'execution' ? (workspace.available && workspace.controls ? <Card>
            <CardHeader>
              <CardTitle>{tr('权限与执行', 'Permissions and execution')}</CardTitle>
              <CardDescription>{tr('与终端共享同一套权限模式；浏览器内的工具授权请求会直接弹出对话框。权限模式随时可切换，运行中的任务从下一次工具调用起采用新模式；沙箱切换需要任务结束后进行。', 'Shares the terminal permission mode; tool approvals open as dialogs in the browser. The permission mode can change at any time and applies from the next tool call; switching the sandbox requires tasks to finish.')}</CardDescription>
            </CardHeader>
            <CardContent className="settings-fields">
              <label className="field"><span>{tr('权限模式', 'Permission mode')}</span>
                <Picker
                  label={tr('权限模式', 'Permission mode')}
                  onChange={mode => void setPermission(mode)}
                  options={permissionOptions().map(option => ({ value: option.value, label: `${option.label} · ${option.hint}` }))}
                  value={workspace.controls.permissionMode}
                />
              </label>
              <label className="field"><span>{tr('沙箱', 'Sandbox')}</span>
                <Picker
                  disabled={workspace.controls.running}
                  label={tr('沙箱', 'Sandbox')}
                  onChange={mode => void setSandbox(mode)}
                  options={[
                    { value: 'auto', label: tr('auto · 优先 Hyperlight，失败回退 Monty', 'auto · prefer Hyperlight, fall back to Monty') },
                    { value: 'hyperlight', label: tr('hyperlight · 微虚拟机隔离', 'hyperlight · microVM isolation') },
                    { value: 'monty', label: tr('monty · 无需虚拟化', 'monty · no virtualization') },
                  ]}
                  value={workspace.controls.sandboxMode}
                />
              </label>
              <p className="list-description">
                {tr('当前：', 'Current: ')}{workspace.controls.permissionLabel} · {tr('沙箱', 'sandbox')} {workspace.controls.sandboxEffective}
                {workspace.controls.running ? tr('（有任务运行中，沙箱暂不可切换）', ' (tasks are running; the sandbox cannot change yet)') : ''}
              </p>
            </CardContent>
          </Card> : <Card>
            <CardContent><p className="list-description">{tr('当前宿主不支持权限与沙箱设置。', 'This host does not expose permission or sandbox controls.')}</p></CardContent>
          </Card>) : null}

          {category === 'workspace' ? (workspace.available ? <Card>
            <CardHeader>
              <CardTitle>{tr('工作区', 'Workspace')}</CardTitle>
              <CardDescription>{tr('每个对话都属于一个工作区。切换后模型、会话、技能、任务会立即刷新，终端自己的工作区不受影响。', 'Every conversation belongs to a workspace. Switching refreshes models, conversations, skills and tasks immediately; the terminal workspace is unaffected.')}</CardDescription>
            </CardHeader>
            <CardContent className="settings-fields">
              <div className="workspace-line"><HardDrive size={14} /><span>{tr('当前工作区', 'Current workspace')}<b className="mono">{workspace.workspace || tr('未知', 'unknown')}</b></span></div>
              <div className="workspace-line"><FolderOpen size={14} /><span>{tr('数据目录', 'Data directory')}<b className="mono">{workspace.dataDirectory || tr('未知', 'unknown')}</b></span></div>
              <div className="workspace-line"><Blocks size={14} /><span>{tr('共享目录', 'Shared directory')}<b className="mono">{workspace.sharedDirectory || tr('未知', 'unknown')}</b></span></div>
              <div className="setting-row"><div>
                <strong>{tr('最近打开的工作区', 'Recent workspaces')}</strong>
                <p>{tr('选择一个工作区即可切换；会话按工作区归类，不会混在一起。', 'Pick a workspace to switch; conversations are grouped by workspace and never mixed.')}</p>
              </div>
                <Button icon={<FolderOpen size={14} />} onClick={() => workspace.openSwitcher()} size="sm" variant="outline">{tr('打开 / 新建', 'Open / New')}</Button>
              </div>
              <div className="workspace-list">{workspace.list.map(entry => <div className="workspace-entry-row" key={entry.workspace}>
                <div className="workspace-entry-copy">
                  <strong>{entry.name}{entry.isCurrent ? <Badge size="sm" variant="dot">{tr('当前', 'Current')}</Badge> : null}</strong>
                  <span className="mono">{entry.workspace}</span>
                </div>
                <Badge color="gray" size="sm" variant="dot">{tr(`${entry.sessionCount} 个会话`, `${entry.sessionCount} conversations`)}</Badge>
                <Button
                  disabled={workspace.switching || entry.isCurrent}
                  onClick={() => void workspace.switchTo(entry.workspace).then(() => notices.notify(tr('工作区已切换', 'Workspace switched'), entry.name, 'success')).catch(notices.report)}
                  size="sm"
                  variant="outline"
                >{entry.isCurrent ? tr('已打开', 'Open') : tr('切换', 'Switch')}</Button>
              </div>)}</div>
            </CardContent>
          </Card> : <Card>
            <CardContent><p className="list-description">{tr('当前宿主没有可切换的工作区。', 'This host has no switchable workspace.')}</p></CardContent>
          </Card>) : null}

          {category === 'usage' ? <ModelUsagePanel /> : null}

          {category === 'network' ? (network ? <Card>
            <CardHeader>
              <CardTitle>{tr('局域网访问与二维码', 'LAN access and QR code')}</CardTitle>
              <CardDescription>{tr('开启后监听 0.0.0.0，同一局域网中的手机、平板或其他电脑即可访问。本机始终免密；其他设备扫码进入，或使用访问密码。首次开启时 Windows 防火墙可能询问，请允许「专用网络」。', 'When enabled, the server listens on 0.0.0.0 so phones, tablets and other computers on the same LAN can connect. This machine never needs a password; other devices scan the QR code or use the access password. Windows Firewall may ask the first time; allow private networks.')}</CardDescription>
            </CardHeader>
            <CardContent className="settings-fields">
              <div className="setting-row"><div>
                <strong>{tr('允许局域网访问（监听 0.0.0.0）', 'Allow LAN access (listen on 0.0.0.0)')}</strong>
                <p>{tr('关闭时仅监听 127.0.0.1。修改监听地址或端口会重启服务，当前页面可能在约 1 秒后重连。', 'When off, the server listens on 127.0.0.1 only. Changing the address or port restarts the listener; the page may reconnect after about a second.')}</p>
              </div>
                <Switch aria-label={tr('允许局域网访问', 'Allow LAN access')} checked={networkLan} onCheckedChange={setNetworkLan} size="sm" />
              </div>
              <div className="field-pair">
                <label className="field"><span>{tr('监听端口', 'Listener port')}</span>
                  <Field
                    label={tr('监听端口', 'Listener port')}
                    onChange={value => setNetworkPort(value.replace(/[^0-9]/g, ''))}
                    placeholder={tr('留空自动选择', 'Empty for automatic')}
                    value={networkPort}
                  />
                </label>
                <label className="field"><span>{tr('访问密码（可选）', 'Access password (optional)')}</span>
                  <Field
                    label={tr('访问密码', 'Access password')}
                    onChange={setNetworkPassword}
                    placeholder={network.passwordConfigured ? tr('已设置 · 留空保持不变', 'Set · leave empty to keep') : tr('建议设置一个密码', 'Setting one is recommended')}
                    type="password"
                    value={networkPassword}
                  />
                </label>
              </div>
              {network.passwordConfigured ? <div className="setting-row"><div>
                <strong>{tr('清除访问密码', 'Clear access password')}</strong>
                <p>{tr('清除后其他设备只能通过二维码进入。', 'After clearing it, other devices can enter only through the QR code.')}</p>
              </div>
                <Switch aria-label={tr('清除访问密码', 'Clear access password')} checked={clearNetworkPassword} onCheckedChange={setClearNetworkPassword} size="sm" />
              </div> : null}
              <div className="webui-network-actions">
                <Button icon={<Wifi size={14} />} loading={networkBusy} onClick={() => void saveNetwork()} size="sm">{tr('保存网络设置', 'Save network settings')}</Button>
                {network.passwordConfigured
                  ? <Badge size="sm" variant="dot"><ShieldCheck size={12} />{tr('密码已设置', 'Password set')}</Badge>
                  : <Badge color="gray" size="sm" variant="dot">{tr('未设置密码', 'No password')}</Badge>}
              </div>
              {network.lan ? <>
                <div className="webui-network-urls">
                  <span>{tr('其他设备访问地址', 'Addresses for other devices')}</span>
                  <div>{network.urls.map(url => <code key={url}>{url}</code>)}</div>
                </div>
                {network.qrDataUrl ? <div className="webui-qr-wrap">
                  <img alt={tr('局域网访问二维码', 'LAN access QR code')} className="webui-qr" src={network.qrDataUrl} />
                  <div className="webui-qr-copy">
                    <strong><QrCode size={14} />{tr('扫码直接进入', 'Scan to open')}</strong>
                    <p>{tr('二维码包含本次运行的访问令牌，手机扫描后无需再输入密码；服务重启后令牌失效。', 'The QR code contains this run’s access token, so a phone opens without typing the password. The token expires when the service restarts.')}</p>
                    {!network.passwordConfigured ? <p className="webui-warning">{tr('建议设置访问密码：否则没有二维码的设备无法进入。', 'Set an access password: without it, a device without the QR code cannot sign in.')}</p> : null}
                    <div className="webui-network-actions">
                      <Button icon={<Copy size={14} />} onClick={() => void copyAccessUrl()} size="sm" variant="outline">{tr('复制带令牌链接', 'Copy link with token')}</Button>
                      <Button icon={<ExternalLink size={14} />} onClick={() => window.open(network.accessUrl ?? network.url, '_blank', 'noopener')} size="sm" variant="outline">{tr('打开', 'Open')}</Button>
                    </div>
                  </div>
                </div> : null}
              </> : <p className="list-description">{tr('本机访问始终免密，无需设置密码。需要其他设备访问时，打开上方开关即可。', 'This machine never needs a password. Turn on the switch above when another device should connect.')}</p>}
            </CardContent>
          </Card> : <Card>
            <CardContent><p className="list-description">{tr('当前宿主未提供网络设置接口。', 'This host does not expose network settings.')}</p></CardContent>
          </Card>) : null}

          {category === 'updates' ? <Card>
            <CardHeader>
              <CardTitle>{tr('更新', 'Updates')}</CardTitle>
              <CardDescription>{tr('NuGet 安装从 NuGet 更新；Windows 安装包优先 GitCode，连不上时回退 GitHub。下载后校验 SHA256 与 Authenticode 签名；安装失败时会尝试回滚到上一版本。', 'NuGet installs update from NuGet; the Windows installer prefers GitCode and falls back to GitHub. Downloads verify SHA256 and Authenticode signatures; a failed install attempts to roll back to the previous version.')}</CardDescription>
            </CardHeader>
            <CardContent className="settings-fields">
              <div className="setting-row"><div>
                <strong>{tr('启动时检查更新', 'Check for updates at startup')}</strong>
                <p>{tr('在后台检查一次，并在终端底部或 Web 顶部提示新版本。', 'Check once in the background and show a notice in the terminal footer or at the top of the web workspace.')}</p>
              </div>
                <Switch aria-label={tr('启动时检查更新', 'Check for updates at startup')} checked={update.settings.autoCheck} onCheckedChange={value => void saveUpdateSettings({ autoCheck: value })} size="sm" />
              </div>
              <div className="setting-row"><div>
                <strong>{tr('自动安装', 'Install automatically')}</strong>
                <p>{tr('发现新版本后立即静默安装并重启；默认关闭，建议只对受信任的发布源开启。', 'Install silently and restart as soon as an update is found. Off by default; enable it only for trusted release sources.')}</p>
              </div>
                <Switch aria-label={tr('自动安装', 'Install automatically')} checked={update.settings.autoInstall} onCheckedChange={value => void saveUpdateSettings({ autoInstall: value })} size="sm" />
              </div>
              <div className="setting-row"><div>
                <strong>{tr('要求 Authenticode 签名', 'Require Authenticode signature')}</strong>
                <p>{tr('开启后拒绝未签名的安装包；当前未签名的构建会更新失败。', 'Reject unsigned installers. Unsigned builds will fail to update while this is on.')}</p>
              </div>
                <Switch aria-label={tr('要求 Authenticode 签名', 'Require Authenticode signature')} checked={update.settings.requireSignature} onCheckedChange={value => void saveUpdateSettings({ requireSignature: value })} size="sm" />
              </div>
              <div className="field-pair">
                <label className="field"><span>{tr('更新来源', 'Update source')}</span>
                  <Picker
                    label={tr('更新来源', 'Update source')}
                    onChange={value => void saveUpdateSettings({ source: value })}
                    options={updateSourceOptions}
                    value={nugetInstall ? 'nuget' : update.settings.source}
                  />
                </label>
                <label className="field"><span>{tr('更新通道', 'Update channel')}</span>
                  <Picker
                    label={tr('更新通道', 'Update channel')}
                    onChange={value => void saveUpdateSettings({ channel: value })}
                    options={[{ value: 'stable', label: tr('稳定版', 'Stable') }, { value: 'pre', label: tr('预发布版', 'Preview') }]}
                    value={update.settings.channel}
                  />
                </label>
              </div>
              <div className="webui-network-actions">
                <Button icon={<RefreshCw size={14} />} loading={update.loading} onClick={() => void update.load(true)} size="sm">{tr('检查更新', 'Check for updates')}</Button>
                {update.status?.updateAvailable ? <Button icon={<ArrowUpCircle size={14} />} loading={update.installing} onClick={() => void installUpdate()} size="sm">{tr(`更新到 v${update.status.latestVersion}`, `Update to v${update.status.latestVersion}`)}</Button> : null}
                {update.status ? <Badge color={update.status.updateAvailable ? 'blue' : 'green'} size="sm" variant="dot">{update.status.updateAvailable ? tr('有可用更新', 'Update available') : tr('已是最新', 'Up to date')}</Badge> : null}
              </div>
              <p className="list-description">
                {tr(
                  `当前 v${update.status?.currentVersion ?? '—'} · ${update.status?.installKind ?? '—'} · ${update.status?.architecture ?? '—'}${(update.status?.error ?? update.status?.lastError) ? ' · 错误：' + (update.status?.error ?? update.status?.lastError) : ''}`,
                  `Current v${update.status?.currentVersion ?? '—'} · ${update.status?.installKind ?? '—'} · ${update.status?.architecture ?? '—'}${(update.status?.error ?? update.status?.lastError) ? ' · error: ' + (update.status?.error ?? update.status?.lastError) : ''}`,
                )}
              </p>
            </CardContent>
          </Card> : null}

          {category === 'agents' ? <Card>
            <CardHeader>
              <CardTitle>{tr('子代理角色', 'Child-agent roles')}</CardTitle>
              <CardDescription>{tr('按角色设置 provider、model、instructions、tools 与执行预算。留空的模型继承默认配置，工具权限仍受宿主限制。', 'Set provider, model, instructions, tools and execution budget per role. Empty models inherit the default configuration; tool permissions remain host-controlled.')}</CardDescription>
            </CardHeader>
            <CardContent>
              <AgentRoles />
            </CardContent>
          </Card> : null}

          {category === 'plugins' ? (contributions.settings.length
            ? contributions.settings.map(entry => <PluginSettingsSection
              key={`${entry.plugin.packageId}:${entry.section.id}`}
              plugin={entry.plugin}
              section={entry.section}
            />)
            : <Card>
              <CardContent><p className="list-description">{tr('当前没有插件提供设置项。安装或启用带设置页的插件后，它们会出现在这里。', 'No plugin exposes settings right now. Install or enable a plugin with a settings section and it will appear here.')}</p></CardContent>
            </Card>) : null}

          {category === 'about' ? <Card className="about-card">
            <CardContent className="about-row">
              <div className="brand-symbol"><Blocks size={20} /></div>
              <div className="about-copy">
                <strong>AllAgent.NET</strong>
                <p>{tr('本地 Agent 工作空间 · 终端与桌面共用同一套界面', 'Local agent workspace · terminal and desktop share one UI')}</p>
              </div>
              <Separator orientation="vertical" spacing="none" className="about-separator" />
              <div className="about-badges">
                <Badge size="sm" variant="dot">React 19</Badge>
                <Badge size="sm" variant="dot">.NET 10</Badge>
                <Badge size="sm" variant="dot">Iconiq UI</Badge>
              </div>
            </CardContent>
          </Card> : null}
        </div>
      </div>
    </div>
  </PageShell>
}
