import { useState } from 'react'
import { Blocks, CheckCircle2, FolderOpen, HardDrive, Settings2 } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Separator } from '@/components/ui/b-separator'
import { Switch } from '@/components/ui/b-switch'
import { Field, Picker } from '@/components/shared'
import { PageShell } from '@/components/PageShell'
import { useStore } from '@/lib/store'
import { PERMISSION_OPTIONS, permissionLabel } from '@/lib/permissions'
import { useApiConfigsStore } from '@/stores/apiConfigs'
import { useSettingsStore } from '@/stores/settings'
import { useNoticesStore } from '@/stores/ui'
import { useWorkspaceStore } from '@/stores/workspace'
import { AgentRoles } from './AgentRoles'
import type { AppSettings } from '@/types'

export default function Settings() {
  const settings = useStore(useSettingsStore)
  const api = useStore(useApiConfigsStore)
  const notices = useStore(useNoticesStore)
  const workspace = useStore(useWorkspaceStore)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)

  async function setPermission(mode: string) {
    try {
      await workspace.setPermission(mode)
      notices.notify('权限模式已切换', permissionLabel(mode), 'success')
    } catch (error) {
      notices.report(error)
    }
  }

  async function setSandbox(mode: string) {
    try {
      await workspace.setSandbox(mode)
      notices.notify('沙箱选择已保存', mode, 'success')
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
      notices.notify('设置已保存', '全部偏好已写入本机。', 'success')
    } catch (error) {
      notices.report(error)
    } finally {
      setBusy(false)
    }
  }

  // Empty strings mean "follow", but the select needs a real value to show its label.
  const providerOptions = [
    { value: 'follow', label: '跟随对话选择' },
    ...api.configs.map(config => ({ value: config.id, label: `${config.displayName} · ${config.defaultModel}` })),
  ]
  // Git AI drafting: default is whatever model the composer currently has selected.
  const commitModelOptions = [
    { value: 'follow', label: '跟随当前选择的模型（推荐）' },
    ...api.configs.filter(config => config.isEnabled).map(config => ({ value: config.id, label: `${config.displayName} · ${config.defaultModel}` })),
  ]

  return <PageShell
    action={<Button
      icon={saved ? <CheckCircle2 size={16} /> : <Settings2 size={16} />}
      loading={busy}
      onClick={() => void save()}
    >{saved ? '已保存' : '保存设置'}</Button>}
    description="外观、默认模型与浏览器操作偏好。设置保存在本机工作区，并立刻生效。"
    eyebrow="YOUR WORKSPACE"
    title="按你的习惯，开始工作。"
  >
    <div className="settings-grid">
      <Card>
        <CardHeader>
          <CardTitle>外观</CardTitle>
          <CardDescription>浅色、深色，或跟随系统外观。</CardDescription>
        </CardHeader>
        <CardContent className="settings-fields">
          <label className="field"><span>主题</span>
            <Picker
              label="主题"
              onChange={value => { settings.theme = value as AppSettings['theme']; settings.applyTheme(settings.theme) }}
              options={[{ value: 'light', label: '浅色' }, { value: 'dark', label: '深色' }, { value: 'system', label: '跟随系统' }]}
              value={settings.theme}
            />
          </label>
          <label className="field"><span>语言偏好</span>
            <Picker
              label="语言偏好"
              onChange={value => { settings.language = value as AppSettings['language'] }}
              options={[{ value: 'zh', label: '中文' }, { value: 'en', label: 'English' }]}
              value={settings.language}
            />
          </label>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>对话默认值</CardTitle>
          <CardDescription>新对话默认使用的模型与流式输出偏好。</CardDescription>
        </CardHeader>
        <CardContent className="settings-fields">
          <label className="field"><span>默认提供商与模型</span>
            <Picker
              label="默认提供商"
              onChange={value => { settings.defaultProviderId = value === 'follow' ? '' : value }}
              options={providerOptions}
              value={settings.defaultProviderId || 'follow'}
            />
          </label>
          <Field label="默认模型名称（可留空，跟随配置）" onChange={value => { settings.defaultModel = value }} placeholder="例如 gpt-5.1" value={settings.defaultModel} />
          <div className="setting-row"><div>
            <strong>流式输出</strong>
            <p>逐字显示模型输出，并实时渲染工具执行轨迹。</p>
          </div>
            <Switch aria-label="流式输出" checked={settings.streamingEnabled} onCheckedChange={value => { settings.streamingEnabled = value }} size="sm" />
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Git 提交信息</CardTitle>
          <CardDescription>AI 生成提交信息时使用的模型。默认跟随输入框当前选择的模型；保存后立即生效。</CardDescription>
        </CardHeader>
        <CardContent className="settings-fields">
          <label className="field"><span>AI 生成提交信息使用的模型</span>
            <Picker
              label="AI 提交信息模型"
              onChange={value => { settings.gitCommitConfigId = value === 'follow' ? '' : value }}
              options={commitModelOptions}
              value={settings.gitCommitConfigId || 'follow'}
            />
          </label>
          <p className="list-description">「跟随当前选择的模型」会使用聊天输入框里正在使用的配置；也可在这里固定一个配置（例如专门用于提交信息的快模型）。</p>
        </CardContent>
      </Card>

      <Card className="settings-wide">
        <CardHeader>
          <CardTitle>Browser Use</CardTitle>
          <CardDescription>浏览器操作默认开启。内置模式使用系统 WebView2，外置模式手动连接 Chromium。</CardDescription>
        </CardHeader>
        <CardContent className="settings-fields">
          <div className="setting-row"><div>
            <strong>浏览器操作</strong>
            <p>保存设置后生效；关闭会中止正在进行的浏览器操作。</p>
          </div>
            <Switch aria-label="Browser Use" checked={settings.browserUseEnabled} onCheckedChange={value => { settings.browserUseEnabled = value }} size="sm" />
          </div>
          <label className="field"><span>浏览器模式</span>
            <Picker
              label="浏览器模式"
              onChange={value => { settings.browserUseMode = value as AppSettings['browserUseMode'] }}
              options={[
                { value: 'builtin', label: '内置浏览器 · 使用时自动打开窗口' },
                { value: 'chromium', label: '外置 Chromium · 手动连接' },
              ]}
              value={settings.browserUseMode}
            />
          </label>
          {settings.browserUseMode === 'chromium' ? <>
            <Field
              description="终端输入 /browser connect 获取逐步连接向导。"
              label="本机 CDP 地址"
              onChange={value => { settings.browserCdpUrl = value }}
              placeholder="http://127.0.0.1:9222"
              value={settings.browserCdpUrl}
            />
            <Field
              label="浏览器路径（用于手动启动，连接请填写 CDP）"
              onChange={value => { settings.browserExecutablePath = value }}
              value={settings.browserExecutablePath}
            />
          </> : null}
        </CardContent>
      </Card>

      <Card className="settings-wide">
        <CardHeader>
          <CardTitle>子代理角色</CardTitle>
          <CardDescription>按角色设置 provider、model、instructions、tools 与执行预算。留空的模型继承默认配置，工具权限仍受宿主限制。</CardDescription>
        </CardHeader>
        <CardContent>
          <AgentRoles />
        </CardContent>
      </Card>

      {workspace.available ? <Card className="settings-wide">
        <CardHeader>
          <CardTitle>工作区</CardTitle>
          <CardDescription>每个对话都属于一个工作区。切换后模型、会话、技能、任务会立即刷新，终端自己的工作区不受影响。</CardDescription>
        </CardHeader>
        <CardContent className="settings-fields">
          <div className="workspace-line"><HardDrive size={14} /><span>当前工作区<b className="mono">{workspace.workspace || '未知'}</b></span></div>
          <div className="workspace-line"><FolderOpen size={14} /><span>数据目录<b className="mono">{workspace.dataDirectory || '未知'}</b></span></div>
          <div className="workspace-line"><Blocks size={14} /><span>共享目录<b className="mono">{workspace.sharedDirectory || '未知'}</b></span></div>
          <div className="setting-row"><div>
            <strong>最近打开的工作区</strong>
            <p>选择一个工作区即可切换；会话按工作区归类，不会混在一起。</p>
          </div>
            <Button icon={<FolderOpen size={14} />} onClick={() => workspace.openSwitcher()} size="sm" variant="outline">打开 / 新建</Button>
          </div>
          <div className="workspace-list">{workspace.list.map(entry => <div className="workspace-entry-row" key={entry.workspace}>
            <div className="workspace-entry-copy">
              <strong>{entry.name}{entry.isCurrent ? <Badge size="sm" variant="dot">当前</Badge> : null}</strong>
              <span className="mono">{entry.workspace}</span>
            </div>
            <Badge color="gray" size="sm" variant="dot">{entry.sessionCount} 个会话</Badge>
            <Button
              disabled={workspace.switching || entry.isCurrent}
              onClick={() => void workspace.switchTo(entry.workspace).then(() => notices.notify('工作区已切换', entry.name, 'success')).catch(notices.report)}
              size="sm"
              variant="outline"
            >{entry.isCurrent ? '已打开' : '切换'}</Button>
          </div>)}</div>
        </CardContent>
      </Card> : null}

      {workspace.available && workspace.controls ? <Card className="settings-wide">
        <CardHeader>
          <CardTitle>权限与执行</CardTitle>
          <CardDescription>与终端共享同一套权限模式；浏览器内的工具授权请求会直接弹出对话框。权限模式随时可切换，运行中的任务从下一次工具调用起采用新模式；沙箱切换需要任务结束后进行。</CardDescription>
        </CardHeader>
        <CardContent className="settings-fields">
          <label className="field"><span>权限模式</span>
            <Picker
              label="权限模式"
              onChange={mode => void setPermission(mode)}
              options={PERMISSION_OPTIONS.map(option => ({ value: option.value, label: `${option.label} · ${option.hint}` }))}
              value={workspace.controls.permissionMode}
            />
          </label>
          <label className="field"><span>沙箱</span>
            <Picker
              disabled={workspace.controls.running}
              label="沙箱"
              onChange={mode => void setSandbox(mode)}
              options={[
                { value: 'auto', label: 'auto · 优先 Hyperlight，失败回退 Monty' },
                { value: 'hyperlight', label: 'hyperlight · 微虚拟机隔离' },
                { value: 'monty', label: 'monty · 无需虚拟化' },
              ]}
              value={workspace.controls.sandboxMode}
            />
          </label>
          <p className="list-description">
            当前：{workspace.controls.permissionLabel} · 沙箱 {workspace.controls.sandboxEffective}
            {workspace.controls.running ? '（有任务运行中，沙箱暂不可切换）' : ''}
          </p>
        </CardContent>
      </Card> : null}

      <Card className="settings-wide about-card">
        <CardContent className="about-row">
          <div className="brand-symbol"><Blocks size={20} /></div>
          <div className="about-copy">
            <strong>AllAgent.NET</strong>
            <p>本地 Agent 工作空间 · 终端与桌面共用同一套界面</p>
          </div>
          <Separator orientation="vertical" spacing="none" className="about-separator" />
          <div className="about-badges">
            <Badge size="sm" variant="dot">React 19</Badge>
            <Badge size="sm" variant="dot">.NET 10</Badge>
            <Badge size="sm" variant="dot">Iconiq UI</Badge>
          </div>
        </CardContent>
      </Card>
    </div>
  </PageShell>
}
