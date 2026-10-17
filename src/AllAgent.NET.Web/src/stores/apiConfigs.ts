import { defineStore, ref } from '@/lib/store'
import type { ApiConfig, ReasoningOptions } from '@/types'
import { invoke } from '@/bridge'

export type TestStatus = 'idle' | 'testing' | 'ok' | 'error'

export const useApiConfigsStore = defineStore('apiConfigs', () => {
  const configs = ref<ApiConfig[]>([])
  const testStatus = ref<Record<string, TestStatus>>({})
  const loaded = ref(false)

  async function loadAll() {
    const data = await invoke<ApiConfig[]>('apiConfigs.list')
    if (!Array.isArray(data)) throw new Error('服务端返回了无效的模型配置格式')
    configs.value = data
    loaded.value = true
  }

  async function save(config: Partial<ApiConfig> & { providerId: string; displayName: string; baseUrl: string; defaultModel: string }) {
    const url = new URL(config.baseUrl)
    if (!['http:', 'https:'].includes(url.protocol)) throw new Error('API 地址必须使用 HTTP 或 HTTPS')
    const temperature = config.temperature ?? 0.7
    if (!Number.isFinite(temperature) || temperature < 0 || temperature > 2) throw new Error('Temperature 必须在 0–2 之间')
    const maxTokens = config.maxTokens ?? 4096
    if (!Number.isInteger(maxTokens) || maxTokens <= 0) throw new Error('最大输出 Tokens 必须是正整数')
    if (config.extraHeadersJson?.trim()) {
      const headers: unknown = JSON.parse(config.extraHeadersJson)
      if (!headers || Array.isArray(headers) || typeof headers !== 'object') throw new Error('额外请求头必须是 JSON 对象')
    }
    const saved = await invoke<ApiConfig>('apiConfigs.save', {
      id: config.id ?? null,
      providerId: config.providerId,
      displayName: config.displayName,
      baseUrl: config.baseUrl,
      apiKey: config.apiKey ?? '',
      defaultModel: config.defaultModel,
      temperature,
      maxTokens,
      isEnabled: config.isEnabled ?? true,
      extraHeadersJson: config.extraHeadersJson ?? null,
      thinkingEnabled: config.thinkingEnabled ?? false,
      thinkingBudget: config.thinkingBudget ?? 8000,
      contextWindowTokens: config.contextWindowTokens ?? 100000,
      protocol: config.protocol ?? '',
      reasoningProtocol: config.reasoningProtocol ?? 'auto',
      reasoningEffort: config.reasoningEffort ?? 'default',
      models: config.models ?? [],
      apiKeyEnvironmentVariable: config.apiKeyEnvironmentVariable ?? null,
      authenticationProviderId: config.authenticationProviderId ?? null,
    })
    const index = configs.value.findIndex(item => item.id === saved.id)
    if (index >= 0) configs.value = configs.value.map(item => (item.id === saved.id ? saved : item))
    else configs.value = [...configs.value, saved]
    return saved
  }

  async function remove(id: string) {
    await invoke('apiConfigs.delete', { id })
    configs.value = configs.value.filter(item => item.id !== id)
  }

  async function testConnection(id: string) {
    testStatus.value = { ...testStatus.value, [id]: 'testing' }
    try {
      await invoke('apiConfigs.test', { id })
      testStatus.value = { ...testStatus.value, [id]: 'ok' }
    } catch (error) {
      testStatus.value = { ...testStatus.value, [id]: 'error' }
      throw error
    } finally {
      window.setTimeout(() => {
        if (testStatus.value[id] !== 'testing') testStatus.value = { ...testStatus.value, [id]: 'idle' }
      }, 4000)
    }
  }

  /** Pulls the provider's model catalog and persists it on the config. */
  async function refreshModels(id: string) {
    const updated = await invoke<ApiConfig>('apiConfigs.refreshModels', { id })
    configs.value = configs.value.map(item => (item.id === updated.id ? updated : item))
    return updated
  }

  async function reasoningOptions(id: string) {
    return invoke<ReasoningOptions>('apiConfigs.reasoning', { id })
  }

  return { configs, testStatus, loaded, loadAll, save, remove, testConnection, refreshModels, reasoningOptions }
})
