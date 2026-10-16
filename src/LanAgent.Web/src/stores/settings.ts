import { defineStore } from '@/lib/store'
import { ref } from '@/lib/store'
import type { AppSettings } from '@/types'
import { invoke } from '@/bridge'

export const useSettingsStore = defineStore('settings', () => {
  const theme = ref<'light' | 'dark' | 'system'>('system')
  const language = ref<'zh' | 'en'>('zh')
  const defaultModel = ref('')
  const streamingEnabled = ref(true)
  const defaultProviderId = ref('')
  const browserUseEnabled = ref(true)
  const browserUseMode = ref<'builtin' | 'chromium'>('builtin')
  const browserExecutablePath = ref('')
  const browserCdpUrl = ref('')

  async function load() {
    const s = await invoke<AppSettings>('settings.get')
    browserUseEnabled.value = s.browserUseEnabled ?? true
    browserUseMode.value = s.browserUseMode ?? 'builtin'
    browserExecutablePath.value = s.browserExecutablePath ?? ''
    browserCdpUrl.value = s.browserCdpUrl ?? ''
    theme.value = s.theme
    language.value = s.language
    defaultModel.value = s.defaultModel
    streamingEnabled.value = s.streamingEnabled
    defaultProviderId.value = s.defaultProviderId
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

  function applyTheme(t: 'light' | 'dark' | 'system') {
    const resolved = t === 'system'
      ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
      : t
    document.documentElement.setAttribute('data-theme', resolved)
    document.documentElement.classList.toggle('dark', resolved === 'dark')
  }

  applyTheme(theme.value)

  // Listen for system theme changes
  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (theme.value === 'system') applyTheme('system')
  })

  return { browserUseEnabled, browserUseMode, browserExecutablePath, browserCdpUrl, theme, language, defaultModel, streamingEnabled, defaultProviderId, load, save, applyTheme }
})
