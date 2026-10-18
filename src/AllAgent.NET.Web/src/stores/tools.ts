import { defineStore, ref } from '@/lib/store'
import type { ToolInfo } from '@/types'
import { invoke } from '@/bridge'

// Tool catalog from the host: built-ins, plugin-contributed tools, MCP server tools and skills.
export const useToolsStore = defineStore('tools', () => {
  const tools = ref<ToolInfo[]>([])
  const errors = ref<string[]>([])
  const loading = ref(false)
  const loaded = ref(false)
  const available = ref(false)

  async function loadAll() {
    if (loading.value) return
    loading.value = true
    try {
      const snapshot = await invoke<{ tools: ToolInfo[]; errors: string[] }>('tools.list')
      tools.value = snapshot.tools ?? []
      errors.value = snapshot.errors ?? []
      available.value = true
    } catch (error) {
      available.value = false
      errors.value = [error instanceof Error ? error.message : String(error)]
    } finally {
      loading.value = false
      loaded.value = true
    }
  }

  return { tools, errors, loading, loaded, available, loadAll }
})
