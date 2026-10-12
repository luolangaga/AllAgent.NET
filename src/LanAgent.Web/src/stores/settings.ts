import { defineStore } from 'pinia'
import { ref, watch } from 'vue'
import type { AppSettings } from '@/types'
import { invoke } from '@/bridge'

export const useSettingsStore = defineStore('settings', () => {
  const theme = ref<'light' | 'dark' | 'system'>('system')
  const language = ref<'zh' | 'en'>('zh')
  const defaultModel = ref('')
  const streamingEnabled = ref(true)
  const defaultProviderId = ref('')

  async function load() {
    const s = await invoke<AppSettings>('settings.get')
    theme.value = s.theme
    language.value = s.language
    defaultModel.value = s.defaultModel
    streamingEnabled.value = s.streamingEnabled
    defaultProviderId.value = s.defaultProviderId
  }

  async function save() {
    await invoke('settings.save', {
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
  }

  watch(theme, applyTheme, { immediate: true })

  // Listen for system theme changes
  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (theme.value === 'system') applyTheme('system')
  })

  return { theme, language, defaultModel, streamingEnabled, defaultProviderId, load, save, applyTheme }
})
