import { defineStore, ref, computed } from '@/lib/store'
import type { BackgroundTask } from '@/types'
import { bridgeEvents, invoke } from '@/bridge'
import { taskIsActive } from '@/lib/format'

export const useTasksStore = defineStore('tasks', () => {
  const tasks = ref<BackgroundTask[]>([])
  const loading = ref(false)
  const loaded = ref(false)
  // Global task-detail modal: any surface (Tasks page, chat sub-agent cards) opens it by id,
  // and the modal always renders the live task from this store.
  const detailId = ref<string | null>(null)

  const activeTasks = computed(() => tasks.value.filter(task => taskIsActive(task.state)))

  async function loadAll() {
    loading.value = true
    try {
      tasks.value = await invoke<BackgroundTask[]>('tasks.list')
      loaded.value = true
    } finally {
      loading.value = false
    }
  }

  // Silent refresh for polling: keeps the busy spinner out of the way and coalesces bursts
  // (every live sub-agent card and this store can ask for the same refresh).
  let refreshing = false
  async function refresh() {
    if (refreshing) return
    refreshing = true
    try {
      tasks.value = await invoke<BackgroundTask[]>('tasks.list')
      loaded.value = true
    } finally {
      refreshing = false
    }
  }

  // The bridge only pushes `tasks.updated` for finished background children, so live progress
  // (steps, state, output) needs polling while anything is running — the TUI polls its own
  // revision counter the same way.
  window.setInterval(() => { if (activeTasks.value.length) void refresh().catch(() => {}) }, 1500)

  async function cancel(id: string) {
    await invoke('tasks.cancel', { id })
    await loadAll()
  }

  async function detail(id: string) {
    return invoke<BackgroundTask>('tasks.get', { id })
  }

  function openDetail(id: string) { detailId.value = id }
  function closeDetail() { detailId.value = null }

  // Background children change state without a user-initiated stream; keep the
  // list and the sidebar badge in sync.
  bridgeEvents.on('tasks.updated', () => { void loadAll().catch(() => {}) })

  return { tasks, loading, loaded, activeTasks, detailId, loadAll, refresh, cancel, detail, openDetail, closeDetail }
})
