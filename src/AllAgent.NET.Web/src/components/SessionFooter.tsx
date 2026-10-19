import { useEffect, useState } from 'react'
import { invoke } from '@/bridge'
import type { ModelFooterItem, ModelFooterSnapshot } from '@/types'

/**
 * The line the terminal keeps under its input, fed by `ModelFooterProvider`s: account quota
 * (Codex windows), plugin contributions, and anything else a plugin registered through
 * `PluginContext.AddModelFooter`. Provider lookups are network calls, so the host caches them
 * for a minute and reports when a refresh is still in flight — poll soon while it is, then
 * settle into the same one-minute cadence the terminal uses.
 */
export function SessionFooter({ configId, model }: { configId: string | null; model: string | null }) {
  const [items, setItems] = useState<ModelFooterItem[]>([])

  useEffect(() => {
    if (!configId) { setItems([]); return }
    let active = true
    let timer = 0
    let signature = ''
    const poll = async () => {
      try {
        const snapshot = await invoke<ModelFooterSnapshot>('session.footer', { apiConfigId: configId, model })
        if (!active) return
        const next = snapshot.items ?? []
        // Identical text must not rebuild the composer area on every poll.
        const nextSignature = JSON.stringify(next)
        if (nextSignature !== signature) { signature = nextSignature; setItems(next) }
        timer = window.setTimeout(() => void poll(), snapshot.refreshing ? 2000 : 60000)
      } catch {
        // A workspace without footer providers (or an offline host) simply shows nothing.
        if (active) timer = window.setTimeout(() => void poll(), 60000)
      }
    }
    void poll()
    return () => { active = false; window.clearTimeout(timer) }
  }, [configId, model])

  if (!items.length) return null
  return <div aria-live="polite" className="session-footer">
    {items.map((item, index) => <span
      className="session-footer-item"
      key={`${index}-${item.label}`}
      title={`${item.label} ${item.value}`}
    >
      <span className="session-footer-label">{item.label}</span>
      <span className="session-footer-value">{item.value}</span>
    </span>)}
  </div>
}
