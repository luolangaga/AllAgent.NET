import { defineStore, ref } from '@/lib/store'
import type { AppSettings } from '@/types'
import { invoke } from '@/bridge'

export const useSettingsStore = defineStore('settings', () => {
  const theme = ref<AppSettings['theme']>('system')
  const language = ref<AppSettings['language']>('zh')
  const defaultModel = ref('')
  const streamingEnabled = ref(true)
  const defaultProviderId = ref('')
  const browserUseEnabled = ref(true)
  const browserUseMode = ref<AppSettings['browserUseMode']>('builtin')
  const browserExecutablePath = ref('')
  const browserCdpUrl = ref('')
  const loaded = ref(false)

  function applyTheme(next: AppSettings['theme']) {
    const resolved = next === 'system'
      ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
      : next
    document.documentElement.classList.toggle('dark', resolved === 'dark')
    document.documentElement.dataset.theme = resolved
  }

  async function load() {
    const settings = await invoke<AppSettings>('settings.get')
    browserUseEnabled.value = settings.browserUseEnabled ?? true
    browserUseMode.value = settings.browserUseMode ?? 'builtin'
    browserExecutablePath.value = settings.browserExecutablePath ?? ''
    browserCdpUrl.value = settings.browserCdpUrl ?? ''
    theme.value = settings.theme ?? 'system'
    language.value = settings.language ?? 'zh'
    defaultModel.value = settings.defaultModel ?? ''
    streamingEnabled.value = settings.streamingEnabled ?? true
    defaultProviderId.value = settings.defaultProviderId ?? ''
    loaded.value = true
    applyTheme(theme.value)
  }

  async function save() {
    await invoke('settings.save', {
      browserUseEnabled: browserUseEnabled.value,
      browserUseMode: browserUseMode.value,
      browserExecutablePath: browserExecutablePath.value,
      browserCdpUrl: browserCdpUrl.value,
      theme: theme.value,
      language: language.value,
      defaultModel: defaultModel.value,
      streamingEnabled: streamingEnabled.value,
      defaultProviderId: defaultProviderId.value,
    })
  }

  applyTheme(theme.value)

  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (theme.value === 'system') applyTheme('system')
  })

  return {
    theme, language, defaultModel, streamingEnabled, defaultProviderId,
    browserUseEnabled, browserUseMode, browserExecutablePath, browserCdpUrl,
    loaded, load, save, applyTheme,
  }
})
