import { useEffect, useRef, useState } from 'react'
import { ChevronDown, ChevronUp, Square } from 'lucide-react'
import { Button } from './ui/b-button'
import { StatusDot } from './ui/status-dot'
import { Tooltip } from './ui/b-tooltip'
import { useStore } from '@/lib/store'
import { useConversationsStore } from '@/stores/conversations'
import { useNoticesStore } from '@/stores/ui'
import { useWorkspaceStore } from '@/stores/workspace'
import { useT } from '@/i18n'
import { runStateLabel } from './RunMarker'
import type { RunningConversation } from '@/types'

const COLLAPSED_KEY = 'allagent.run-dock.collapsed'

/**
 * Bottom-right dock listing the sessions that keep running while you look at another one.
 * A run is never cancelled by leaving it, so this is where a background session stays visible,
 * is jumped to, or is stopped on purpose.
 */
export function RunDock() {
  const tr = useT()
  const conv = useStore(useConversationsStore)
  const workspace = useStore(useWorkspaceStore)
  const notices = useStore(useNoticesStore)
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem(COLLAPSED_KEY) === '1')
  const [opening, setOpening] = useState<string | null>(null)
  const [stopping, setStopping] = useState<string | null>(null)

  // Other sessions only: the conversation on screen shows its own progress inline.
  const others = conv.running.filter(run => run.conversationId !== conv.activeId)

  const seen = useRef<Map<string, string> | null>(null)
  const stopped = useRef(new Set<string>())

  // A background session that finishes while you are elsewhere should not end silently.
  useEffect(() => {
    const current = new Map(conv.running.map(run => [run.conversationId, run.title]))
    const previous = seen.current
    seen.current = current
    if (!previous) return
    for (const [id, title] of previous) {
      if (current.has(id) || id === conv.activeId) continue
      if (stopped.current.delete(id)) continue
      notices.notify(tr('后台会话已完成', 'Background conversation finished'), tr(`「${title}」已跑完，切回去可以查看结果。`, `“${title}” finished; switch back to see the result.`), 'success')
    }
  }, [conv.running, conv.activeId])

  function toggle() {
    setCollapsed(value => {
      localStorage.setItem(COLLAPSED_KEY, value ? '0' : '1')
      return !value
    })
  }

  async function open(run: RunningConversation) {
    setOpening(run.conversationId)
    try {
      if (run.workspace !== workspace.workspace) {
        await workspace.switchTo(run.workspace)
        await conv.loadAll()
      }
      await conv.selectConversation(run.conversationId)
      location.hash = '/chat'
    } catch (error) {
      notices.report(error)
    } finally {
      setOpening(null)
    }
  }

  async function stop(run: RunningConversation) {
    setStopping(run.conversationId)
    stopped.current.add(run.conversationId)
    try {
      await conv.cancelRun(run.conversationId)
      notices.notify(tr('已停止后台会话', 'Stopped background conversation'), `「${run.title}」`, 'info')
    } catch (error) {
      stopped.current.delete(run.conversationId)
      notices.report(error)
    } finally {
      setStopping(null)
    }
  }

  if (!others.length) return null

  return <aside className={`run-dock${collapsed ? ' is-collapsed' : ''}`} aria-label={tr('后台运行中的会话', 'Conversations running in the background')}>
    <header>
      <StatusDot animate size="sm" tone="active" />
      <strong>{tr(`${others.length} 个会话在后台运行`, `${others.length} conversations running in the background`)}</strong>
      <Tooltip content={collapsed ? tr('展开', 'Expand') : tr('收起', 'Collapse')}>
        <Button iconLabel={collapsed ? tr('展开后台会话', 'Expand background conversations') : tr('收起后台会话', 'Collapse background conversations')} onClick={toggle} size="icon-xs" variant="ghost">
          {collapsed ? <ChevronUp size={12} /> : <ChevronDown size={12} />}
        </Button>
      </Tooltip>
    </header>
    {collapsed ? null : <div className="run-dock-list">
      {others.map(run => {
        const waiting = run.state === 'awaiting-approval' || run.state === 'awaiting-answer' || run.state === 'awaiting-plan'
        return <div className={`run-dock-row${waiting ? ' is-waiting' : ''}`} key={run.conversationId}>
          <button
            className="run-dock-open"
            disabled={opening === run.conversationId}
            onClick={() => void open(run)}
            title={tr('切换到该会话', 'Switch to this conversation')}
            type="button"
          >
            <StatusDot animate={!waiting} size="sm" tone={waiting ? 'warning' : 'active'} />
            <span className="run-dock-title">{run.title}</span>
            <small>{runStateLabel(run.state)}</small>
            <em>{run.workspaceName}</em>
          </button>
          <Tooltip content={tr('停止该会话', 'Stop this conversation')}>
            <Button
              disabled={stopping === run.conversationId}
              iconLabel={tr('停止后台会话', 'Stop background conversation')}
              onClick={() => void stop(run)}
              size="icon-xs"
              variant="ghost"
            ><Square size={11} /></Button>
          </Tooltip>
        </div>
      })}
    </div>}
  </aside>
}
