import { useEffect, useState } from 'react'
import { useStore } from '@/lib/store'
import { useSettingsStore } from '@/stores/settings'

/** Resolved light/dark state: an explicit theme wins, 'system' follows the OS. */
export function useDarkMode(): boolean {
  const settings = useStore(useSettingsStore)
  const [systemDark, setSystemDark] = useState(() => window.matchMedia('(prefers-color-scheme: dark)').matches)
  useEffect(() => {
    const query = window.matchMedia('(prefers-color-scheme: dark)')
    const update = () => setSystemDark(query.matches)
    query.addEventListener('change', update)
    return () => query.removeEventListener('change', update)
  }, [])
  return settings.theme === 'dark' || (settings.theme === 'system' && systemDark)
}
