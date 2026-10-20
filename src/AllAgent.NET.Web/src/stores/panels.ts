import { defineStore, ref } from '@/lib/store'

/** Floating bubbles the topbar opens: the three host bubbles plus any plugin panel key. */
export type PanelId = string

const OPEN_KEY = 'allagent.panels.open'

const DEFAULT_OPEN: Record<PanelId, boolean> = { files: false, git: false, terminal: false }

function readOpen(): Record<PanelId, boolean> {
  try {
    const stored = JSON.parse(localStorage.getItem(OPEN_KEY) ?? '{}') as Record<string, unknown>
    const values: Record<string, boolean> = {}
    for (const [key, value] of Object.entries(stored)) if (typeof value === 'boolean') values[key] = value
    return { ...DEFAULT_OPEN, ...values }
  } catch {
    return { ...DEFAULT_OPEN }
  }
}

export const usePanelsStore = defineStore('panels', () => {
  const open = ref<Record<PanelId, boolean>>(readOpen())
  // Raise-on-interaction order; gives each bubble a stable stacking above the content.
  const order = ref<PanelId[]>(['files', 'git', 'terminal'])

  function persist() {
    try { localStorage.setItem(OPEN_KEY, JSON.stringify(open.value)) } catch { /* storage may be unavailable */ }
  }

  function raise(id: PanelId) {
    if (order.value[order.value.length - 1] === id) return
    order.value = [...order.value.filter(item => item !== id), id]
  }

  function openPanel(id: PanelId) {
    open.value = { ...open.value, [id]: true }
    raise(id)
    persist()
  }

  function closePanel(id: PanelId) {
    open.value = { ...open.value, [id]: false }
    persist()
  }

  function togglePanel(id: PanelId) {
    if (open.value[id]) closePanel(id)
    else openPanel(id)
  }

  function zIndex(id: PanelId): number {
    return 45 + Math.max(0, order.value.indexOf(id))
  }

  return { open, order, raise, openPanel, closePanel, togglePanel, zIndex }
})
