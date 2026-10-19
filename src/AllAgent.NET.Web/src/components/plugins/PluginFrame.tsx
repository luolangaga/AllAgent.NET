import { useEffect, useMemo, useRef, useState } from 'react'
import { RefreshCw } from 'lucide-react'
import { Button } from '@/components/ui/b-button'
import { useStore } from '@/lib/store'
import { useDarkMode } from '@/lib/useDarkMode'
import { createFrameTarget, registerFrame, type FrameHandle, type FrameView } from '@/lib/pluginRuntime'
import { useConversationsStore } from '@/stores/conversations'
import { useWorkspaceStore } from '@/stores/workspace'
import type { PluginWebInfo } from '@/types'

// Frames are same-origin with their own assets but a different origin than the app page: they can
// fetch their files and use their own storage, yet can never touch the host DOM — and the Origin
// check on /bridge blocks direct socket access. allow-same-origin is therefore safe here.
const SANDBOX = 'allow-scripts allow-same-origin allow-forms allow-popups allow-popups-to-escape-sandbox allow-downloads'
const READY_TIMEOUT_MS = 15000

export function PluginFrame({
  plugin, view, entry, assetBaseUrl, autoHeight, minHeight, maxHeight, lazy, refreshKey, className, onHandle,
}: {
  plugin: PluginWebInfo
  view: FrameView
  entry?: string
  assetBaseUrl?: string | null
  autoHeight?: boolean
  minHeight?: number
  maxHeight?: number
  lazy?: boolean
  refreshKey?: number
  className?: string
  onHandle?: (handle: FrameHandle | null) => void
}) {
  const dark = useDarkMode()
  const conversations = useStore(useConversationsStore)
  const workspace = useStore(useWorkspaceStore)
  const element = useRef<HTMLIFrameElement>(null)
  const handle = useRef<FrameHandle | null>(null)
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading')
  const [height, setHeight] = useState<number | null>(null)
  const [attempt, setAttempt] = useState(0)

  const target = useMemo(
    () => createFrameTarget({ plugin, view, entry, assetBaseUrl }),
    // The token is bound to one load attempt; a retry or a host change issues a fresh frame.
    [plugin.packageId, plugin.version, plugin.devUrl, view.kind, view.id, entry, assetBaseUrl, attempt, refreshKey],
  )

  useEffect(() => {
    const iframe = element.current
    if (!iframe || !target || !target.origin) return
    setStatus('loading')
    const registered = registerFrame(iframe, {
      plugin,
      view,
      entry,
      assetBaseUrl,
      context: { workspace: workspace.workspace || undefined, conversationId: conversations.activeId },
      onResize: autoHeight ? value => setHeight(clamp(value, minHeight ?? 120, maxHeight ?? 640)) : undefined,
      onReady: () => setStatus('ready'),
    }, target)
    handle.current = registered
    onHandle?.(registered)
    const timer = window.setTimeout(() => setStatus(current => current === 'loading' ? 'error' : current), READY_TIMEOUT_MS)
    return () => {
      window.clearTimeout(timer)
      registered.dispose()
      handle.current = null
      onHandle?.(null)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [target?.src, attempt, refreshKey])

  useEffect(() => { handle.current?.sendTheme() }, [dark])
  useEffect(() => {
    handle.current?.setContext({ workspace: workspace.workspace || undefined, conversationId: conversations.activeId })
  }, [workspace.workspace, conversations.activeId])

  if (!target) {
    return <div className={`plugin-frame-error ${className ?? ''}`}>
      <strong>插件界面暂不可用</strong>
      <span>宿主没有提供插件资产地址：插件可能已停用、Web 界面被关闭，或资产服务未启动。</span>
    </div>
  }

  const passive = !target.origin
  return <div className={`plugin-frame-shell ${className ?? ''}`} data-plugin={plugin.packageId}>
    <iframe
      className="plugin-frame"
      loading={lazy ? 'lazy' : 'eager'}
      onLoad={() => { if (passive) setStatus('ready') }}
      ref={element}
      sandbox={SANDBOX}
      src={target.src}
      style={autoHeight ? { height: `${height ?? minHeight ?? 160}px` } : undefined}
      title={`${plugin.name} · ${view.kind}`}
    />
    {status === 'loading' ? <div className="plugin-frame-loading" role="status">
      <span aria-hidden className="plugin-frame-spinner" />正在加载插件界面…
    </div> : null}
    {status === 'error' ? <div className="plugin-frame-error" role="alert">
      <strong>插件界面加载失败</strong>
      <span>界面未在 {Math.round(READY_TIMEOUT_MS / 1000)} 秒内完成握手。可以重试，或在插件页关闭该插件的 Web 界面。</span>
      <Button icon={<RefreshCw size={14} />} onClick={() => setAttempt(value => value + 1)} size="sm" variant="outline">重试</Button>
    </div> : null}
  </div>
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(value, maximum))
}
