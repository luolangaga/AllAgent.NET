import { useEffect, useRef, useState } from 'react'
import { Button } from '../components/ui/b-button'
import { invoke } from '../bridge'

export function AgentRoles({ report }: { report: (error: unknown) => void }) {
  const [json, setJson] = useState('')
  const [busy, setBusy] = useState(true)
  const [saved, setSaved] = useState(false)
  const reportRef = useRef(report)
  reportRef.current = report
  useEffect(() => {
    let active = true
    invoke<unknown[]>('agents.list').then(roles => { if (active) setJson(JSON.stringify(roles, null, 2)) })
      .catch(error => { if (active) reportRef.current(error) }).finally(() => { if (active) setBusy(false) })
    return () => { active = false }
  }, [])
  async function save() {
    setBusy(true); setSaved(false)
    try {
      const roles: unknown = JSON.parse(json)
      if (!Array.isArray(roles)) throw new Error('角色配置必须是数组')
      await invoke('agents.import', { json })
      setSaved(true)
    } catch (error) { report(error) } finally { setBusy(false) }
  }
  return <div className="settings-group">
    <h3>子代理独立配置</h3>
    <p>按角色设置 provider、model、instructions、tools、permissions 和 execution。留空的模型继承默认配置；工具权限仍受宿主限制。更改执行预算后，新任务生效。</p>
    <p>execution 支持 temperature、reasoningEffort、thinkingBudget、maxOutputTokens、maxContextCharacters、maxToolCalls、timeoutSeconds、maxTotalTokens、maxConcurrent、skills 和 includeProjectInstructions。</p>
    <p>failurePolicy 可设 return、retry 或 fallback，配合 maxRetries、fallbackProvider、fallbackModel。maxCost 需同时填写 inputCostPerMillion 和 outputCostPerMillion，按相同货币计价；费用包含保守估算。一层派生上限：maxDepth 为 0 禁用、1 启用。</p>
    <label className="field"><span>角色配置 JSON</span><textarea aria-label="子代理角色配置 JSON" rows={22} value={json} disabled={busy} spellCheck={false} onChange={event => { setJson(event.target.value); setSaved(false) }} style={{ width: '100%', fontFamily: 'monospace', resize: 'vertical' }} /></label>
    <Button disabled={busy || !json.trim()} loading={busy} onClick={() => void save()}>{saved ? '角色配置已保存' : '保存角色配置'}</Button>
  </div>
}
