import { useState } from 'react'
import { Pencil, Plug, Plus, Trash2 } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Card } from '@/components/ui/card'
import { Switch } from '@/components/ui/b-switch'
import { Confirm, Field, Modal } from '@/components/shared'
import { EmptyState, PageShell } from '@/components/PageShell'
import { useStore } from '@/lib/store'
import { useMcpServersStore } from '@/stores/mcpServers'
import { useNoticesStore } from '@/stores/ui'
import type { McpServerConfig } from '@/types'

export default function Mcp() {
  const store = useStore(useMcpServersStore)
  const notices = useStore(useNoticesStore)
  const [editing, setEditing] = useState<McpServerConfig | null>(null)
  const [remove, setRemove] = useState<McpServerConfig | null>(null)
  const [busy, setBusy] = useState(false)

  const update = (patch: Partial<McpServerConfig>) => setEditing(current => (current ? { ...current, ...patch } : current))

  async function run(action: () => Promise<unknown>) {
    setBusy(true)
    try {
      await action()
      return true
    } catch (error) {
      notices.report(error)
      return false
    } finally {
      setBusy(false)
    }
  }

  return <PageShell
    action={<Button icon={<Plus size={16} />} onClick={() => setEditing({ id: '', name: '', command: '', arguments: '', isEnabled: false, createdAt: '' })}>添加服务</Button>}
    description="服务默认关闭。启用配置后点击连接才会启动；断开或停用即可释放对应进程。"
    eyebrow="CONNECTED TOOLS"
    title="把工具，连接到工作流。"
  >
    <div className="section-meta">
      <span>{store.servers.length} 个服务 · {store.connectedIds.length} 个已连接</span>
      <span>stdio 启动命令 · 本地运行</span>
    </div>
    {!store.servers.length
      ? <EmptyState title="连接你的工具生态">使用可执行命令和参数添加一个 MCP 服务。</EmptyState>
      : <Card className="list-card">
        <div className="divide-list">
          {store.servers.map(server => <div className="list-row" key={server.id}>
            <div className="resource-icon"><Plug size={20} /></div>
            <div className="list-content">
              <div className="resource-title">
                <h3>{server.name}</h3>
                {store.connectedIds.includes(server.id)
                  ? <Badge color="green" size="sm" variant="dot">已连接</Badge>
                  : <Badge color={server.isEnabled ? 'blue' : 'gray'} size="sm" variant="dot">{server.isEnabled ? '已启用' : '已停用'}</Badge>}
              </div>
              <p className="list-description mono">{server.command} {server.arguments}</p>
            </div>
            <div className="list-actions">
              <Switch
                aria-label={`启用 ${server.name}`}
                checked={server.isEnabled}
                disabled={busy}
                onCheckedChange={enabled => void run(() => store.save({ ...server, isEnabled: enabled }))}
                size="sm"
              />
              <Button
                disabled={(!server.isEnabled && !store.connectedIds.includes(server.id)) || busy}
                loading={store.connectingIds.includes(server.id)}
                onClick={() => void run(async () => {
                  if (store.connectedIds.includes(server.id)) {
                    await store.disconnect(server.id)
                    notices.notify('MCP 服务已断开', server.name, 'success')
                    return
                  }
                  const ok = await store.connect(server.id)
                  if (!ok) throw new Error(`${server.name} 连接失败，请检查命令是否可用。`)
                  notices.notify('MCP 服务已连接', server.name, 'success')
                })}
                size="sm"
                variant="outline"
              >{store.connectedIds.includes(server.id) ? '断开' : '连接'}</Button>
              <Button iconLabel="编辑服务" onClick={() => setEditing({ ...server })} size="icon-sm" variant="ghost"><Pencil size={15} /></Button>
              <Button iconLabel="删除服务" onClick={() => setRemove(server)} size="icon-sm" variant="ghost"><Trash2 size={15} /></Button>
            </div>
          </div>)}
        </div>
      </Card>}

    {editing ? <Modal
      footer={<Button
        disabled={!editing.name.trim() || !editing.command.trim()}
        loading={busy}
        onClick={() => void run(() => store.save(editing)).then(ok => { if (ok) setEditing(null) })}
      >保存服务</Button>}
      onClose={() => setEditing(null)}
      title={editing.id ? '编辑 MCP 服务' : '添加 MCP 服务'}
    >
      <Field label="服务名称" onChange={name => update({ name })} required value={editing.name} />
      <Field label="启动命令" onChange={command => update({ command })} placeholder="例如 npx / uvx / 可执行文件路径" required value={editing.command} />
      <Field label="命令参数" onChange={value => update({ arguments: value })} placeholder="例如 -y @modelcontextprotocol/server-everything" value={editing.arguments} />
      <div className="setting-row"><span>启用服务</span>
        <Switch aria-label="启用服务" checked={editing.isEnabled} onCheckedChange={isEnabled => update({ isEnabled })} size="sm" />
      </div>
    </Modal> : null}

    {remove ? <Confirm
      onClose={() => setRemove(null)}
      onConfirm={() => run(() => store.remove(remove.id))}
      title={`删除 MCP 服务「${remove.name}」？`}
    /> : null}
  </PageShell>
}
