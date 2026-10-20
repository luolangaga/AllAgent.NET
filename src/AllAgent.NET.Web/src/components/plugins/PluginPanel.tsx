import { useStore } from '@/lib/store'
import { FloatingPanel } from '@/components/FloatingPanel'
import { PluginFrame } from '@/components/plugins/PluginFrame'
import { pluginIcon } from '@/components/plugins/icons'
import { useContributionsStore, type PluginPanelEntry } from '@/stores/contributions'
import { usePanelsStore } from '@/stores/panels'

/** A plugin panel contribution: a topbar toggle that opens its sandboxed frame in a floating window. */
export function PluginPanel({ entry }: { entry: PluginPanelEntry }) {
  const contributions = useStore(useContributionsStore)
  const panels = useStore(usePanelsStore)
  const Icon = pluginIcon(entry.icon)

  return <FloatingPanel
    defaultRect={{ x: Math.max(16, window.innerWidth - 1000), y: 64, width: 960, height: 620 }}
    icon={<Icon size={13} />}
    id={entry.key}
    minHeight={320}
    minWidth={520}
    onClose={() => panels.closePanel(entry.key)}
    title={entry.title}
  >
    <PluginFrame
      assetBaseUrl={contributions.assetBaseUrl}
      className="plugin-panel-frame"
      entry={entry.entry}
      plugin={entry.plugin}
      view={{ kind: 'panel', id: entry.panelId }}
    />
  </FloatingPanel>
}
