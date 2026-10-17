import { useMemo } from 'react'
import { Settings2, SlidersHorizontal, Square } from 'lucide-react'
import { AIInput, type PromptSettingGroup } from './ui/ai-input'
import { Button } from './ui/b-button'
import { StatusDot } from './ui/status-dot'
import type { ApiConfig, ReasoningOptions } from '@/types'

export interface PromptMeta {
  thinkingEnabled: boolean
  thinkingBudget: number
  model?: string
  reasoningEffort?: string
}

export function PromptComposer({ draft, onDraftChange, busy, configs, selectedId, onSelect, onSend, onStop, onConfigure, onSettings, models, reasoning }: {
  draft: string
  onDraftChange: (value: string) => void
  busy: boolean
  configs: ApiConfig[]
  selectedId: string | null
  onSelect: (id: string) => void
  onSend: (text: string, meta: PromptMeta) => void
  onStop: () => void
  onConfigure: () => void
  onSettings: () => void
  models: string[]
  reasoning: ReasoningOptions | null
}) {
  const config = configs.find(item => item.id === selectedId) ?? configs[0]

  const modelOptions = useMemo(() => {
    const list = models.length ? models : config?.defaultModel ? [config.defaultModel] : []
    const current = config?.defaultModel
    return [...new Set([...(current ? [current] : []), ...list])].map(id => ({ value: id, label: id }))
  }, [models, config?.defaultModel])

  const thinkingOptions = useMemo(() => [
    { value: 'off', label: '标准回答' },
    { value: 'on', label: '深度思考' },
    ...(reasoning?.efforts ?? []).filter(effort => effort !== 'default').map(effort => ({ value: `effort:${effort}`, label: `等级 · ${effort}` })),
  ], [reasoning?.efforts])

  // Iconiq's settings dropdown only renders groups that declare a display mode:
  // submenu groups get a label, a live value and a flyout list.
  const groups: PromptSettingGroup[] = [
    ...(modelOptions.length > 0 ? [{ id: 'model', label: '模型', display: 'submenu' as const, options: modelOptions }] : []),
    { id: 'thinking', label: '思考等级', display: 'submenu' as const, options: thinkingOptions },
  ]

  return <div className="composer-area">
    <div className="composer-meta">
      <span className="composer-model">
        <StatusDot animate={busy} size="sm" tone={config ? (busy ? 'active' : 'success') : 'neutral'} />
        {config ? `${config.displayName} · ${config.defaultModel}` : '先连接一个模型，开启对话'}
      </span>
      {busy ? <Button icon={<Square size={12} />} onClick={onStop} size="sm" variant="outline">停止生成</Button> : null}
    </div>
    <AIInput
      agents={configs.map(item => ({ value: item.id, label: item.displayName }))}
      defaultAgent={config?.id}
      defaultSettings={{
        model: config?.defaultModel ?? '',
        thinking: config?.thinkingEnabled ? 'on' : 'off',
        budget: String(config?.thinkingBudget ?? 8000),
      }}
      disabled={busy || !config}
      key={config?.id ?? 'none'}
      menuItems={[
        { value: 'models', label: '模型与 API 配置', icon: SlidersHorizontal, onClick: onConfigure },
        { value: 'settings', label: '工作空间设置', icon: Settings2, onClick: onSettings },
      ]}
      onAgentChange={onSelect}
      onSend={(text, meta) => {
        const thinking = meta.settings.thinking ?? 'off'
        const effort = thinking.startsWith('effort:') ? thinking.slice('effort:'.length) : undefined
        onSend(text, {
          thinkingEnabled: thinking !== 'off',
          thinkingBudget: Number(meta.settings.budget ?? config?.thinkingBudget ?? 8000),
          model: meta.settings.model || undefined,
          reasoningEffort: effort,
        })
      }}
      onValueChange={onDraftChange}
      placeholder={config ? '描述你的想法，或交给我一个任务…' : '请先在「模型与 API」中添加配置'}
      settingGroups={groups}
      showMessages={false}
      value={draft}
    />
    <div className="composer-hint">
      <span>{config ? 'Enter 发送 · Shift + Enter 换行' : ''}</span>
      <span>重要内容请核实</span>
    </div>
  </div>
}
