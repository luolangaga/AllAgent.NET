import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { X } from 'lucide-react'
import { Button } from '@/components/ui/b-button'
import { useStore } from '@/lib/store'
import { usePanelsStore, type PanelId } from '@/stores/panels'

interface Rect { x: number; y: number; width: number; height: number }
interface Size { width: number; height: number }

const MARGIN = 10

function readRect(id: PanelId, fallback: Rect): Rect {
  try {
    const stored = JSON.parse(localStorage.getItem(`allagent.panel.${id}`) ?? 'null') as Rect | null
    if (stored && [stored.x, stored.y, stored.width, stored.height].every(value => typeof value === 'number' && Number.isFinite(value))) return stored
  } catch { /* fall through to the default */ }
  return fallback
}

function useViewport(): Size {
  const [size, setSize] = useState<Size>(() => ({ width: window.innerWidth, height: window.innerHeight }))
  useEffect(() => {
    const update = () => setSize({ width: window.innerWidth, height: window.innerHeight })
    window.addEventListener('resize', update)
    return () => window.removeEventListener('resize', update)
  }, [])
  return size
}

/** Keeps a window reachable: at least a strip of header stays on screen. */
function clampRect(rect: Rect, viewport: Size, minWidth: number, minHeight: number): Rect {
  const width = Math.min(Math.max(rect.width, minWidth), Math.max(minWidth, viewport.width - MARGIN * 2))
  const height = Math.min(Math.max(rect.height, minHeight), Math.max(minHeight, viewport.height - MARGIN * 2))
  const x = Math.min(Math.max(rect.x, MARGIN + 40 - width), viewport.width - 80)
  const y = Math.min(Math.max(rect.y, MARGIN), viewport.height - 48)
  return { x, y, width, height }
}

/**
 * The floating bubble shell every topbar toggle opens: draggable by its header, resizable from
 * the corner, stacked above the page but below modals and the command palette, and persisted
 * per panel. On narrow screens it becomes a near-fullscreen overlay instead.
 */
export function FloatingPanel({ id, title, icon, headerExtra, children, defaultRect, minWidth = 320, minHeight = 240, onClose }: {
  id: PanelId
  title: ReactNode
  icon: ReactNode
  headerExtra?: ReactNode
  children: ReactNode
  defaultRect: Rect
  minWidth?: number
  minHeight?: number
  onClose: () => void
}) {
  const panels = useStore(usePanelsStore)
  const viewport = useViewport()
  const narrow = viewport.width <= 820
  const [rect, setRect] = useState<Rect>(() => clampRect(readRect(id, defaultRect), { width: window.innerWidth, height: window.innerHeight }, minWidth, minHeight))
  const rectRef = useRef(rect)
  rectRef.current = rect

  // A shrinking viewport must never strand the window off screen.
  useEffect(() => {
    setRect(current => {
      const next = clampRect(current, viewport, minWidth, minHeight)
      return next.x === current.x && next.y === current.y && next.width === current.width && next.height === current.height ? current : next
    })
  }, [viewport.width, viewport.height, minWidth, minHeight])

  const persist = useCallback(() => {
    try { localStorage.setItem(`allagent.panel.${id}`, JSON.stringify(rectRef.current)) } catch { /* storage may be unavailable */ }
  }, [id])

  function startDrag(event: React.PointerEvent<HTMLElement>) {
    if (narrow) return
    if ((event.target as HTMLElement).closest('button, input, textarea, select, a')) return
    event.preventDefault()
    const offsetX = event.clientX - rect.x
    const offsetY = event.clientY - rect.y
    const move = (moveEvent: PointerEvent) => {
      setRect(current => clampRect({ ...current, x: moveEvent.clientX - offsetX, y: Math.max(4, moveEvent.clientY - offsetY) }, { width: window.innerWidth, height: window.innerHeight }, minWidth, minHeight))
    }
    const up = () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
      persist()
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
  }

  function startResize(event: React.PointerEvent<HTMLElement>) {
    if (narrow) return
    event.preventDefault()
    event.stopPropagation()
    const startX = event.clientX
    const startY = event.clientY
    const initial = rectRef.current
    const move = (moveEvent: PointerEvent) => {
      setRect(clampRect({ ...initial, width: initial.width + moveEvent.clientX - startX, height: initial.height + moveEvent.clientY - startY }, { width: window.innerWidth, height: window.innerHeight }, minWidth, minHeight))
    }
    const up = () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
      persist()
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
  }

  const style = narrow
    ? { zIndex: panels.zIndex(id) }
    : { left: rect.x, top: rect.y, width: rect.width, height: rect.height, zIndex: panels.zIndex(id) }

  return <aside
    aria-label={typeof title === 'string' ? title : undefined}
    className={`floating-panel${narrow ? ' is-narrow' : ''}`}
    onPointerDown={() => panels.raise(id)}
    role="dialog"
    style={style}
  >
    <header className="panel-head" onPointerDown={startDrag}>
      <span className="panel-title">{icon}{title}</span>
      <div className="panel-head-actions">
        {headerExtra}
        <Button iconLabel="关闭面板" onClick={onClose} size="icon-xs" variant="ghost"><X size={13} /></Button>
      </div>
    </header>
    <div className="panel-body">{children}</div>
    {!narrow ? <div aria-hidden className="panel-resize" onPointerDown={startResize} /> : null}
  </aside>
}
