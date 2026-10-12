import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { McpServerConfig } from '@/types'
import { invoke } from '@/bridge'

export const useMcpServersStore = defineStore('mcpServers', () => {
  const servers = ref<McpServerConfig[]>([])
  const connectingIds = ref<Set<string>>(new Set())

  async function loadAll() {
    servers.value = await invoke<McpServerConfig[]>('mcpServers.list')
  }

  async function save(config: Partial<McpServerConfig> & { name: string; command: string }) {
    const result = await invoke<McpServerConfig>('mcpServers.save', {
      id: config.id || null,
      name: config.name,
      command: config.command,
      arguments: config.arguments || '',
      isEnabled: config.isEnabled ?? true,
    })
    await loadAll()
    return result
  }

  async function remove(id: string) {
    await invoke('mcpServers.delete', { id })
    await loadAll()
  }

  async function connect(id: string) {
    connectingIds.value = new Set([...connectingIds.value, id])
    try {
      const ok = await invoke<boolean>('mcpServers.connect', { id })
      return ok
    } finally {
      connectingIds.value = new Set([...connectingIds.value].filter(x => x !== id))
    }
  }

  return { servers, connectingIds, loadAll, save, remove, connect }
})