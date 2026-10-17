import { defineStore, ref } from '@/lib/store'
import type { AgentRoleDefinition } from '@/types'
import { invoke } from '@/bridge'

export const useAgentsStore = defineStore('agents', () => {
  const roles = ref<AgentRoleDefinition[]>([])
  const loaded = ref(false)

  async function loadAll() {
    roles.value = await invoke<AgentRoleDefinition[]>('agents.list')
    loaded.value = true
  }

  function serialize(): string {
    return JSON.stringify(roles.value, null, 2)
  }

  async function importJson(json: string) {
    const parsed: unknown = JSON.parse(json)
    if (!Array.isArray(parsed)) throw new Error('角色配置必须是数组')
    if (parsed.some(item => typeof item !== 'object' || item === null || typeof (item as AgentRoleDefinition).name !== 'string')) {
      throw new Error('每个角色都需要 name 字段')
    }
    await invoke('agents.import', { json })
    await loadAll()
  }

  return { roles, loaded, loadAll, serialize, importJson }
})
