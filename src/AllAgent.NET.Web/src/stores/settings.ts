import { defineStore, ref } from '@/lib/store'
import type { AppSettings } from '@/types'
import { invoke } from '@/bridge'
import { applyResolvedLanguage, currentLanguage, resolveLanguage } from '@/i18n'

export const useSettingsStore = defineStore('settings', () => {
  const theme = ref<AppSettings['theme']>('system')
  const language = ref<AppSettings['language']>('auto')
  const languageResolved = ref<AppSettings['languageResolved']>(currentLanguage())
  const defaultModel = ref('')
  const streamingEnabled = ref(true)
  const defaultProviderId = ref('')
  // AI commit-message drafting target; empty follows the composer's currently selected model.
  const gitCommitConfigId = ref('')
  const browserUseEnabled = ref(true)
  const browserUseMode = ref<AppSettings['browserUseMode']>('builtin')
  const browserExecutablePath = ref('')
  const browserCdpUrl = ref('')
  const loaded = ref(false)

  function prefersDark(): boolean {
    return typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches
  }

  function applyTheme(next: AppSettings['theme']) {
    const resolved = next === 'system' ? (prefersDark() ? 'dark' : 'light') : next
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
    language.value = settings.language ?? 'auto'
    const resolved = settings.languageResolved ?? resolveLanguage(language.value)
    languageResolved.value = resolved
    applyResolvedLanguage(resolved)
    defaultModel.value = settings.defaultModel ?? ''
    streamingEnabled.value = settings.streamingEnabled ?? true
    defaultProviderId.value = settings.defaultProviderId ?? ''
    gitCommitConfigId.value = settings.gitCommitConfigId ?? ''
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
      gitCommitConfigId: gitCommitConfigId.value,
    })
  }

  /** Language switches immediately, unlike the rest of the form which saves on demand. */
  async function setLanguage(next: AppSettings['language']) {
    const previous = language.value
    const previousResolved = languageResolved.value
    const resolved = next === 'auto' ? resolveLanguage('auto') : next
    language.value = next
    languageResolved.value = resolved
    applyResolvedLanguage(resolved)
    try {
      await save()
    } catch (error) {
      language.value = previous
      languageResolved.value = previousResolved
      applyResolvedLanguage(previousResolved)
      throw error
    }
  }

  /** Host-driven changes apply to every open page, including other workspaces. */
  function applyFromBridge(next: { language: AppSettings['language']; languageResolved: AppSettings['languageResolved'] }) {
    language.value = next.language
    languageResolved.value = next.languageResolved
    applyResolvedLanguage(next.languageResolved)
  }

  applyTheme(theme.value)

  if (typeof window.matchMedia === 'function') {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
      if (theme.value === 'system') applyTheme('system')
    })
  }

  return {
    theme, language, languageResolved, defaultModel, streamingEnabled, defaultProviderId, gitCommitConfigId,
    browserUseEnabled, browserUseMode, browserExecutablePath, browserCdpUrl,
    loaded, load, save, setLanguage, applyFromBridge, applyTheme,
  }
})
