import { bridgeEvents, invoke } from '@/bridge'
import { computed, defineStore, ref } from '@/lib/store'
import type { PluginSettingsSnapshot, PluginWebInfo, PluginWebSettingsSection, WebContributionsSnapshot } from '@/types'

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

export interface PluginSettingsEntry {
  plugin: PluginWebInfo
  section: PluginWebSettingsSection
}

export function pluginPageRoute(packageId: string, pageId: string): string {
  return `plugin:${packageId.toLowerCase()}:${pageId}`
}

export const useContributionsStore = defineStore('contributions', () => {
  const plugins = ref<PluginWebInfo[]>([])
  const assetBaseUrl = ref<string | null>(null)
  const settingsValues = ref<Record<string, Record<string, string>>>({})
  const loaded = ref(false)
  const available = ref(false)
  const busy = ref(false)

  async function loadAll() {
    try {
      const snapshot = await invoke<WebContributionsSnapshot>('web.contributions')
      plugins.value = (snapshot.plugins ?? []).filter(plugin => plugin.contributes)
      assetBaseUrl.value = snapshot.assetBaseUrl ?? null
      available.value = true
      await loadSettings()
    } catch {
      plugins.value = []
      assetBaseUrl.value = null
      settingsValues.value = {}
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

  const settings = computed<PluginSettingsEntry[]>(() => active.value.flatMap(plugin =>
    (plugin.contributes?.settings ?? []).map(section => ({ plugin, section }))))

  async function loadSettings() {
    const packages = [...new Set(settings.value
      .filter(entry => (entry.section.fields?.length ?? 0) > 0)
      .map(entry => entry.plugin.packageId))]
    if (packages.length === 0) { settingsValues.value = {}; return }
    const pairs = await Promise.all(packages.map(async packageId => {
      try {
        const snapshot = await invoke<PluginSettingsSnapshot>('web.settings.get', { packageId })
        return [packageId, snapshot.values ?? {}] as const
      } catch {
        return [packageId, {}] as const
      }
    }))
    settingsValues.value = Object.fromEntries(pairs)
  }

  async function saveSetting(packageId: string, key: string, value: string) {
    await invoke('web.settings.set', { packageId, key, value })
    settingsValues.value = {
      ...settingsValues.value,
      [packageId]: { ...(settingsValues.value[packageId] ?? {}), [key]: value },
    }
  }

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

  return { plugins, assetBaseUrl, settingsValues, loaded, available, busy, active, pages, commands, settings, loadAll, loadSettings, saveSetting, setWebEnabled, pageByRoute, toolCardFor }
})
