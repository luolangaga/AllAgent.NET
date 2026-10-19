import { bridgeEvents, invoke } from '@/bridge'
import { computed, defineStore, ref } from '@/lib/store'
import type { PluginWebInfo, WebContributionsSnapshot } from '@/types'

// Contribution snapshot from the host: which plugins add pages, tool cards and commands to the
// shell. Only `active` plugins (enabled + web enabled + valid web manifest) render anything.

export interface PluginPageEntry {
  route: string
  pageId: string
  title: string
  icon?: string | null
  entry: string
  order: number
  plugin: PluginWebInfo
}

export interface PluginWebCommandEntry {
  key: string
  title: string
  pluginName: string
  route: string
}

export interface PluginToolCardEntry {
  plugin: PluginWebInfo
  entry: string
}

export function pluginPageRoute(packageId: string, pageId: string): string {
  return `plugin:${packageId.toLowerCase()}:${pageId}`
}

export const useContributionsStore = defineStore('contributions', () => {
  const plugins = ref<PluginWebInfo[]>([])
  const assetBaseUrl = ref<string | null>(null)
  const loaded = ref(false)
  const available = ref(false)
  const busy = ref(false)

  async function loadAll() {
    try {
      const snapshot = await invoke<WebContributionsSnapshot>('web.contributions')
      plugins.value = (snapshot.plugins ?? []).filter(plugin => plugin.contributes)
      assetBaseUrl.value = snapshot.assetBaseUrl ?? null
      available.value = true
    } catch {
      plugins.value = []
      assetBaseUrl.value = null
      available.value = false
    } finally {
      loaded.value = true
    }
  }

  async function setWebEnabled(packageId: string, enabled: boolean) {
    busy.value = true
    try { await invoke('web.setEnabled', { packageId, enabled }) } finally { busy.value = false }
    await loadAll()
  }

  const active = computed(() => plugins.value.filter(plugin => plugin.active))

  const pages = computed<PluginPageEntry[]>(() => active.value
    .flatMap(plugin => (plugin.contributes?.pages ?? []).map(page => ({
      route: pluginPageRoute(plugin.packageId, page.id),
      pageId: page.id,
      title: page.title,
      icon: page.icon,
      entry: page.entry,
      order: page.order ?? 50,
      plugin,
    })))
    .sort((left, right) => left.order - right.order || left.title.localeCompare(right.title)))

  const commands = computed<PluginWebCommandEntry[]>(() => active.value.flatMap(plugin =>
    (plugin.contributes?.commands ?? [])
      .filter(command => command.page)
      .map(command => ({
        key: `${plugin.packageId}:${command.id}`,
        title: command.title,
        pluginName: plugin.name,
        route: pluginPageRoute(plugin.packageId, command.page as string),
      }))))

  function pageByRoute(route: string): PluginPageEntry | undefined {
    return pages.value.find(entry => entry.route === route)
  }

  function toolCardFor(tool: string): PluginToolCardEntry | undefined {
    for (const plugin of active.value) {
      const card = (plugin.contributes?.toolCards ?? []).find(item => item.tool === tool)
      if (card) return { plugin, entry: card.entry }
    }
    return undefined
  }

  bridgeEvents.on('web.contributions.changed', () => { void loadAll() })

  return { plugins, assetBaseUrl, loaded, available, busy, active, pages, commands, loadAll, setWebEnabled, pageByRoute, toolCardFor }
})
