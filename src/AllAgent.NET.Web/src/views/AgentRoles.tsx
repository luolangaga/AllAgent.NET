import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { TextArea } from '@/components/shared'
import { useStore } from '@/lib/store'
import { useAgentsStore } from '@/stores/agents'
import { useNoticesStore } from '@/stores/ui'

// Sub-agent role editor: roles are structured JSON, so the editor keeps the
// raw form with live validation and a readable summary of what is loaded.
export function AgentRoles() {
  const store = useStore(useAgentsStore)
  const notices = useStore(useNoticesStore)
  const [json, setJson] = useState('')
  const [pristine, setPristine] = useState(true)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!store.loaded) {
      void store.loadAll().catch(notices.report)
    }
  }, [])

  useEffect(() => {
    if (store.loaded && pristine) setJson(store.serialize())
  }, [store.loaded, store.roles.length])

  async function save() {
    setBusy(true)
    try {
      await store.importJson(json)
      setPristine(true)
      setJson(store.serialize())
      notices.notify('角色配置已保存', '新的执行预算对后续任务生效。', 'success')
    } catch (error) {
      notices.report(error)
    } finally {
      setBusy(false)
    }
  }

  return <div className="agent-roles">
    <div className="agent-roles-summary">
      {store.roles.map(role => <Badge key={role.name} size="sm" variant="dot">{role.name}{role.readOnly ? ' · 只读' : ''}{role.model ? ` · ${role.model}` : ''}</Badge>)}
      {!store.roles.length ? <span className="list-description">尚未加载角色配置。</span> : null}
    </div>
    <TextArea
      description="execution 支持 temperature、reasoningEffort、thinkingBudget、maxOutputTokens、maxToolCalls、timeoutSeconds、maxConcurrent、skills 等；maxDepth 为 0 禁用派生、1 启用一层。"
      label="角色配置 JSON"
      onChange={value => { setJson(value); setPristine(false) }}
      rows={16}
      value={json}
    />
    <div className="agent-roles-actions">
      <Button disabled={busy || !json.trim()} loading={busy} onClick={() => void save()}>保存角色配置</Button>
      <Button
        disabled={busy || pristine}
        onClick={() => { setJson(store.serialize()); setPristine(true) }}
        variant="outline"
      >放弃修改</Button>
    </div>
  </div>
}
