import { defineStore, ref } from '@/lib/store'
import type { InstalledPluginInfo, PluginAuthInfo, PluginPackageInfo, PluginStatusInfo, PluginsSnapshot, HookInfo } from '@/types'
import { invoke } from '@/bridge'

export const usePluginsStore = defineStore('plugins', () => {
  const plugins = ref<PluginStatusInfo[]>([])
  const providers = ref<string[]>([])
  const authentication = ref<PluginAuthInfo[]>([])
  const hooks = ref<HookInfo[]>([])
  const loaded = ref(false)
  const available = ref(false)
  const busy = ref(false)
  const searching = ref(false)
  const searchResults = ref<PluginPackageInfo[]>([])
  const query = ref('')

  async function loadAll() {
    try {
      const snapshot = await invoke<PluginsSnapshot>('plugins.list')
      plugins.value = snapshot.plugins ?? []
      providers.value = (snapshot.providers ?? []).map(provider => provider.providerId)
      authentication.value = snapshot.authentication ?? []
      hooks.value = snapshot.hooks ?? []
      available.value = true
    } catch {
      available.value = false
    } finally {
      loaded.value = true
    }
  }

  async function run(action: () => Promise<unknown>) {
    busy.value = true
    try {
      await action()
      await loadAll()
    } finally {
      busy.value = false
    }
  }

  async function setEnabled(packageId: string, enabled: boolean) {
    await run(() => invoke('plugins.setEnabled', { packageId, enabled }))
  }

  async function remove(packageId: string) {
    await run(() => invoke('plugins.remove', { packageId }))
  }

  async function search(text: string) {
    searching.value = true
    query.value = text
    try {
      searchResults.value = await invoke<PluginPackageInfo[]>('plugins.search', { query: text })
    } finally {
      searching.value = false
    }
  }

  async function install(packageId: string, version: string) {
    await run(() => invoke<InstalledPluginInfo>('plugins.install', { id: packageId, version }))
  }

  async function importPackage(fileName: string, base64: string) {
    await run(() => invoke<InstalledPluginInfo>('plugins.import', { fileName, base64 }))
  }

  async function setHook(name: string, enabled: boolean) {
    await run(() => invoke('plugins.setHookEnabled', { name, enabled }))
  }

  return { plugins, providers, authentication, hooks, loaded, available, busy, searching, searchResults, query, loadAll, setEnabled, remove, search, install, importPackage, setHook }
})
