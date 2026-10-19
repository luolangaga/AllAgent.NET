import { useEffect, useRef } from 'react'
import { Terminal, type ITheme } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { WebLinksAddon } from '@xterm/addon-web-links'
import '@xterm/xterm/css/xterm.css'
import { bridgeEvents, invoke, type BridgeEvents } from '@/bridge'
import { base64ToBytes, utf8ToBase64 } from '@/lib/base64'
import type { TerminalAttach, TerminalSession } from '@/types'

const FONT = '"Cascadia Code", Consolas, "SF Mono", "JetBrains Mono", monospace'

/** xterm colours derived from the app's light/dark tokens so the terminal sits in the theme. */
export function terminalTheme(dark: boolean): ITheme {
  return dark
    ? {
      background: '#0c0c0e', foreground: '#e8e8e6', cursor: '#7dd3fc', cursorAccent: '#0c0c0e',
      selectionBackground: 'rgba(56, 189, 248, 0.28)',
      black: '#131316', red: '#f87171', green: '#4ade80', yellow: '#fbbf24', blue: '#60a5fa',
      magenta: '#c084fc', cyan: '#22d3ee', white: '#e5e5e3',
      brightBlack: '#6b6b73', brightRed: '#fca5a5', brightGreen: '#86efac', brightYellow: '#fde68a',
      brightBlue: '#93c5fd', brightMagenta: '#d8b4fe', brightCyan: '#67e8f9', brightWhite: '#ffffff',
    }
    : {
      background: '#ffffff', foreground: '#1b1b1f', cursor: '#0284c7', cursorAccent: '#ffffff',
      selectionBackground: 'rgba(2, 132, 199, 0.18)',
      black: '#1b1b1f', red: '#dc2626', green: '#16a34a', yellow: '#d97706', blue: '#2563eb',
      magenta: '#9333ea', cyan: '#0891b2', white: '#d6d6d4',
      brightBlack: '#74747b', brightRed: '#ef4444', brightGreen: '#22c55e', brightYellow: '#f59e0b',
      brightBlue: '#3b82f6', brightMagenta: '#a855f7', brightCyan: '#06b6d4', brightWhite: '#ffffff',
    }
}

/**
 * One xterm instance per terminal tab. Output frames are sequence-guarded, the replay from
 * `terminal.attach` is written first, and frames that raced the attach are flushed afterwards,
 * so a reload never loses or reorders bytes. Keystrokes go back as UTF-8 bytes; resize follows
 * the element via the fit addon and is debounced.
 */
export function TerminalView({ session, active, dark }: { session: TerminalSession; active: boolean; dark: boolean }) {
  const hostRef = useRef<HTMLDivElement>(null)
  const termRef = useRef<Terminal | null>(null)
  const fitRef = useRef<FitAddon | null>(null)
  const fitTimer = useRef(0)

  useEffect(() => {
    const host = hostRef.current
    if (!host) return
    let disposed = false
    let attached = false
    let sequence = 0
    const pending: Array<{ seq: number; data: Uint8Array }> = []

    const term = new Terminal({
      fontFamily: FONT,
      fontSize: 12.5,
      lineHeight: 1.35,
      cursorBlink: true,
      scrollback: 5000,
      theme: terminalTheme(document.documentElement.classList.contains('dark')),
    })
    const fit = new FitAddon()
    term.loadAddon(fit)
    term.loadAddon(new WebLinksAddon())
    term.open(host)
    termRef.current = term
    fitRef.current = fit
    requestAnimationFrame(() => { try { fit.fit() } catch { /* hidden panel */ } })

    function scheduleFit() {
      window.clearTimeout(fitTimer.current)
      fitTimer.current = window.setTimeout(() => {
        if (disposed) return
        try {
          const previousCols = term.cols
          const previousRows = term.rows
          fit.fit()
          if (term.cols !== previousCols || term.rows !== previousRows) {
            void invoke<boolean>('terminal.resize', { id: session.id, cols: term.cols, rows: term.rows }).catch(() => {})
          }
        } catch { /* hidden panel */ }
      }, 120)
    }

    const onOutput = (payload: BridgeEvents['terminal.output']) => {
      if (payload.streamId !== session.id) return
      const seq = payload.sequence ?? 0
      const bytes = base64ToBytes(payload.base64)
      if (!attached) { pending.push({ seq, data: bytes }); return }
      if (seq <= sequence) return
      sequence = seq
      term.write(bytes)
    }
    bridgeEvents.on('terminal.output', onOutput)

    const onData = term.onData(data => {
      void invoke<boolean>('terminal.write', { id: session.id, data: utf8ToBase64(data) }).catch(() => {})
    })

    void invoke<TerminalAttach>('terminal.attach', { id: session.id }).then(result => {
      if (disposed) return
      sequence = result.sequence
      if (result.replay) term.write(base64ToBytes(result.replay))
      attached = true
      for (const item of pending.sort((left, right) => left.seq - right.seq)) {
        if (item.seq <= sequence) continue
        sequence = item.seq
        term.write(item.data)
      }
      pending.length = 0
    }).catch(error => {
      if (disposed) return
      term.write(`\r\n\x1b[31m无法附加终端：${error instanceof Error ? error.message : String(error)}\x1b[0m\r\n`)
    })

    const observer = new ResizeObserver(() => scheduleFit())
    observer.observe(host)
    window.addEventListener('resize', scheduleFit)

    return () => {
      disposed = true
      window.clearTimeout(fitTimer.current)
      window.removeEventListener('resize', scheduleFit)
      observer.disconnect()
      bridgeEvents.off('terminal.output', onOutput)
      onData.dispose()
      term.dispose()
      termRef.current = null
      fitRef.current = null
    }
  }, [session.id])

  useEffect(() => {
    const term = termRef.current
    if (term) term.options.theme = terminalTheme(dark)
  }, [dark])

  useEffect(() => {
    if (!active) return
    const timer = window.setTimeout(() => {
      const term = termRef.current
      const fit = fitRef.current
      if (!term || !fit) return
      try { fit.fit() } catch { /* hidden panel */ }
      term.focus()
      void invoke<boolean>('terminal.resize', { id: session.id, cols: term.cols, rows: term.rows }).catch(() => {})
    }, 40)
    return () => window.clearTimeout(timer)
  }, [active, session.id])

  return <div className={`term-view${active ? '' : ' is-hidden'}`} ref={hostRef} />
}
