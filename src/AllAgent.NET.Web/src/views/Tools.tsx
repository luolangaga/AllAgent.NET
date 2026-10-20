import { useEffect, useMemo, useState } from 'react'
import { Plug, Puzzle, RefreshCw, Search, Wand2, Wrench } from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Card } from '@/components/ui/card'
import { CodeBlock } from '@/components/ui/code-block'
import { Input } from '@/components/ui/input'
import { EmptyState, PageShell } from '@/components/PageShell'
import { useStore } from '@/lib/store'
import { useNoticesStore } from '@/stores/ui'
import { useToolsStore } from '@/stores/tools'
import type { ToolInfo } from '@/types'

const sourceLabels: Record<string, string> = { host: '内置', plugin: '插件', mcp: 'MCP 服务', skill: '技能', 'on-demand': '按需' }
const sourceBadge: Record<string, 'green' | 'blue' | 'gray'> = { host: 'green', plugin: 'blue', mcp: 'gray', skill: 'green', 'on-demand': 'blue' }
const riskInfo: Record<string, { label: string; color: 'green' | 'gray' | 'red' }> = {
  read: { label: '读取', color: 'green' },
  write: { label: '写入', color: 'gray' },
  execute: { label: '执行', color: 'red' },
}

function sourceIcon(tool: ToolInfo) {
  if (tool.source === 'plugin') return <Puzzle size={20} />
  if (tool.source === 'mcp') return <Plug size={20} />
  if (tool.source === 'skill') return <Wand2 size={20} />
  return <Wrench size={20} />
}

// Catalog of every tool the host exposes: built-ins, plugin contributions, MCP server tools
// and skills — the same inventory the terminal shows under /tools.
export default function Tools() {
  const store = useStore(useToolsStore)
  const notices = useStore(useNoticesStore)
  const [query, setQuery] = useState('')
  const [source, setSource] = useState('all')

  useEffect(() => { if (!store.loaded) void store.loadAll() }, [])

  const sources = useMemo(() => ['all', ...new Set(store.tools.map(tool => tool.source))], [store.tools])
  const filtered = useMemo(() => {
    const needle = query.trim().toLowerCase()
    return store.tools.filter(tool =>
      (source === 'all' || tool.source === source)
      && (!needle || tool.name.toLowerCase().includes(needle) || tool.description.toLowerCase().includes(needle) || tool.owner.toLowerCase().includes(needle)))
  }, [store.tools, query, source])

  const counts = useMemo(() => {
    const map = new Map<string, number>()
    store.tools.forEach(tool => map.set(tool.source, (map.get(tool.source) ?? 0) + 1))
    return map
  }, [store.tools])

  return <PageShell
    action={<Button
      icon={<RefreshCw size={15} />}
      loading={store.loading}
      onClick={() => void store.loadAll().catch(notices.report)}
      variant="outline"
    >刷新</Button>}
    description="插件、MCP 服务与宿主为 Agent 注册的全部工具契约。聊天里的每次工具调用都能在这里找到对应的名称与参数结构。"
    eyebrow="TOOL REGISTRY"
    title="Agent 能用的每一件工具。"
  >
    {!store.available && store.loaded && !store.tools.length
      ? <EmptyState title="工具目录不可用">{store.errors[0] ?? '此宿主未提供工具清单。'}</EmptyState>
      : <>
        <div className="section-meta">
          <span>{store.tools.length} 个工具{store.tools.length ? ` · 内置 ${counts.get('host') ?? 0} · 插件 ${counts.get('plugin') ?? 0} · MCP ${counts.get('mcp') ?? 0} · 技能 ${counts.get('skill') ?? 0} · 按需 ${counts.get('on-demand') ?? 0}` : ''}</span>
          <span>来源标注与权限风险来自宿主注册；插件前缀由插件包自带</span>
        </div>
        {store.errors.map(error => <Alert
          appearance="warning"
          dismissible={false}
          key={error}
          message={error}
          timeout={0}
          title="工具目录部分不可用"
        />)}
        <Card className="list-card">
          <div className="tool-filters">
            <Input
              aria-label="搜索工具"
              onValueChange={setQuery}
              placeholder="搜索工具名、说明或来源…"
              size="sm"
              startAdornment={<Search size={13} />}
              value={query}
            />
            <div className="tool-source-chips">
              {sources.map(item => <Button
                key={item}
                onClick={() => setSource(item)}
                size="xs"
                variant={source === item ? 'default' : 'outline'}
              >{item === 'all' ? '全部' : `${sourceLabels[item] ?? item}${counts.get(item) ? ` ${counts.get(item)}` : ''}`}</Button>)}
            </div>
          </div>
          {filtered.length ? <div className="divide-list">{filtered.map(tool => <div className="list-row tool-row" key={`${tool.source}-${tool.owner}-${tool.name}`}>
            <div className="resource-icon">{sourceIcon(tool)}</div>
            <div className="list-content">
              <div className="resource-title">
                <h3 className="mono">{tool.name}</h3>
                <Badge color={sourceBadge[tool.source] ?? 'gray'} size="sm" variant="dot">
                  {sourceLabels[tool.source] ?? tool.source}{tool.owner ? ` · ${tool.owner}` : ''}
                </Badge>
                <Badge color={riskInfo[tool.risk]?.color ?? 'gray'} size="sm">{riskInfo[tool.risk]?.label ?? tool.risk}</Badge>
              </div>
              <p className="list-description">{tool.description || '暂无说明'}</p>
              <details className="tool-params">
                <summary>参数 Schema</summary>
                <CodeBlock code={tool.parametersJson} language="json" maxHeight={220} />
              </details>
            </div>
          </div>)}</div> : <p className="list-description plugin-empty">
            {store.loading ? '正在收集工具目录…' : query || source !== 'all' ? '没有匹配的工具。' : '当前宿主没有注册任何工具。'}
          </p>}
        </Card>
      </>}
  </PageShell>
}
