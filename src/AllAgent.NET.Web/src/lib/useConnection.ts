import { useSyncExternalStore } from 'react'
import { connectionState, onConnectionChange, type ConnectionState } from '@/bridge'
import { t } from '@/i18n'

export function useConnection(): ConnectionState {
  return useSyncExternalStore(
    listener => onConnectionChange(() => listener()),
    () => connectionState()
  )
}

export function connectionLabel(state: ConnectionState): string {
  switch (state) {
    case 'native': return t('桌面后端 · 已连接', 'Desktop backend · connected')
    case 'connected': return t('本地后端 · 实时通道', 'Local backend · live channel')
    case 'connecting': return t('正在连接本地后端', 'Connecting to the local backend')
    case 'preview': return t('演示数据 · 未连接后端', 'Demo data · backend not connected')
    default: return t('未连接后端', 'Backend not connected')
  }
}
