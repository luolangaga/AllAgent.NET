import { defineStore, ref } from '@/lib/store'
import type { PowerShellCommandPage, PowerShellModuleInfo, PowerShellPackageInfo, PowerShellStoreStatus } from '@/types'
import { invoke } from '@/bridge'

// Installing a module downloads it with its dependencies and import-validates every one of
// them in the bundled SDK worker, so the two slow calls get a much longer bridge timeout.
const READ_TIMEOUT = 10 * 60 * 1000
const INSTALL_TIMEOUT = 15 * 60 * 1000

export const usePowerShellStore = defineStore('powershell', () => {
  const available = ref(false)
  const loaded = ref(false)
  const workerAvailable = ref(false)
  const workerPath = ref('')
  const root = ref('')
  const modules = ref<PowerShellModuleInfo[]>([])
  const loading = ref(false)
  const installing = ref(false)
  const searching = ref(false)
  const query = ref('')
  const packages = ref<PowerShellPackageInfo[]>([])
  const searched = ref(false)
  const commands = ref<PowerShellCommandPage | null>(null)
  const commandsBusy = ref(false)
  const commandsError = ref('')

  function apply(status: PowerShellStoreStatus) {
    workerAvailable.value = status.workerAvailable
    workerPath.value = status.workerPath ?? ''
    root.value = status.root ?? ''
    modules.value = status.modules ?? []
  }

  async function load() {
    try {
      apply(await invoke<PowerShellStoreStatus>('powershell.status', undefined, READ_TIMEOUT))
      available.value = true
    } catch {
      available.value = false
    } finally {
      loaded.value = true
    }
  }

  /** Re-imports every module in the worker and rewrites the verified catalog. */
  async function refresh() {
    loading.value = true
    try {
      apply(await invoke<PowerShellStoreStatus>('powershell.refresh', undefined, READ_TIMEOUT))
      available.value = true
    } finally {
      loading.value = false
    }
  }

  async function search(text: string) {
    searching.value = true
    query.value = text
    try {
      packages.value = await invoke<PowerShellPackageInfo[]>('powershell.search', { query: text }, READ_TIMEOUT)
      searched.value = true
    } finally {
      searching.value = false
    }
  }

  /** Installs the exact name/version and returns the refreshed catalog. */
  async function install(name: string, version?: string | null) {
    installing.value = true
    try {
      apply(await invoke<PowerShellStoreStatus>('powershell.install', { name, version: version || null }, INSTALL_TIMEOUT))
      available.value = true
    } finally {
      installing.value = false
    }
  }

  /** The command list belongs to the modal that asked for it, so a failure is reported there. */
  async function readCommands(name: string, version: string) {
    commands.value = null
    commandsError.value = ''
    commandsBusy.value = true
    try {
      commands.value = await invoke<PowerShellCommandPage>('powershell.commands', { name, version, limit: 100 }, READ_TIMEOUT)
    } catch (error) {
      commandsError.value = error instanceof Error ? error.message : String(error)
    } finally {
      commandsBusy.value = false
    }
  }

  return {
    available, loaded, workerAvailable, workerPath, root, modules, loading, installing, searching,
    query, packages, searched, commands, commandsBusy, commandsError, load, refresh, search, install, readCommands,
  }
})
