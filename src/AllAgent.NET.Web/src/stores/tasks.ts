import { defineStore, ref, computed } from '@/lib/store'
import type { BackgroundTask } from '@/types'
import { bridgeEvents, invoke } from '@/bridge'
import { taskIsActive } from '@/lib/format'

export const useTasksStore = defineStore('tasks', () => {
  const tasks = ref<BackgroundTask[]>([])
  const loading = ref(false)
  const loaded = ref(false)

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

  async function cancel(id: string) {
    await invoke('tasks.cancel', { id })
    await loadAll()
  }

  async function detail(id: string) {
    return invoke<BackgroundTask>('tasks.get', { id })
  }

  // Background children change state without a user-initiated stream; keep the
  // list and the sidebar badge in sync.
  bridgeEvents.on('tasks.updated', () => { void loadAll().catch(() => {}) })

  return { tasks, loading, loaded, activeTasks, loadAll, cancel, detail }
})
