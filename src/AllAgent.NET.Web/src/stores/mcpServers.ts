import { defineStore, ref } from '@/lib/store'
import type { McpServerConfig } from '@/types'
import { invoke } from '@/bridge'

export const useMcpServersStore = defineStore('mcpServers', () => {
  const servers = ref<McpServerConfig[]>([])
  const connectedIds = ref<string[]>([])
  const connectingIds = ref<string[]>([])
  const loaded = ref(false)

  async function loadAll() {
    servers.value = await invoke<McpServerConfig[]>('mcpServers.list')
    connectedIds.value = servers.value.filter(server => server.isConnected).map(server => server.id)
    loaded.value = true
  }

  async function save(config: Partial<McpServerConfig> & { name: string; command: string }) {
    const result = await invoke<McpServerConfig>('mcpServers.save', {
      id: config.id || null,
      name: config.name,
      command: config.command,
      arguments: config.arguments || '',
      isEnabled: config.isEnabled ?? false,
    })
    await loadAll()
    connectedIds.value = connectedIds.value.filter(id => id !== result.id)
    return result
  }

  async function remove(id: string) {
    await invoke('mcpServers.delete', { id })
    connectedIds.value = connectedIds.value.filter(item => item !== id)
    await loadAll()
  }

  async function connect(id: string) {
    connectingIds.value = [...connectingIds.value, id]
    try {
      const ok = await invoke<boolean>('mcpServers.connect', { id })
      if (ok) connectedIds.value = [...new Set([...connectedIds.value, id])]
      return ok
    } finally {
      connectingIds.value = connectingIds.value.filter(item => item !== id)
    }
  }

  async function disconnect(id: string) {
    await invoke('mcpServers.disconnect', { id })
    await loadAll()
  }

  return { servers, connectedIds, connectingIds, loaded, loadAll, save, remove, connect, disconnect }
})
