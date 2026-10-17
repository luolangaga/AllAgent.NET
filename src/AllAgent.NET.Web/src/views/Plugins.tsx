import { useEffect, useState } from 'react'
import { Puzzle, Search, Trash2, Upload } from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Switch } from '@/components/ui/b-switch'
import { Confirm } from '@/components/shared'
import { EmptyState, PageShell } from '@/components/PageShell'
import { useStore } from '@/lib/store'
import { usePluginsStore } from '@/stores/plugins'
import { useNoticesStore } from '@/stores/ui'
import type { PluginStatusInfo } from '@/types'

function readBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result).split(',', 2)[1] ?? '')
    reader.onerror = () => reject(new Error('读取插件包失败'))
    reader.readAsDataURL(file)
  })
}

export default function Plugins() {
  const store = useStore(usePluginsStore)
  const notices = useStore(useNoticesStore)
  const [query, setQuery] = useState('')
  const [remove, setRemove] = useState<PluginStatusInfo | null>(null)

  useEffect(() => { void store.loadAll() }, [])

  async function run(action: () => Promise<unknown>, success?: string) {
    try {
      await action()
      if (success) notices.notify(success, undefined, 'success')
    } catch (error) {
      notices.report(error)
    }
  }

  return <PageShell
    action={<Button
      icon={<Search size={15} />}
      loading={store.searching}
      onClick={() => void run(() => store.search(query.trim()))}
      variant="outline"
    >搜索插件</Button>}
    description=".NET 插件为 Agent 增加工具、命令与模型提供商。插件安装在共享目录，启用后立即生效。"
    eyebrow="EXTENSIONS"
    title="把能力，装进工作台。"
  >
    {!store.available ? <EmptyState title="此宿主不支持插件管理">
      插件管理只在终端宿主（<span className="mono">/web-ui</span> 或 <span className="mono">allagent web-ui</span>）中可用；桌面端不加载插件。
    </EmptyState> : <>
      <div className="section-meta">
        <span>{store.plugins.length} 个已安装 · {store.plugins.filter(plugin => plugin.enabled).length} 个已启用</span>
        <span>{store.authentication.length ? `认证入口：${store.authentication.map(auth => auth.name).join(' · ')}` : '未发现认证插件'}</span>
      </div>
      {store.plugins.length > 0 ? <Card className="list-card">
        <div className="divide-list">{store.plugins.map(plugin => <div className="list-row" key={plugin.packageId}>
          <div className="resource-icon"><Puzzle size={20} /></div>
          <div className="list-content">
            <div className="resource-title">
              <h3>{plugin.name}</h3>
              <Badge size="sm" variant="dot">{plugin.packageId} · {plugin.version}</Badge>
              {plugin.enabled ? <Badge color="green" size="sm" variant="dot">已启用</Badge> : <Badge color="gray" size="sm">已停用</Badge>}
              {plugin.error ? <Badge color="red" size="sm" variant="dot">加载失败</Badge> : null}
            </div>
            {plugin.error ? <Alert appearance="warning" dismissible={false} message={plugin.error} timeout={0} title="插件错误" /> : null}
          </div>
          <div className="list-actions">
            <Switch
              aria-label={`启用 ${plugin.name}`}
              checked={plugin.enabled}
              disabled={store.busy}
              onCheckedChange={enabled => void run(() => store.setEnabled(plugin.packageId, enabled), enabled ? '插件已启用' : '插件已停用')}
              size="sm"
            />
            <Button iconLabel={`卸载 ${plugin.name}`} onClick={() => setRemove(plugin)} size="icon-sm" variant="ghost"><Trash2 size={15} /></Button>
          </div>
        </div>)}</div>
      </Card> : <EmptyState title="还没有安装插件">
        在下方搜索 NuGet 上的 AllAgent 插件，或者导入本地的 .nupkg 包。宿主自带的内置插件会在下次启动时自动安装。
      </EmptyState>}

      {store.hooks.length > 0 ? <Card className="list-card">
        <div className="list-row">
          <div className="resource-icon"><Puzzle size={20} /></div>
          <div className="list-content">
            <div className="resource-title"><h3>生命周期 Hooks</h3><Badge size="sm" variant="dot">{store.hooks.length} 个</Badge></div>
            <p className="list-description">插件注册的钩子会在任务开始、工具调用与结束时触发。</p>
          </div>
        </div>
        <div className="divide-list">{store.hooks.map(hook => <div className="list-row" key={hook.name}>
          <div className="list-content"><div className="resource-title"><h3 className="mono">{hook.name}</h3></div></div>
          <div className="list-actions"><Switch
            aria-label={`启用 ${hook.name}`}
            checked={hook.enabled}
            disabled={store.busy}
            onCheckedChange={enabled => void run(() => store.setHook(hook.name, enabled))}
            size="sm"
          /></div>
        </div>)}</div>
      </Card> : null}

      <Card className="list-card">
        <div className="list-row">
          <div className="resource-icon"><Search size={20} /></div>
          <div className="list-content">
            <div className="resource-title"><h3>插件商店</h3><Badge size="sm" variant="dot">NuGet · AllAgent.NET 标签</Badge></div>
            <p className="list-description">搜索带 AllAgent.NET / AllAgent / LanAgent 标签的包；安装会校验依赖与签名哈希。</p>
          </div>
        </div>
        <div className="plugin-search">
          <Input
            aria-label="搜索插件"
            onValueChange={setQuery}
            placeholder="搜索插件包，例如 commandcode、workbuddy…"
            size="sm"
            value={query}
          />
          <Button
            disabled={store.searching}
            icon={<Search size={14} />}
            onClick={() => void run(() => store.search(query.trim()))}
            size="sm"
            variant="outline"
          >搜索</Button>
        </div>
        {store.searchResults.length > 0 ? <div className="divide-list">{store.searchResults.map(pkg => <div className="list-row" key={`${pkg.id}@${pkg.version}`}>
          <div className="list-content">
            <div className="resource-title">
              <h3 className="mono">{pkg.id}</h3>
              <Badge size="sm" variant="dot">{pkg.version}</Badge>
              {pkg.authors ? <Badge color="gray" size="sm" variant="dot">{pkg.authors}</Badge> : null}
            </div>
            <p className="list-description">{pkg.description || '暂无描述'}</p>
          </div>
          <div className="list-actions">
            <Button
              disabled={store.busy}
              onClick={() => void run(() => store.install(pkg.id, pkg.version), `已安装 ${pkg.id}`)}
              size="sm"
              variant="outline"
            >安装</Button>
          </div>
        </div>)}</div> : store.query ? <p className="list-description plugin-empty">没有匹配“{store.query}”的插件包。</p> : null}
      </Card>

      <Card className="list-card">
        <div className="list-row">
          <div className="resource-icon"><Upload size={20} /></div>
          <div className="list-content">
            <div className="resource-title"><h3>本地安装</h3><Badge size="sm" variant="dot">.nupkg</Badge></div>
            <p className="list-description">选择本地构建的插件包；安装后会出现在上面的列表里，可随时启用或卸载。</p>
          </div>
        </div>
        <label className="file-import plugin-import">
          <input
            accept=".nupkg"
            disabled={store.busy}
            onChange={event => {
              const file = event.target.files?.[0]
              event.target.value = ''
              if (!file) return
              void run(async () => {
                const base64 = await readBase64(file)
                await store.importPackage(file.name, base64)
              }, `已导入 ${file.name}`)
            }}
            type="file"
          />
        </label>
      </Card>
    </>}

    {remove ? <Confirm
      description="插件会先停用再移出清单；文件保留在共享目录以便恢复。"
      onClose={() => setRemove(null)}
      onConfirm={() => run(() => store.remove(remove.packageId), '插件已卸载')}
      title={`卸载插件「${remove.name}」？`}
    /> : null}
  </PageShell>
}
