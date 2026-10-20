import { useEffect, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Switch } from '@/components/ui/b-switch'
import { Field, Picker } from '@/components/shared'
import { PluginFrame } from '@/components/plugins/PluginFrame'
import { useStore } from '@/lib/store'
import { useContributionsStore } from '@/stores/contributions'
import { useNoticesStore } from '@/stores/ui'
import type { PluginWebInfo, PluginWebSettingField, PluginWebSettingsSection } from '@/types'

/** Generic host renderer for a plugin's declared settings section: native fields plus an
 * optional sandboxed frame for plugin-owned status and actions. */
export function PluginSettingsSection({ plugin, section }: { plugin: PluginWebInfo; section: PluginWebSettingsSection }) {
  const contributions = useStore(useContributionsStore)
  const notices = useStore(useNoticesStore)
  const [busy, setBusy] = useState(false)
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const timers = useRef<Record<string, number>>({})
  const values = contributions.settingsValues[plugin.packageId] ?? {}

  useEffect(() => () => {
    for (const timer of Object.values(timers.current)) window.clearTimeout(timer)
  }, [])

  async function save(field: PluginWebSettingField, value: string) {
    setBusy(true)
    try { await contributions.saveSetting(plugin.packageId, field.key, value) }
    catch (error) { notices.report(error) }
    finally { setBusy(false) }
  }

  function edit(field: PluginWebSettingField, value: string) {
    setDrafts(current => ({ ...current, [field.key]: value }))
    window.clearTimeout(timers.current[field.key])
    timers.current[field.key] = window.setTimeout(() => { void save(field, value) }, 400)
  }

  return <Card className="settings-wide">
    <CardHeader>
      <CardTitle>{section.title} <Badge size="sm" variant="dot">{plugin.name}</Badge></CardTitle>
      <CardDescription>由 {plugin.packageId} 提供。字段修改后自动保存。</CardDescription>
    </CardHeader>
    <CardContent className="settings-fields">
      {(section.fields ?? []).map(field => {
        const value = drafts[field.key] ?? values[field.key] ?? field.default ?? ''
        if (field.type === 'boolean') return <div className="setting-row" key={field.key}>
          <div>
            <strong>{field.label}</strong>
            {field.description ? <p>{field.description}</p> : null}
          </div>
          <Switch
            aria-label={field.label}
            checked={value === 'true'}
            disabled={busy}
            onCheckedChange={next => void save(field, String(next))}
            size="sm"
          />
        </div>
        if (field.type === 'select') return <label className="field" key={field.key}>
          <span>{field.label}</span>
          <Picker
            disabled={busy}
            label={field.label}
            onChange={next => void save(field, next)}
            options={(field.options ?? []).map(option => ({ value: option, label: option }))}
            value={value}
          />
          {field.description ? <small className="field-hint">{field.description}</small> : null}
        </label>
        return <Field
          description={field.description ?? undefined}
          key={field.key}
          label={field.label}
          onChange={next => edit(field, next)}
          placeholder={field.placeholder ?? undefined}
          type={field.type ?? 'text'}
          value={value}
        />
      })}
      {section.entry ? <PluginFrame
        assetBaseUrl={contributions.assetBaseUrl}
        autoHeight
        className="plugin-settings-frame"
        entry={section.entry}
        maxHeight={560}
        minHeight={180}
        plugin={plugin}
        view={{ kind: 'settings', id: section.id }}
      /> : null}
    </CardContent>
  </Card>
}
