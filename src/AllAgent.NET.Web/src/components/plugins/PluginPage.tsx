import { Component, useState, type ReactNode } from 'react'
import { RefreshCw } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { EmptyState } from '@/components/PageShell'
import { PluginFrame } from '@/components/plugins/PluginFrame'
import { useStore } from '@/lib/store'
import { useContributionsStore } from '@/stores/contributions'

/** A plugin page renders inside the host shell: slim header plus a sandboxed frame. */
export function PluginPage({ route, onNavigate }: { route: string; onNavigate: (page: string) => void }) {
  const contributions = useStore(useContributionsStore)
  const [refreshKey, setRefreshKey] = useState(0)
  const entry = contributions.pageByRoute(route)

  if (!entry) {
    return <section className="page">
      <EmptyState title="插件界面暂不可用">
        该页面所属的插件可能已被停用、卸载，或它的 Web 界面被关闭。
        <div className="plugin-page-missing">
          <Button onClick={() => onNavigate('chat')} size="sm" variant="outline">返回对话</Button>
          <Button onClick={() => onNavigate('plugins')} size="sm" variant="ghost">查看插件页</Button>
        </div>
      </EmptyState>
    </section>
  }

  const capabilities = entry.plugin.capabilities ?? []
  return <section className="plugin-page">
    <header className="plugin-page-head">
      <div className="plugin-page-title">
        <strong>{entry.title}</strong>
        <Badge size="sm" variant="dot">{entry.plugin.name}</Badge>
        <span className="plugin-page-package mono">{entry.plugin.packageId} · {entry.plugin.version}</span>
      </div>
      <div className="plugin-page-meta">
        {capabilities.length ? <span className="plugin-page-caps" title={capabilities.join(' · ')}>能力：{capabilities.join(' · ')}</span> : <span className="plugin-page-caps">沙箱界面</span>}
        <Button
          icon={<RefreshCw size={14} />}
          iconLabel="重新加载插件界面"
          onClick={() => setRefreshKey(value => value + 1)}
          size="icon-sm"
          variant="ghost"
        />
      </div>
    </header>
    <FrameBoundary onReset={() => setRefreshKey(value => value + 1)}>
      <PluginFrame
        assetBaseUrl={contributions.assetBaseUrl}
        className="plugin-page-frame"
        entry={entry.entry}
        plugin={entry.plugin}
        refreshKey={refreshKey}
        view={{ kind: 'page', id: entry.pageId }}
      />
    </FrameBoundary>
  </section>
}

/** Host-side safety net: a render failure in the page chrome never takes down the shell. */
class FrameBoundary extends Component<{ children: ReactNode; onReset: () => void }, { error: Error | null }> {
  state: { error: Error | null } = { error: null }
  static getDerivedStateFromError(error: Error) {
    return { error }
  }
  render() {
    if (!this.state.error) return this.props.children
    return <div className="plugin-frame-error" role="alert">
      <strong>插件界面渲染出错</strong>
      <span>{this.state.error.message}</span>
      <Button onClick={() => { this.setState({ error: null }); this.props.onReset() }} size="sm" variant="outline">重新加载</Button>
    </div>
  }
}
