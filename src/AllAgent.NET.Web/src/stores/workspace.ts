import { defineStore, ref } from '@/lib/store'
import type { SessionControlState, WorkspaceEntry, WorkspaceInfo } from '@/types'
import { bridgeEvents, invoke } from '@/bridge'

// Workspace control only exists on the terminal host (the /web-ui server).
// Desktop builds report `available: false` and the UI hides these controls.
export const useWorkspaceStore = defineStore('workspace', () => {
  const workspace = ref('')
  const dataDirectory = ref('')
  const sharedDirectory = ref('')
  const workspacesRoot = ref('')
  const list = ref<WorkspaceEntry[]>([])
  const switching = ref(false)
  const controls = ref<SessionControlState | null>(null)
  const available = ref(false)
  const switcherOpen = ref(false)

  function openSwitcher() { switcherOpen.value = true }
  function closeSwitcher() { switcherOpen.value = false }

  function apply(info: WorkspaceInfo) {
    workspace.value = info.workspace
    dataDirectory.value = info.dataDirectory
    sharedDirectory.value = info.sharedDirectory
    workspacesRoot.value = info.workspacesRoot ?? workspacesRoot.value
  }

  async function load() {
    try {
      apply(await invoke<WorkspaceInfo>('workspace.get'))
      controls.value = await invoke<SessionControlState>('session.controls')
      available.value = true
      list.value = await invoke<WorkspaceEntry[]>('workspaces.list')
    } catch {
      available.value = false
    }
  }

  /** Refresh the live control state. `running` flips with every task start and finish, so anything
   * locked while a task runs must ask again when the run list changes instead of trusting a stale value. */
  async function loadControls() {
    if (!available.value) return
    try {
      controls.value = await invoke<SessionControlState>('session.controls')
    } catch {
      // The host went away; the next reconnect reloads everything.
    }
  }

  async function switchTo(path: string, create = false) {
    switching.value = true
    try {
      apply(await invoke<WorkspaceInfo>('workspace.switch', { path, create }))
      await load()
    } finally {
      switching.value = false
    }
  }

  /** Ask the host to open its native folder dialog; the pick arrives as a bridge event. */
  async function openFolderDialog(): Promise<void> {
    await invoke<boolean>('workspace.pickFolder')
  }

  /** Create 文档\AllAgent.NET\<name> on the host and switch to it. */
  async function create(name: string) {
    switching.value = true
    try {
      apply(await invoke<WorkspaceInfo>('workspace.create', { name }))
      await load()
    } finally {
      switching.value = false
    }
  }

  async function setPermission(mode: string) {
    controls.value = await invoke<SessionControlState>('session.setPermission', { mode })
  }

  async function setSandbox(mode: string) {
    controls.value = await invoke<SessionControlState>('session.setSandbox', { mode })
  }

  bridgeEvents.on('workspace.changed', data => {
    workspace.value = data.workspace
    void load()
  })

  // Run and job changes are the only things that move `running`: refresh the control state with them
  // so a finished task unlocks the sandbox switch immediately instead of at the next reload.
  bridgeEvents.on('runs.changed', () => { void loadControls() })
  bridgeEvents.on('tasks.updated', () => { void loadControls() })

  return { workspace, dataDirectory, sharedDirectory, workspacesRoot, list, switching, controls, available, switcherOpen, openSwitcher, closeSwitcher, load, switchTo, openFolderDialog, create, setPermission, setSandbox }
})
