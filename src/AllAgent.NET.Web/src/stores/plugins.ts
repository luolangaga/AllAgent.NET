import { defineStore, ref } from '@/lib/store'
import type { InstalledPluginInfo, PluginAuthInfo, PluginPackageInfo, PluginSearchResult, PluginStatusInfo, PluginsSnapshot, HookInfo } from '@/types'
import { invoke } from '@/bridge'

const PAGE_SIZE = 10

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
  const page = ref(0)
  const totalHits = ref(0)
  const searched = ref(false)

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

  async function search(text: string, targetPage = 0) {
    searching.value = true
    query.value = text
    try {
      const result = await invoke<PluginSearchResult>('plugins.search', { query: text, skip: targetPage * PAGE_SIZE, take: PAGE_SIZE })
      searchResults.value = result.packages ?? []
      totalHits.value = result.totalHits ?? searchResults.value.length
      page.value = targetPage
      searched.value = true
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

  return { plugins, providers, authentication, hooks, loaded, available, busy, searching, searchResults, query, page, pageSize: PAGE_SIZE, totalHits, searched, loadAll, setEnabled, remove, search, install, importPackage, setHook }
})
