import { useEffect, useRef, useState } from 'react'
import { ChevronDown, Plus, RotateCcw, SquareTerminal, X } from 'lucide-react'
import { Button } from '@/components/ui/b-button'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/b-tooltip'
import { FloatingPanel } from '@/components/FloatingPanel'
import { EmptyState } from '@/components/PageShell'
import { TerminalView } from './TerminalView'
import { useStore } from '@/lib/store'
import { usePanelsStore } from '@/stores/panels'
import { useSettingsStore } from '@/stores/settings'
import { useTerminalStore } from '@/stores/terminal'
import { useNoticesStore } from '@/stores/ui'

function useSystemDark(): boolean {
  const [dark, setDark] = useState(() => window.matchMedia('(prefers-color-scheme: dark)').matches)
  useEffect(() => {
    const query = window.matchMedia('(prefers-color-scheme: dark)')
    const update = () => setDark(query.matches)
    query.addEventListener('change', update)
    return () => query.removeEventListener('change', update)
  }, [])
  return dark
}

/**
 * The terminal bubble: a VSCode-style tab strip over host-side ConPTY sessions. Tabs live on the
 * host, so closing the panel (or reloading the page) never kills a shell — only the X does.
 */
export function TerminalPanel() {
  const panels = useStore(usePanelsStore)
  const store = useStore(useTerminalStore)
  const settings = useStore(useSettingsStore)
  const notices = useStore(useNoticesStore)
  const [shellMenu, setShellMenu] = useState(false)
  const menuRef = useRef<HTMLDivElement>(null)
  const systemDark = useSystemDark()
  const open = panels.open.terminal
  const dark = settings.theme === 'dark' || (settings.theme === 'system' && systemDark)

  useEffect(() => {
    if (open && !store.loaded) void store.load()
  }, [open, store.loaded])

  useEffect(() => {
    if (open) store.requestFocus()
  }, [open])

  useEffect(() => {
    if (!shellMenu) return
    const close = (event: PointerEvent) => {
      if (!menuRef.current?.contains(event.target as Node)) setShellMenu(false)
    }
    window.addEventListener('pointerdown', close)
    return () => window.removeEventListener('pointerdown', close)
  }, [shellMenu])

  if (!open) return null

  async function create(shellId?: string): Promise<void> {
    try {
      await store.create(shellId ? { shell: shellId } : {})
    } catch (error) {
      notices.report(error)
    }
  }

  async function restart(): Promise<void> {
    const session = store.sessions.find(item => item.id === store.activeId)
    if (!session) return
    try {
      await store.create({ shell: session.shellId, cwd: session.cwd })
      await store.close(session.id)
    } catch (error) {
      notices.report(error)
    }
  }

  const active = store.sessions.find(session => session.id === store.activeId) ?? null

  return <>
    <FloatingPanel
      defaultRect={{ x: 24, y: Math.max(72, window.innerHeight - 600), width: 900, height: 540 }}
      headerExtra={<Tooltip content="新建终端">
        <Button icon={<Plus size={13} />} iconLabel="新建终端" onClick={() => void create()} size="icon-xs" variant="ghost" />
      </Tooltip>}
      icon={<SquareTerminal size={13} />}
      id="terminal"
      minHeight={280}
      minWidth={480}
      onClose={() => panels.closePanel('terminal')}
      title="终端"
    >
      <div className="term-panel">
        {store.sessions.length ? <div className="term-tabs">
          <div className="term-tab-strip">
            {store.sessions.map(session => <div className={`term-tab${session.id === store.activeId ? ' is-active' : ''}${session.alive ? '' : ' is-dead'}`} key={session.id}>
              <button className="term-tab-main" onClick={() => store.activate(session.id)} title={session.title} type="button">
                <SquareTerminal aria-hidden size={12} />
                <span className="term-tab-title">{session.title}</span>
                {!session.alive ? <span className="term-tab-state">已退出</span> : null}
              </button>
              <button aria-label="关闭终端" className="term-tab-close" onClick={() => void store.close(session.id).catch(notices.report)} type="button">
                <X size={11} />
              </button>
            </div>)}
          </div>
          <div className="term-actions" ref={menuRef}>
            <Tooltip content="新建终端（默认 Shell）">
              <Button icon={<Plus size={13} />} iconLabel="新建终端" onClick={() => void create()} size="icon-xs" variant="ghost" />
            </Tooltip>
            <Tooltip content="选择 Shell 新建 / 设置默认">
              <Button icon={<ChevronDown size={12} />} iconLabel="选择 Shell" onClick={() => setShellMenu(value => !value)} size="icon-xs" variant="ghost" />
            </Tooltip>
            {shellMenu ? <div className="term-shell-menu">
              {store.shells.map(shell => <button
                className={`term-shell-item${shell.id === store.defaultShell ? ' is-current' : ''}`}
                key={shell.id}
                onClick={() => { setShellMenu(false); store.setDefaultShell(shell.id); void create(shell.id) }}
                type="button"
              >
                <SquareTerminal aria-hidden size={12} />
                <span>{shell.label}</span>
                {shell.id === store.defaultShell ? <span className="term-shell-default">默认</span> : null}
              </button>)}
              <button
                className="term-shell-item"
                onClick={() => { setShellMenu(false); void Promise.all(store.sessions.map(session => store.close(session.id))).catch(notices.report) }}
                type="button"
              ><X aria-hidden size={12} /><span>关闭全部终端</span></button>
            </div> : null}
          </div>
        </div> : null}
        <div className="term-body">
          {!store.available ? <EmptyState title="当前宿主不支持集成终端">
            终端需要 Windows 上的本地宿主（allagent /web-ui 或 GUI 模式）；浏览器演示模式下不可用。
          </EmptyState> : !store.sessions.length ? <div className="term-empty">
            <EmptyState title="还没有终端">
              新建一个终端标签；Shell 会话由宿主进程托管，刷新页面或关闭面板都不会中断。
            </EmptyState>
            <div className="term-shell-grid">
              {store.shells.map(shell => <Button
                icon={<SquareTerminal size={13} />}
                key={shell.id}
                onClick={() => void create(shell.id)}
                size="sm"
                variant="outline"
              >{shell.label}</Button>)}
            </div>
            {!store.loaded ? <p className="term-note"><Spinner size="sm" /> 正在读取可用的 Shell…</p> : null}
          </div> : null}
          {store.sessions.map(session => <TerminalView
            active={session.id === store.activeId}
            dark={dark}
            key={session.id}
            session={session}
          />)}
          {active && !active.alive ? <div className="term-overlay">
            <p>终端已退出{active.exitCode != null ? `（退出码 ${active.exitCode}）` : ''}。</p>
            <div className="term-overlay-actions">
              <Button icon={<RotateCcw size={13} />} onClick={() => void restart()} size="sm">重新启动</Button>
              <Button onClick={() => void store.close(active.id).catch(notices.report)} size="sm" variant="ghost">关闭标签</Button>
            </div>
          </div> : null}
          {active && active.alive ? <div className="term-statusbar">
            <span className="mono" title={active.cwd}>{active.shellLabel} · {active.cwd}</span>
            <span>{active.cols} × {active.rows}</span>
          </div> : null}
        </div>
      </div>
    </FloatingPanel>
  </>
}
