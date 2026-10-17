import { useSyncExternalStore } from 'react'
import { connectionState, onConnectionChange, type ConnectionState } from '@/bridge'

export function useConnection(): ConnectionState {
  return useSyncExternalStore(
    listener => onConnectionChange(() => listener()),
    () => connectionState()
  )
}

export function connectionLabel(state: ConnectionState): string {
  switch (state) {
    case 'native': return '桌面后端 · 已连接'
    case 'connected': return '本地后端 · 实时通道'
    case 'connecting': return '正在连接本地后端'
    case 'preview': return '演示数据 · 未连接后端'
    default: return '未连接后端'
  }
}
