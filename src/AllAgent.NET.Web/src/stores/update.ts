import { invoke } from '@/bridge'
import { defineStore, ref } from '@/lib/store'
import type { UpdateInvokeResult, UpdateSettings, UpdateStatus } from '@/types'

const DISMISS_KEY = 'allagent.update.dismissed'

const DEFAULT_SETTINGS: UpdateSettings = {
  source: 'auto',
  channel: 'stable',
  autoCheck: true,
  autoInstall: false,
  requireSignature: false,
  requireChecksum: true,
}

export const useUpdateStore = defineStore('update', () => {
  const status = ref<UpdateStatus | null>(null)
  const settings = ref<UpdateSettings>({ ...DEFAULT_SETTINGS })
  const loading = ref(false)
  const installing = ref(false)
  const dismissedVersion = ref<string>(readDismissed())

  function readDismissed(): string {
    try { return localStorage.getItem(DISMISS_KEY) ?? '' } catch { return '' }
  }

  async function load(force = false) {
    if (loading.value) return
    loading.value = true
    try {
      status.value = await invoke<UpdateStatus>(force ? 'update.check' : 'update.status')
      // The host checks once at startup, but a page opened later may see an empty
      // or stale cache. Ask the host for a fresh check instead of hiding the banner.
      const checked = status.value?.checkedAt ? new Date(status.value.checkedAt).getTime() : 0
      if (!checked || Date.now() - checked > 24 * 60 * 60 * 1000) {
        status.value = await invoke<UpdateStatus>('update.check', { force: true })
      }
    } catch {
      // An older host may not expose the update bridge methods; the banner stays hidden.
    } finally {
      loading.value = false
    }
  }

  async function loadSettings() {
    try {
      settings.value = await invoke<UpdateSettings>('update.settings.get')
    } catch {
      settings.value = { ...DEFAULT_SETTINGS }
    }
  }

  async function saveSettings() {
    await invoke('update.settings.set', settings.value)
  }

  async function install(): Promise<UpdateInvokeResult> {
    installing.value = true
    try {
      return await invoke<UpdateInvokeResult>('update.install', {})
    } finally {
      installing.value = false
    }
  }

  function dismiss() {
    dismissedVersion.value = status.value?.latestVersion ?? ''
    try { localStorage.setItem(DISMISS_KEY, dismissedVersion.value) } catch { /* storage may be unavailable */ }
  }

  return { status, settings, loading, installing, dismissedVersion, load, loadSettings, saveSettings, install, dismiss }
})
