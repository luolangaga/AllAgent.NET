import { defineStore, ref } from '@/lib/store'

/** The three floating bubbles the topbar opens: workspace files, Git and the terminal. */
export type PanelId = 'files' | 'git' | 'terminal'

const OPEN_KEY = 'allagent.panels.open'

const DEFAULT_OPEN: Record<PanelId, boolean> = { files: false, git: false, terminal: false }

function readOpen(): Record<PanelId, boolean> {
  try {
    const stored = JSON.parse(localStorage.getItem(OPEN_KEY) ?? '{}') as Partial<Record<PanelId, boolean>>
    return { files: stored.files ?? false, git: stored.git ?? false, terminal: stored.terminal ?? false }
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
