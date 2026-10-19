import { useEffect, useState } from 'react'
import { RefreshCw, Search, SquareTerminal, Terminal } from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Confirm, Modal } from '@/components/shared'
import { EmptyState, PageShell } from '@/components/PageShell'
import { useStore } from '@/lib/store'
import { useNoticesStore } from '@/stores/ui'
import { usePowerShellStore } from '@/stores/powershell'
import type { PowerShellModuleInfo } from '@/types'

/** The install confirm works from a Gallery match or a hand-typed name/version pair. */
type InstallTarget = { name: string; version: string }

export default function PowerShell() {
  const store = useStore(usePowerShellStore)
  const notices = useStore(useNoticesStore)
  const [query, setQuery] = useState('')
  const [install, setInstall] = useState<InstallTarget | null>(null)
  const [details, setDetails] = useState<PowerShellModuleInfo | null>(null)
  const [manualName, setManualName] = useState('')
  const [manualVersion, setManualVersion] = useState('')

  useEffect(() => { void store.load() }, [])

  async function run(action: () => Promise<unknown>, success?: string) {
    try {
      await action()
      if (success) notices.notify(success, undefined, 'success')
    } catch (error) {
      notices.report(error)
    }
  }

  function openCommands(module: PowerShellModuleInfo) {
    setDetails(module)
    void store.readCommands(module.name, module.version)
  }

  function installByName() {
    const name = manualName.trim()
    if (!name) return
    setInstall({ name, version: manualVersion.trim() })
  }

  const usable = store.modules.filter(module => !module.error).length
  const installed = (name: string) => store.modules.some(module => module.name.toLowerCase() === name.toLowerCase())

  return <PageShell
    action={<Button
      icon={<RefreshCw size={15} />}
      loading={store.loading}
      onClick={() => void run(() => store.refresh(), '能力清单已刷新')}
      variant="outline"
    >刷新清单</Button>}
    description="从 PowerShell Gallery 安装模块到 AllAgent 专属目录：连同依赖一起下载、导入验证后注册为 AI 能力。不修改系统 PowerShell，也不安装系统组件。"
    eyebrow="POWERSHELL 能力商店"
    title="把 PowerShell 能力，装进工作台。"
  >
    {!store.loaded ? <p className="list-description">正在读取本机 PowerShell 能力清单…</p>
      : !store.available ? <EmptyState title="此宿主不支持 PowerShell 组件管理">
        PowerShell 组件的安装与验证只在终端宿主（<span className="mono">/web-ui</span> 或 <span className="mono">allagent web-ui</span>）中可用。
      </EmptyState> : <>
      {!store.workerAvailable ? <Alert
        appearance="warning"
        dismissible={false}
        message={`未找到内嵌 PowerShell SDK worker：${store.workerPath}。随包的 SDK 与宿主一起分发，请重新安装 AllAgent；安装系统 PowerShell 不参与运行。`}
        timeout={0}
        title="PowerShell 组件缺失"
        width="100%"
      /> : null}

      <div className="section-meta">
        <span>{store.modules.length} 个模块 · {usable} 个可用 · 来源 PowerShell Gallery</span>
        <span className="mono">{store.root}</span>
      </div>

      <Card className="list-card">
        <div className="list-row">
          <div className="resource-icon"><SquareTerminal size={20} /></div>
          <div className="list-content">
            <div className="resource-title">
              <h3>已安装模块</h3>
              <Badge size="sm" variant="dot">{store.modules.length} 个</Badge>
            </div>
            <p className="list-description">每个新任务启动时会实际导入验证这些模块，只有导出命令的模块才会成为 AI 能力；导入失败会保留错误详情。</p>
          </div>
        </div>
        {store.modules.length ? <div className="divide-list">{store.modules.map(module => <div className="list-row" key={`${module.name}@${module.version}`}>
          <div className="resource-icon"><Terminal size={20} /></div>
          <div className="list-content">
            <div className="resource-title">
              <h3 className="mono">{module.name}</h3>
              <Badge size="sm" variant="dot">{module.version}</Badge>
              {module.error
                ? <Badge color="red" size="sm" variant="dot">导入失败</Badge>
                : <Badge color="green" size="sm" variant="dot">可用</Badge>}
              <Badge color="gray" size="sm">{module.commandCount} 个命令</Badge>
            </div>
            {module.error ? <Alert appearance="warning" dismissible={false} message={module.error} timeout={0} title="导入错误" /> : null}
          </div>
          <div className="list-actions">
            <Button disabled={store.commandsBusy} onClick={() => openCommands(module)} size="sm" variant="outline">查看命令</Button>
          </div>
        </div>)}</div> : <p className="list-description plugin-empty">还没有安装模块。在下方搜索 PowerShell Gallery，或直接按名称安装。</p>}
      </Card>

      <Card className="list-card">
        <div className="list-row">
          <div className="resource-icon"><Search size={20} /></div>
          <div className="list-content">
            <div className="resource-title"><h3>模块商店</h3><Badge size="sm" variant="dot">PowerShell Gallery</Badge></div>
            <p className="list-description">搜索模块名或关键词；安装使用结果中的确切版本，并接受该模块及其依赖的许可证。</p>
          </div>
        </div>
        <div className="plugin-search">
          <Input
            aria-label="搜索模块"
            onKeyDown={event => { if (event.key === 'Enter' && query.trim()) void run(() => store.search(query.trim())) }}
            onValueChange={setQuery}
            placeholder="搜索模块，例如 Pester、PSReadLine、Az"
            size="sm"
            value={query}
          />
          <Button
            disabled={store.searching || !query.trim()}
            icon={<Search size={14} />}
            loading={store.searching}
            onClick={() => void run(() => store.search(query.trim()))}
            size="sm"
            variant="outline"
          >搜索</Button>
        </div>
        {store.packages.length ? <div className="divide-list">{store.packages.map(pkg => <div className="list-row" key={`${pkg.name}@${pkg.version}`}>
          <div className="list-content">
            <div className="resource-title">
              <h3 className="mono">{pkg.name}</h3>
              <Badge size="sm" variant="dot">{pkg.version}</Badge>
              {pkg.author ? <Badge color="gray" size="sm" variant="dot">{pkg.author}</Badge> : null}
              {installed(pkg.name) ? <Badge color="green" size="sm" variant="dot">已安装</Badge> : null}
            </div>
            <p className="list-description">{pkg.description || '暂无描述'}</p>
          </div>
          <div className="list-actions">
            <Button
              disabled={store.installing}
              onClick={() => setInstall({ name: pkg.name, version: pkg.version })}
              size="sm"
              variant="outline"
            >安装</Button>
          </div>
        </div>)}</div> : store.searched ? <p className="list-description plugin-empty">
          {store.query ? `没有匹配“${store.query}”的模块；关键词只支持模块名的一部分。` : '输入关键词后开始搜索。'}
        </p> : null}
        {store.searched && store.packages.length ? <p className="list-description plugin-empty">
          共 {store.packages.length} 个结果（PowerShell Gallery 一次最多返回 30 个），列出的版本可直接安装。
        </p> : null}
      </Card>

      <Card className="list-card">
        <div className="list-row">
          <div className="resource-icon"><SquareTerminal size={20} /></div>
          <div className="list-content">
            <div className="resource-title"><h3>按名称安装</h3><Badge size="sm" variant="dot">精确版本</Badge></div>
            <p className="list-description">已知模块名时直接安装；版本留空安装当前稳定版。</p>
          </div>
        </div>
        <div className="plugin-search">
          <Input
            aria-label="模块名称"
            onValueChange={setManualName}
            placeholder="模块名称，例如 Pester"
            size="sm"
            value={manualName}
          />
          <Input
            aria-label="模块版本"
            onValueChange={setManualVersion}
            placeholder="版本（可选），例如 5.7.1"
            size="sm"
            value={manualVersion}
          />
          <Button
            disabled={store.installing || !manualName.trim()}
            onClick={installByName}
            size="sm"
            variant="outline"
          >安装</Button>
        </div>
      </Card>
    </>}

    {install ? <Confirm
      confirmLabel="安装"
      description={`从 PowerShell Gallery 下载 ${install.name}${install.version ? ` ${install.version}` : '（当前稳定版）'} 及其依赖到 AllAgent 专属目录，接受相关许可证并导入验证。安装可能需要几分钟；模块是会执行的代码，安装表示允许导入它及其依赖。`}
      onClose={() => setInstall(null)}
      onConfirm={async () => {
        await store.install(install.name, install.version)
        notices.notify(`已安装 ${install.name}`, '能力清单已更新，新的任务会自动带上这些模块。', 'success')
        setManualName('')
        setManualVersion('')
        setInstall(null)
      }}
      title={`安装 ${install.name}？`}
    /> : null}

    {details ? <Modal
      description={`${details.error ? '导入失败：' + details.error : '宿主导入验证后列出的导出命令。'} AI 通过 powershell_invoke 调用单个命令，或在 powershell_script 会话中组合使用。`}
      footer={<Button onClick={() => setDetails(null)} variant="outline">关闭</Button>}
      onClose={() => setDetails(null)}
      title={`${details.name} · ${details.version}`}
    >
      {store.commandsBusy ? <p className="list-description">正在读取命令列表…</p>
        : store.commandsError ? <Alert appearance="warning" dismissible={false} message={store.commandsError} timeout={0} title="无法读取命令列表" />
          : store.commands?.error ? <Alert appearance="warning" dismissible={false} message={store.commands.error} timeout={0} title="模块导入失败" />
            : store.commands ? <>
              <div className="ps-commands mono">
                {store.commands.commands.map(command => <code key={command}>{command}</code>)}
              </div>
              <p className="list-description">
                共 {store.commands.total} 个命令{store.commands.total > store.commands.commands.length ? `，这里显示前 ${store.commands.commands.length} 个` : ''}。
              </p>
            </> : null}
    </Modal> : null}
  </PageShell>
}
