import { Alert } from './ui/alert'
import { useStore } from '@/lib/store'
import { useNoticesStore } from '@/stores/ui'

export function Notices() {
  const notices = useStore(useNoticesStore)
  return <>
    {notices.notices.map(notice => <Alert
      appearance={notice.appearance}
      key={notice.id}
      message={notice.message}
      onDismiss={() => notices.dismiss(notice.id)}
      position="top-right"
      timeout={notice.appearance === 'destructive' ? 8000 : 4000}
      title={notice.title}
      titleLines="none"
      variant="toast"
      width={420}
    />)}
  </>
}
