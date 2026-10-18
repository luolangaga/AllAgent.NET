import { defineStore, ref } from '@/lib/store'
import type { AuthProviderInfo } from '@/types'
import { bridgeEvents, invoke } from '@/bridge'

// Account login state, mirroring the terminal's /login list. OAuth flows settle
// outside the RPC that starts them, so `auth.changed` triggers a refresh.
export const useAuthStore = defineStore('auth', () => {
  const providers = ref<AuthProviderInfo[]>([])
  const loading = ref(false)
  const loaded = ref(false)
  const available = ref(false)
  const busyId = ref<string | null>(null)

  async function loadAll() {
    loading.value = true
    try {
      providers.value = await invoke<AuthProviderInfo[]>('auth.providers')
      available.value = true
    } catch {
      available.value = false
    } finally {
      loading.value = false
      loaded.value = true
    }
  }

  async function login(id: string, newAccount = false) {
    busyId.value = id
    try {
      await invoke('auth.login', { id, newAccount })
      await loadAll()
    } finally {
      busyId.value = null
    }
  }

  async function loginInput(id: string, secret: string) {
    busyId.value = id
    try {
      await invoke('auth.loginInput', { id, secret })
      await loadAll()
    } finally {
      busyId.value = null
    }
  }

  async function logout(id: string) {
    busyId.value = id
    try {
      await invoke('auth.logout', { id })
      await loadAll()
    } finally {
      busyId.value = null
    }
  }

  bridgeEvents.on('auth.changed', () => { void loadAll().catch(() => {}) })

  return { providers, loading, loaded, available, busyId, loadAll, login, loginInput, logout }
})
