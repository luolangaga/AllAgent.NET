import { useEffect, useRef } from 'react'
import { PluginFrame } from '@/components/plugins/PluginFrame'
import type { FrameHandle } from '@/lib/pluginRuntime'
import type { PluginWebInfo, ToolCall } from '@/types'

const PUSH_INTERVAL_MS = 100

/** Custom tool card: the plugin renders its own view of the call, fed by throttled tool.update events. */
export function PluginToolCard({ call, plugin, entry, assetBaseUrl }: {
  call: ToolCall
  plugin: PluginWebInfo
  entry: string
  assetBaseUrl: string | null
}) {
  const handle = useRef<FrameHandle | null>(null)
  const lastSent = useRef(0)
  const timer = useRef<number | undefined>(undefined)

  useEffect(() => {
    function send() {
      lastSent.current = Date.now()
      handle.current?.event('tool.update', {
        id: call.id,
        name: call.name,
        status: call.status,
        args: clip(call.args, 20000),
        output: clip(call.output ?? '', 16000),
        note: call.note ?? null,
        rejected: call.rejected ?? false,
      })
    }
    const elapsed = Date.now() - lastSent.current
    if (elapsed >= PUSH_INTERVAL_MS) send()
    else {
      window.clearTimeout(timer.current)
      timer.current = window.setTimeout(send, PUSH_INTERVAL_MS - elapsed)
    }
    return () => window.clearTimeout(timer.current)
  }, [call.status, call.args, call.output, call.note, call.rejected])

  return <div className="plugin-tool-card">
    <PluginFrame
      assetBaseUrl={assetBaseUrl}
      autoHeight
      entry={entry}
      lazy
      maxHeight={420}
      minHeight={140}
      onHandle={registered => { handle.current = registered }}
      plugin={plugin}
      view={{ kind: 'toolcard', id: call.id, params: { name: call.name } }}
    />
  </div>
}

function clip(text: string, limit: number): string {
  return text.length > limit ? `${text.slice(0, limit)}\n… 已截断` : text
}
