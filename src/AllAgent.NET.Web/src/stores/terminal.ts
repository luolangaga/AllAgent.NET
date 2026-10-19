import { defineStore, ref } from '@/lib/store'
import { bridgeEvents, invoke } from '@/bridge'
import type { TerminalSession, TerminalShell } from '@/types'

const SHELL_KEY = 'allagent.terminal.shell'

export const useTerminalStore = defineStore('terminal', () => {
  const shells = ref<TerminalShell[]>([])
  const sessions = ref<TerminalSession[]>([])
  const activeId = ref<string | null>(null)
  const defaultShell = ref(localStorage.getItem(SHELL_KEY) ?? '')
  const loaded = ref(false)
  const available = ref(true)
  /** Bumped whenever the panel should take keyboard focus (open, tab click, Ctrl+`). */
  const focusTick = ref(0)

  async function load(): Promise<void> {
    try {
      shells.value = await invoke<TerminalShell[]>('terminal.shells')
      sessions.value = await invoke<TerminalSession[]>('terminal.list')
      if (!defaultShell.value && shells.value.length) defaultShell.value = shells.value[0].id
      if (!activeId.value && sessions.value.length) activeId.value = sessions.value[0].id
      available.value = true
    } catch {
      available.value = false
    } finally {
      loaded.value = true
    }
  }

  /** Re-reads the host-side tab list (terminal.changed, or after a reconnect). */
  async function sync(): Promise<void> {
    try {
      sessions.value = await invoke<TerminalSession[]>('terminal.list')
      if (activeId.value && !sessions.value.some(session => session.id === activeId.value)) activeId.value = sessions.value[0]?.id ?? null
      if (!activeId.value && sessions.value.length) activeId.value = sessions.value[0].id
    } catch { /* keep the last list while the backend is away */ }
  }

  async function create(options: { shell?: string; cwd?: string } = {}): Promise<TerminalSession> {
    const session = await invoke<TerminalSession>('terminal.create', {
      shell: options.shell ?? defaultShell.value ?? null,
      cwd: options.cwd ?? null,
      cols: 120,
      rows: 30,
    })
    sessions.value = [...sessions.value.filter(item => item.id !== session.id), session]
    activeId.value = session.id
    focusTick.value++
    return session
  }

  async function close(id: string): Promise<void> {
    try {
      await invoke<boolean>('terminal.close', { id })
    } finally {
      sessions.value = sessions.value.filter(item => item.id !== id)
      if (activeId.value === id) activeId.value = sessions.value[0]?.id ?? null
    }
  }

  function activate(id: string): void {
    activeId.value = id
    focusTick.value++
  }

  function requestFocus(): void {
    focusTick.value++
  }

  function setDefaultShell(id: string): void {
    defaultShell.value = id
    try { localStorage.setItem(SHELL_KEY, id) } catch { /* storage may be unavailable */ }
  }

  function markExit(id: string, exitCode: number | null): void {
    sessions.value = sessions.value.map(session => session.id === id ? { ...session, alive: false, exitCode } : session)
  }

  return {
    shells, sessions, activeId, defaultShell, loaded, available, focusTick,
    load, sync, create, close, activate, requestFocus, setDefaultShell, markExit,
  }
})

// Exit notices and tab-list changes arrive as events; the store owns both.
bridgeEvents.on('terminal.exit', payload => {
  useTerminalStore().markExit(payload.streamId, payload.exitCode ?? null)
})

bridgeEvents.on('terminal.changed', () => {
  void useTerminalStore().sync()
})
