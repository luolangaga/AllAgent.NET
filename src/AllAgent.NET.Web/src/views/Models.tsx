import { useEffect, useState } from 'react'
import { Cpu, KeyRound, LogIn, Pencil, Plus, Trash2, Wand2 } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Card, CardAction, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Switch } from '@/components/ui/b-switch'
import { Confirm, Field, Modal, Picker, TextArea } from '@/components/shared'
import { EmptyState, PageShell } from '@/components/PageShell'
import { bridgeEvents } from '@/bridge'
import { formatTokens, providerLabel } from '@/lib/format'
import { useStore } from '@/lib/store'
import { useApiConfigsStore } from '@/stores/apiConfigs'
import { useAuthStore } from '@/stores/auth'
import { useNoticesStore } from '@/stores/ui'
import { usePluginsStore } from '@/stores/plugins'
import type { ApiConfig, AuthProviderInfo, ReasoningOptions } from '@/types'

const providers = [
  { value: 'openai', label: 'OpenAI', url: 'https://api.openai.com/v1' },
  { value: 'anthropic', label: 'Anthropic', url: 'https://api.anthropic.com' },
  { value: 'gemini', label: 'Google Gemini', url: 'https://generativelanguage.googleapis.com' },
  { value: 'ollama', label: 'Ollama', url: 'http://localhost:11434/v1' },
]

const defaultConfig: ApiConfig = {
  id: '', providerId: 'openai', displayName: '', baseUrl: providers[0].url, apiKey: '',
  defaultModel: '', temperature: 0.7, maxTokens: 0, isEnabled: true,
  thinkingEnabled: false, thinkingBudget: 8000,
  contextWindowTokens: 100000, protocol: '', reasoningProtocol: 'auto', reasoningEffort: 'default',
  models: [], apiKeyEnvironmentVariable: null, authenticationProviderId: null,
}

export default function Models() {
  const store = useStore(useApiConfigsStore)
  const plugins = useStore(usePluginsStore)
  const auth = useStore(useAuthStore)
  const notices = useStore(useNoticesStore)
  const [editing, setEditing] = useState<ApiConfig | null>(null)
  const [modelsText, setModelsText] = useState('')
  const [reasoning, setReasoning] = useState<ReasoningOptions | null>(null)
  const [remove, setRemove] = useState<ApiConfig | null>(null)
  const [busy, setBusy] = useState(false)
  const [refreshing, setRefreshing] = useState(false)
  const [interactive, setInteractive] = useState<AuthProviderInfo | null>(null)
  const [secret, setSecret] = useState('')

  useEffect(() => { if (!auth.loaded) void auth.loadAll() }, [])
  // Login flows write account model catalogs into ApiConfigs; refresh the cards when they settle.
  useEffect(() => { bridgeEvents.on('auth.changed', () => { void store.loadAll().catch(() => {}) }) }, [])

  const update = (patch: Partial<ApiConfig>) => setEditing(current => (current ? { ...current, ...patch } : current))

  const protocolOptions = [
    { value: '', label: '自动（按提供商推断）' },
    { value: 'openai', label: 'openai · Chat Completions' },
    { value: 'responses', label: 'responses · OpenAI Responses' },
    { value: 'anthropic', label: 'anthropic' },
    { value: 'gemini', label: 'gemini' },
    ...plugins.providers.map(id => ({ value: id, label: `插件 · ${id}` })),
  ]
  const authOptions = [
    { value: '', label: '不使用认证入口（直接用 API Key）' },
    ...plugins.authentication.map(auth => ({ value: auth.id, label: `${auth.name} · ${auth.id}` })),
  ]
  const effortOptions = (reasoning?.efforts ?? ['default']).map(effort => ({
    value: effort,
    label: effort === 'budget' ? `budget · 自定义预算` : effort === 'default' ? 'default · 模型默认' : effort,
  }))

  function openEditor(config: ApiConfig) {
    setEditing({ ...config })
    setModelsText((config.models ?? []).join('\n'))
    setReasoning(null)
    if (config.id) {
      void store.reasoningOptions(config.id).then(setReasoning).catch(() => setReasoning(null))
    }
  }

  // API-key presets in the account list open the matching config, or a prefilled new one.
  function openPreset(provider: AuthProviderInfo) {
    const existing = provider.configId ? store.configs.find(config => config.id === provider.configId) : undefined
    if (existing) { openEditor(existing); return }
    const preset = providers.find(item => item.value === provider.providerId)
    openEditor({ ...defaultConfig, providerId: provider.providerId ?? 'openai', baseUrl: preset?.url ?? defaultConfig.baseUrl })
  }

  async function run(action: () => Promise<unknown>) {
    setBusy(true)
    try {
      await action()
      return true
    } catch (error) {
      notices.report(error)
      return false
    } finally {
      setBusy(false)
    }
  }

  function savePayload(config: ApiConfig): Partial<ApiConfig> & { providerId: string; displayName: string; baseUrl: string; defaultModel: string } {
    return {
      ...config,
      id: config.id || undefined,
      models: modelsText.split('\n').map(line => line.trim()).filter(Boolean),
    }
  }

  async function refreshModels() {
    if (!editing?.id) return
    setRefreshing(true)
    try {
      const updated = await store.refreshModels(editing.id)
      setEditing(updated)
      setModelsText((updated.models ?? []).join('\n'))
      notices.notify('模型列表已更新', `${updated.models?.length ?? 0} 个模型 · 上下文 ${formatTokens(updated.effectiveContextTokens ?? updated.contextWindowTokens ?? 0)}`, 'success')
    } catch (error) {
      notices.report(error)
    } finally {
      setRefreshing(false)
    }
  }

  return <PageShell
    action={<Button icon={<Plus size={16} />} onClick={() => openEditor({ ...defaultConfig })}>添加模型</Button>}
    description="连接云端模型或本地推理服务。协议、模型列表、思考强度与上下文容量都与终端配置同步，密钥只保存在本机。"
    eyebrow="MODEL CONNECTIONS"
    title="让合适的模型，加入工作。"
  >
    <div className="section-meta">
      <span>{store.configs.length} 个配置 · {store.configs.filter(config => config.isEnabled).length} 个已启用</span>
      <span>OpenAI · Anthropic · Gemini · Ollama 兼容端点 · 支持插件提供商</span>
    </div>
    {auth.available ? <Card className="list-card account-card">
      <div className="list-row">
        <div className="resource-icon"><KeyRound size={20} /></div>
        <div className="list-content">
          <div className="resource-title">
            <h3>账号登录</h3>
            <Badge size="sm" variant="dot">{auth.providers.filter(provider => provider.loggedIn).length} 个可用</Badge>
          </div>
          <p className="list-description">与终端 /login 列表一致：OAuth 账号点击登录后在本机浏览器完成授权；API Key 型提供商直接填写密钥。登录成功后会自动接入账号模型目录。</p>
        </div>
      </div>
      <div className="divide-list">{auth.providers.map(provider => <div className="list-row" key={provider.id}>
        <div className="list-content">
          <div className="resource-title">
            <h3>{provider.name}</h3>
            <Badge color="gray" size="sm" variant="dot">{provider.kind === 'oauth' ? 'OAuth 账号' : provider.kind === 'interactive' ? '插件 · Key' : 'API Key'}</Badge>
            {provider.loggedIn
              ? <Badge color="green" size="sm" variant="dot">已登录</Badge>
              : <Badge color="gray" size="sm">{provider.kind === 'apikey' ? '未配置' : '未登录'}</Badge>}
            {provider.pending ? <Badge color="blue" size="sm" variant="dot">等待授权</Badge> : null}
          </div>
          <p className="list-description">{[provider.account, provider.message].filter(Boolean).join(' · ') || (provider.loggedIn ? '已就绪' : '尚未登录')}</p>
        </div>
        <div className="list-actions">
          {provider.kind === 'apikey' ? <Button
            disabled={auth.busyId === provider.id}
            onClick={() => openPreset(provider)}
            size="sm"
            variant="outline"
          >{provider.loggedIn ? '管理' : '填写 API Key'}</Button>
            : provider.loggedIn ? <Button
              disabled={auth.busyId === provider.id}
              onClick={() => void run(() => auth.logout(provider.id)).then(ok => { if (ok) notices.notify('已退出登录', provider.name, 'success') })}
              size="sm"
              variant="ghost"
            >退出登录</Button>
              : <Button
                disabled={auth.busyId === provider.id || provider.pending}
                icon={<LogIn size={14} />}
                onClick={() => {
                  if (provider.kind === 'interactive') { setSecret(''); setInteractive(provider); return }
                  void run(() => auth.login(provider.id)).then(ok => {
                    if (ok) notices.notify('已开始登录', '如已打开浏览器请完成授权；完成后状态会自动刷新。')
                  })
                }}
                size="sm"
                variant="outline"
              >{provider.pending ? '等待授权…' : '登录'}</Button>}
        </div>
      </div>)}</div>
    </Card> : null}
    {!store.configs.length
      ? <EmptyState title="连接你的第一个模型">填写 API 地址、密钥与模型名称，然后回到对话页开始工作。</EmptyState>
      : <div className="resource-grid">{store.configs.map(config => <Card key={config.id}>
        <CardHeader>
          <CardDescription className="resource-icon-row"><Cpu size={14} />{providerLabel(config.providerId)} · <span className="mono">{config.defaultModel}</span></CardDescription>
          <CardTitle>{config.displayName}</CardTitle>
          <CardAction>
            <Switch
              aria-label={`启用 ${config.displayName}`}
              checked={config.isEnabled}
              onCheckedChange={enabled => void run(() => store.save({ ...config, isEnabled: enabled }))}
              size="sm"
            />
          </CardAction>
        </CardHeader>
        <CardContent className="resource-card-body">
          <p className="resource-line mono">{config.baseUrl}</p>
          <div className="resource-badges">
            <Badge size="sm" variant="dot">协议 {config.effectiveProtocol || config.protocol || config.providerId}</Badge>
            <Badge size="sm" variant="dot">{config.models?.length ? `${config.models.length} 个模型` : config.defaultModel}</Badge>
            <Badge size="sm" variant="dot">上下文 {formatTokens(config.effectiveContextTokens ?? config.contextWindowTokens ?? 0)}</Badge>
            <Badge size="sm" variant="dot">输出 {formatTokens(config.effectiveMaxTokens ?? config.maxTokens ?? 0)}</Badge>
            {config.thinkingEnabled ? <Badge color="violet" size="sm" variant="dot">思考 {config.reasoningEffort || 'default'}</Badge> : null}
            {config.authenticationProviderId ? <Badge color="blue" size="sm" variant="dot">登录 · {config.authenticationProviderId}</Badge> : null}
            {config.apiKeyEnvironmentVariable ? <Badge color="gray" size="sm" variant="dot">env:{config.apiKeyEnvironmentVariable}</Badge> : null}
            {!config.isEnabled ? <Badge color="gray" size="sm">已停用</Badge> : null}
            {store.testStatus[config.id] === 'ok' ? <Badge color="green" size="sm" variant="dot">连接正常</Badge> : null}
            {store.testStatus[config.id] === 'error' ? <Badge color="red" size="sm" variant="dot">连接失败</Badge> : null}
          </div>
        </CardContent>
        <CardFooter className="resource-card-footer">
          <Button
            disabled={busy}
            loading={store.testStatus[config.id] === 'testing'}
            onClick={() => void run(() => store.testConnection(config.id))}
            size="sm"
            variant="outline"
          >测试连接</Button>
          <span className="resource-spacer" />
          <Button iconLabel="编辑模型" onClick={() => openEditor(config)} size="icon-sm" variant="ghost"><Pencil size={15} /></Button>
          <Button iconLabel="删除模型" onClick={() => setRemove(config)} size="icon-sm" variant="ghost"><Trash2 size={15} /></Button>
        </CardFooter>
      </Card>)}</div>}

    {editing ? <Modal
      footer={<>
        <Button disabled={busy} onClick={() => setEditing(null)} variant="outline">取消</Button>
        <Button
          disabled={!editing.displayName.trim() || !editing.baseUrl.trim() || !editing.defaultModel.trim() || !Number.isInteger(editing.maxTokens) || editing.maxTokens < 0 || editing.thinkingBudget < 512}
          loading={busy}
          onClick={() => void run(() => store.save(savePayload(editing))).then(ok => { if (ok) setEditing(null) })}
        >保存配置</Button>
      </>}
      onClose={() => { if (!busy) setEditing(null) }}
      size="lg"
      title={editing.id ? '编辑模型配置' : '添加模型配置'}
    >
      <Input
        description="内置：openai / anthropic / gemini / ollama / custom；也支持插件与自定义 ID（例如 deepseek、dashscope、workbuddy、codex）。"
        label="提供商 ID"
        onValueChange={providerId => update({ providerId })}
        value={editing.providerId}
      />
      <div className="preset-row">
        {providers.map(preset => <Button
          key={preset.value}
          onClick={() => update({ providerId: preset.value, baseUrl: preset.url })}
          size="xs"
          variant={editing.providerId === preset.value ? 'secondary' : 'outline'}
        >{preset.label}</Button>)}
        <Button
          onClick={() => update({ providerId: 'custom' })}
          size="xs"
          variant={editing.providerId === 'custom' ? 'secondary' : 'outline'}
        >custom</Button>
      </div>
      <Field label="配置名称" onChange={displayName => update({ displayName })} required value={editing.displayName} />
      <Field label="API 地址" onChange={baseUrl => update({ baseUrl })} required value={editing.baseUrl} />
      <Field label="API Key" onChange={apiKey => update({ apiKey })} placeholder="编辑时留空表示不修改" type="password" value={editing.apiKey} />
      <Field
        description="有值时优先使用环境变量里的密钥，适合把 Key 留在系统环境里。"
        label="API Key 环境变量（可选）"
        onChange={apiKeyEnvironmentVariable => update({ apiKeyEnvironmentVariable })}
        placeholder="例如 OPENAI_API_KEY"
        value={editing.apiKeyEnvironmentVariable ?? ''}
      />
      <label className="field"><span>认证入口</span>
        <Picker
          label="认证入口"
          onChange={authenticationProviderId => update({ authenticationProviderId })}
          options={authOptions}
          value={editing.authenticationProviderId ?? ''}
        />
      </label>
      <label className="field"><span>接口协议</span>
        <Picker
          label="接口协议"
          onChange={protocol => update({ protocol })}
          options={protocolOptions}
          value={editing.protocol ?? ''}
        />
      </label>
      <Field label="模型名称" onChange={defaultModel => update({ defaultModel })} required value={editing.defaultModel} />
      <TextArea
        description="每行一个模型 ID；「自动获取模型」会拉取接口的模型列表并记住上下文与输出上限。"
        label="模型列表"
        onChange={setModelsText}
        rows={6}
        value={modelsText}
      />
      {editing.id ? <div className="settings-inline-actions">
        <Button
          icon={<Wand2 size={15} />}
          loading={refreshing}
          onClick={() => void refreshModels()}
          size="sm"
          variant="outline"
        >自动获取模型</Button>
        <span className="list-description">按当前地址与密钥调用提供商的模型接口。</span>
      </div> : null}
      <div className="field-pair">
        <Field label="Temperature（0–2）" onChange={value => update({ temperature: Number(value) })} type="number" value={editing.temperature} />
        <Field label="最大输出 Tokens（0 = 自动）" onChange={value => update({ maxTokens: Number(value) })} type="number" value={editing.maxTokens} />
      </div>
      <Field
        description={`当前模型实际生效 ${formatTokens(editing.effectiveContextTokens ?? editing.contextWindowTokens ?? 0)} tokens；填入容量会只为该模型记录，填 0 或留空恢复自动。`}
        label="上下文容量（当前模型）"
        onChange={value => {
          const model = editing.defaultModel.trim()
          const tokens = Number(value) || 0
          // Without a model ID the fallback stays global.
          if (!model) { update({ contextWindowTokens: tokens || 100000 }); return }
          update({ modelContextWindows: { ...(editing.modelContextWindows ?? {}), [model]: tokens } })
        }}
        type="number"
        value={editing.modelContextWindows?.[editing.defaultModel.trim()] || editing.effectiveContextTokens || editing.contextWindowTokens || 100000}
      />
      {reasoning ? <>
        <label className="field"><span>思考协议</span>
          <Picker
            label="思考协议"
            onChange={reasoningProtocol => update({ reasoningProtocol })}
            options={reasoning.protocols.map(protocol => ({ value: protocol, label: protocol }))}
            value={reasoning.protocol}
          />
        </label>
        <label className="field"><span>思考强度</span>
          <Picker
            label="思考强度"
            onChange={reasoningEffort => update({ reasoningEffort })}
            options={effortOptions}
            value={editing.reasoningEffort ?? reasoning.effort}
          />
        </label>
        <Field
          description={`允许范围 ${reasoning.minimumBudget}–${reasoning.maximumBudget} tokens · 当前 ${reasoning.label}`}
          label="思考预算（tokens）"
          onChange={value => update({ thinkingBudget: Number(value) })}
          type="number"
          value={editing.thinkingBudget}
        />
      </> : null}
      <TextArea
        description={'JSON 对象，例如 {"X-Custom": "value"}。'}
        label="额外请求头（可选）"
        onChange={extraHeadersJson => update({ extraHeadersJson })}
        rows={3}
        value={editing.extraHeadersJson ?? ''}
      />
      <div className="setting-row"><span>启用此模型</span>
        <Switch aria-label="启用此模型" checked={editing.isEnabled} onCheckedChange={isEnabled => update({ isEnabled })} size="sm" />
      </div>
    </Modal> : null}

    {remove ? <Confirm
      onClose={() => setRemove(null)}
      onConfirm={() => run(() => store.remove(remove.id))}
      title={`删除「${remove.displayName}」？`}
    /> : null}
    {interactive ? <Modal
      footer={<>
        <Button onClick={() => setInteractive(null)} variant="outline">取消</Button>
        <Button
          disabled={!secret.trim()}
          loading={auth.busyId === interactive.id}
          onClick={() => void run(() => auth.loginInput(interactive.id, secret.trim())).then(ok => {
            if (ok) { setInteractive(null); notices.notify('登录成功', interactive.name, 'success') }
          })}
        >验证并保存</Button>
      </>}
      onClose={() => setInteractive(null)}
      size="sm"
      title={`登录 ${interactive.name}`}
    >
      <Field
        description="密钥仅保存在本机凭据库（Windows DPAPI 加密）。"
        label={interactive.inputPrompt ?? 'API Key'}
        onChange={setSecret}
        placeholder="粘贴密钥"
        required
        type="password"
        value={secret}
      />
    </Modal> : null}
  </PageShell>
}
