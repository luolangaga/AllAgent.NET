import { useEffect, useId, useRef, useState } from 'react'
import { AnimatePresence, motion } from 'motion/react'
import { Check, ChevronDown, Eye, ShieldCheck, ShieldOff, ShieldQuestion, Zap } from 'lucide-react'
import { useStore } from '@/lib/store'
import { permissionOptions, permissionHint, permissionLabel } from '@/lib/permissions'
import { useNoticesStore } from '@/stores/ui'
import { useWorkspaceStore } from '@/stores/workspace'

const PERMISSION_ICONS: Record<string, typeof ShieldCheck> = {
  restricted: ShieldCheck,
  ask: ShieldQuestion,
  full: ShieldOff,
  plan: Eye,
  simple: Zap,
}

/**
 * Session permission switch for the composer. The host keeps the mode as its single source of
 * truth, so the chip mirrors `session.controls` and reloads it from every reply. The mode is live
 * on the host: switching mid-run takes effect on the next tool call, so the chip never locks and
 * an active task is never a reason to refuse a switch.
 */
export function PermissionPicker() {
  const workspace = useStore(useWorkspaceStore)
  const notices = useStore(useNoticesStore)
  const [open, setOpen] = useState(false)
  const [hovered, setHovered] = useState<string | null>(null)
  const hoverLayoutId = useId()
  const containerRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const handlePointerDown = (event: PointerEvent) => {
      if (containerRef.current?.contains(event.target as Node)) return
      setOpen(false)
    }
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { event.stopPropagation(); setOpen(false) }
    }
    document.addEventListener('pointerdown', handlePointerDown)
    document.addEventListener('keydown', handleKeyDown)
    return () => {
      document.removeEventListener('pointerdown', handlePointerDown)
      document.removeEventListener('keydown', handleKeyDown)
    }
  }, [open])

  const controls = workspace.controls
  if (!workspace.available || !controls) return null

  const mode = controls.permissionMode
  const Current = PERMISSION_ICONS[mode] ?? ShieldCheck

  async function choose(value: string) {
    setOpen(false)
    if (value === mode) return
    try {
      await workspace.setPermission(value)
      notices.notify('权限已切换', permissionLabel(value), 'success')
    } catch (error) {
      notices.report(error)
    }
  }

  return <div className="permission-picker" ref={containerRef}>
    <button
      aria-expanded={open}
      aria-haspopup="menu"
      aria-label={`权限模式：${permissionLabel(mode, controls.permissionLabel)}`}
      className="permission-chip"
      data-mode={mode}
      onClick={() => {
        setHovered(null)
        setOpen(previous => !previous)
      }}
      title={permissionHint(mode)}
      type="button"
    >
      <span className="permission-chip-icon"><Current size={13} /></span>
      <span className="permission-chip-label">{permissionLabel(mode, controls.permissionLabel)}</span>
      <ChevronDown aria-hidden="true" className={open ? 'is-open' : ''} size={12} />
    </button>
    <AnimatePresence>
      {open ? <motion.div
        animate={{ opacity: 1, scale: 1, y: 0 }}
        aria-label="选择权限模式"
        className="permission-menu"
        exit={{ opacity: 0, scale: 0.97, y: 4 }}
        initial={{ opacity: 0, scale: 0.96, y: 6 }}
        onMouseLeave={() => setHovered(null)}
        role="menu"
        transition={{ duration: 0.16, ease: 'easeOut' }}
      >
        <div className="permission-menu-head">
          <strong>会话权限模式</strong>
          <span>切换立即生效；正在运行的任务从下一次工具调用起采用新模式</span>
        </div>
        {permissionOptions().map(option => <button
          aria-checked={option.value === mode}
          className={`permission-item ${option.value === mode ? 'is-current' : ''}`}
          key={option.value}
          onClick={() => void choose(option.value)}
          onMouseEnter={() => setHovered(option.value)}
          role="menuitemradio"
          type="button"
        >
          {hovered === option.value ? <motion.span
            className="permission-item-hover"
            layoutId={hoverLayoutId}
            transition={{ type: 'spring', stiffness: 460, damping: 34, mass: 0.58 }}
          /> : null}
          <span className="permission-item-text">
            <strong>{option.label}</strong>
            <em>{option.hint}</em>
          </span>
          {option.value === mode ? <Check aria-hidden="true" className="permission-item-check" size={15} /> : null}
        </button>)}
      </motion.div> : null}
    </AnimatePresence>
  </div>
}
