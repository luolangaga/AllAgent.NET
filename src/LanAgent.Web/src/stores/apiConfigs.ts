import { defineStore } from '@/lib/store'
import { ref } from '@/lib/store'
import type { ApiConfig } from '@/types'
import { invoke } from '@/bridge'

export const useApiConfigsStore = defineStore('apiConfigs', () => {
  const configs = ref<ApiConfig[]>([])
  const testStatus = ref<Record<string, 'idle' | 'testing' | 'ok' | 'error'>>({})
  const loadError = ref<string>('')

  async function loadAll() {
    loadError.value = ''
    try {
      const data = await invoke<ApiConfig[]>('apiConfigs.list')
      console.log('[ApiConfigsStore] Loaded', data?.length ?? 0, 'configs')
      if (!Array.isArray(data)) {
        console.error('[ApiConfigsStore] apiConfigs.list returned non-array:', data)
        loadError.value = '服务器返回了无效数据格式'
        configs.value = []
        return
      }
      configs.value = data
    } catch (e: any) {
      console.error('[ApiConfigsStore] loadAll failed:', e)
      loadError.value = e.message ?? '加载失败'
      configs.value = []
      throw e
    }
  }

  async function save(config: Partial<ApiConfig> & { providerId: string; displayName: string; baseUrl: string; defaultModel: string }) {
    const url = new URL(config.baseUrl)
    if (!['http:', 'https:'].includes(url.protocol)) throw new Error('API 地址必须使用 HTTP 或 HTTPS')
    if (!Number.isFinite(config.temperature ?? 0.7) || (config.temperature ?? 0.7) < 0 || (config.temperature ?? 0.7) > 2) throw new Error('Temperature 必须在 0–2 之间')
    if (!Number.isInteger(config.maxTokens ?? 4096) || (config.maxTokens ?? 4096) <= 0) throw new Error('最大输出 Tokens 必须是正整数')
    if (config.extraHeadersJson?.trim()) { const headers = JSON.parse(config.extraHeadersJson); if (!headers || Array.isArray(headers) || typeof headers !== 'object') throw new Error('额外请求头必须是 JSON 对象') }
    const saved = await invoke<ApiConfig>('apiConfigs.save', {
      id: config.id ?? null,
      providerId: config.providerId,
      displayName: config.displayName,
      baseUrl: config.baseUrl,
      apiKey: config.apiKey ?? '',
      defaultModel: config.defaultModel,
      temperature: config.temperature ?? 0.7,
      maxTokens: config.maxTokens ?? 4096,
      isEnabled: config.isEnabled ?? true,
      extraHeadersJson: config.extraHeadersJson ?? null,
      thinkingEnabled: config.thinkingEnabled ?? false,
      thinkingBudget: config.thinkingBudget ?? 8000,
    })
    const idx = configs.value.findIndex(c => c.id === saved.id)
    if (idx >= 0) configs.value[idx] = saved
    else configs.value.push(saved)
    return saved
  }

  async function remove(id: string) {
    await invoke('apiConfigs.delete', { id })
    configs.value = configs.value.filter(c => c.id !== id)
  }

  async function testConnection(id: string) {
    testStatus.value[id] = 'testing'
    try {
      await invoke('apiConfigs.test', { id })
      testStatus.value[id] = 'ok'
    } catch {
      testStatus.value[id] = 'error'
    }
    setTimeout(() => { testStatus.value[id] = 'idle' }, 3000)
  }

  return { configs, testStatus, loadError, loadAll, save, remove, testConnection }
})
