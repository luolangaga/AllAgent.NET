import { defineStore, ref } from '@/lib/store'

export type NoticeAppearance = 'default' | 'success' | 'info' | 'destructive' | 'warning'

export interface Notice {
  id: string
  title: string
  message?: string
  appearance: NoticeAppearance
}

export const useNoticesStore = defineStore('notices', () => {
  const notices = ref<Notice[]>([])

  function notify(title: string, message?: string, appearance: NoticeAppearance = 'default') {
    notices.value = [...notices.value, { id: crypto.randomUUID(), title, message, appearance }]
  }

  function report(error: unknown) {
    notify('操作未完成', error instanceof Error ? error.message : String(error), 'destructive')
  }

  function dismiss(id: string) {
    notices.value = notices.value.filter(notice => notice.id !== id)
  }

  return { notices, notify, report, dismiss }
})
