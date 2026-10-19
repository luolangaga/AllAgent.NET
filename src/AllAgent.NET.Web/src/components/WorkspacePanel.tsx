import { useEffect } from 'react'
import {
  ChevronRight, ClipboardCopy, ExternalLink, Eye, EyeOff, FileText, FolderTree, MessageSquarePlus, RefreshCw,
  Search, SquareTerminal,
} from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/b-button'
import { CodeBlock } from '@/components/ui/code-block'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/b-tooltip'
import { FileTypeIcon } from './FileTypeIcon'
import { FloatingPanel } from './FloatingPanel'
import { EmptyState } from './PageShell'
import { Markdown } from './Markdown'
import { useStore } from '@/lib/store'
import { invoke } from '@/bridge'
import { formatBytes } from '@/lib/attachments'
import { formatDateTime } from '@/lib/format'
import { usePanelsStore } from '@/stores/panels'
import { useCommandsStore } from '@/stores/commands'
import { useNoticesStore } from '@/stores/ui'
import { useTerminalStore } from '@/stores/terminal'
import { isIgnoredName, useWorkspaceBrowserStore } from '@/stores/workspaceBrowser'
import type { WorkspaceFileContent, WorkspaceTreeEntry } from '@/types'

const CODE_EXTENSIONS: Record<string, string> = {
  ts: 'typescript', tsx: 'tsx', js: 'javascript', jsx: 'jsx', mjs: 'javascript', cjs: 'javascript',
  cs: 'csharp', py: 'python', ps1: 'powershell', psm1: 'powershell', sh: 'bash', bat: 'dos', cmd: 'dos',
  json: 'json', jsonc: 'json', xml: 'xml', yml: 'yaml', yaml: 'yaml', toml: 'ini', ini: 'ini',
  css: 'css', scss: 'scss', less: 'less', html: 'html', htm: 'html', sql: 'sql', go: 'go', rs: 'rust',
  java: 'java', kt: 'kotlin', php: 'php', rb: 'ruby', c: 'c', h: 'c', cpp: 'cpp', hpp: 'cpp', vue: 'xml', svg: 'xml',
}

const IMAGE_MEDIA: Record<string, string> = {
  png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp',
  bmp: 'image/bmp', ico: 'image/x-icon', svg: 'image/svg+xml',
}

function extensionOf(path: string): string {
  const name = path.split('/').pop() ?? path
  const dot = name.lastIndexOf('.')
  return dot > 0 ? name.slice(dot + 1).toLowerCase() : ''
}

export function WorkspacePanel() {
  const panels = useStore(usePanelsStore)
  const store = useStore(useWorkspaceBrowserStore)
  const open = panels.open.files

  useEffect(() => {
    if (open && !store.loaded) void store.loadTree('.')
  }, [open, store.loaded])

  if (!open) return null
  return <FloatingPanel
    defaultRect={{ x: Math.max(24, window.innerWidth - 880), y: 64, width: 860, height: 560 }}
    headerExtra={<Tooltip content="重新读取目录与文件">
      <Button iconLabel="刷新工作区文件" onClick={() => void store.refresh()} size="icon-xs" variant="ghost"><RefreshCw size={12} /></Button>
    </Tooltip>}
    icon={<FolderTree size={13} />}
    id="files"
    minHeight={320}
    minWidth={560}
    onClose={() => panels.closePanel('files')}
    title="工作区文件"
  >
    <div className="wsb-body">
      <div className="wsb-side">
        <div className="wsb-tools">
          <Input
            aria-label="搜索工作区文件"
            onValueChange={value => void store.search(value)}
            placeholder="搜索文件…"
            size="sm"
            startAdornment={<Search size={13} />}
            value={store.query}
          />
          <Tooltip content={store.hideIgnored ? '显示 node_modules、.git 等' : '隐藏常见忽略目录'}>
            <Button
              iconLabel="切换忽略目录显示"
              onClick={() => { store.hideIgnored = !store.hideIgnored }}
              size="icon-sm"
              variant="ghost"
            >{store.hideIgnored ? <EyeOff size={13} /> : <Eye size={13} />}</Button>
          </Tooltip>
        </div>
        <div className="wsb-tree">
          {store.query.trim()
            ? <SearchResults />
            : <TreeLevel depth={0} path="." />}
        </div>
      </div>
      <div className="wsb-preview">
        <Preview />
      </div>
    </div>
  </FloatingPanel>
}

function SearchResults() {
  const store = useStore(useWorkspaceBrowserStore)
  if (store.searching && !store.matches.length) return <p className="wsb-note"><Spinner size="sm" /> 正在搜索…</p>
  if (!store.matches.length) return <p className="wsb-note">没有匹配的文件。</p>
  return <div className="wsb-results">
    {store.matches.map(entry => <button
      className="tree-row tree-row-flat"
      key={entry.path}
      onClick={() => void (entry.isDirectory ? store.loadTree(entry.path) : store.openFile(entry.path))}
      type="button"
    >
      <FileTypeIcon directory={entry.isDirectory} name={entry.name} size={14} />
      <span className="tree-name" title={entry.path}>{entry.name}</span>
      <span className="tree-path mono">{entry.path}</span>
    </button>)}
  </div>
}

function TreeLevel({ path, depth }: { path: string; depth: number }) {
  const store = useStore(useWorkspaceBrowserStore)
  const entries = store.trees[path]
  if (!entries) {
    return store.errors[path]
      ? <p className="wsb-note">{store.errors[path]}</p>
      : <p className="wsb-note"><Spinner size="sm" /> 正在读取…</p>
  }
  const visible = entries.filter(entry => !(store.hideIgnored && isIgnoredName(entry.name)))
  if (!visible.length && depth === 0) return <p className="wsb-note">这个目录是空的。</p>
  return <>
    {visible.map(entry => <TreeRow depth={depth} entry={entry} key={entry.path} />)}
    {store.truncated[path] ? <p className="wsb-note">目录内容过多，仅显示前 2000 项。</p> : null}
  </>
}

function TreeRow({ entry, depth }: { entry: WorkspaceTreeEntry; depth: number }) {
  const store = useStore(useWorkspaceBrowserStore)
  const panels = useStore(usePanelsStore)
  const terminal = useStore(useTerminalStore)
  const notices = useStore(useNoticesStore)
  const open = store.expanded.includes(entry.path)

  async function openInTerminal() {
    try {
      await terminal.create({ cwd: entry.path })
      panels.openPanel('terminal')
    } catch (error) {
      notices.report(error)
    }
  }

  function activate() {
    if (entry.link) {
      void invoke<boolean>('shell.openPath', { path: entry.path }).catch(notices.report)
      return
    }
    if (entry.directory) void store.toggleDirectory(entry.path)
    else void store.openFile(entry.path)
  }

  // Children render as siblings of the row; nesting them inside the flex row would lay
  // them out horizontally next to the parent.
  return <>
    <div className="tree-row" style={{ paddingLeft: 6 + depth * 14 }}>
      <button className="tree-main" onClick={activate} title={entry.path} type="button">
        {entry.directory
          ? <ChevronRight aria-hidden className={`tree-chevron${open ? ' is-open' : ''}`} size={12} />
          : <span aria-hidden className="tree-chevron-spacer" />}
        <FileTypeIcon directory={entry.directory} name={entry.name} open={open} size={14} />
        <span className="tree-name">{entry.name}</span>
        {entry.link ? <Badge color="gray" size="sm">链接</Badge> : null}
      </button>
      <div className="tree-actions">
        {entry.directory && !entry.link ? <Tooltip content="在终端中打开">
          <Button iconLabel="在终端中打开" onClick={() => void openInTerminal()} size="icon-xs" variant="ghost"><SquareTerminal size={11} /></Button>
        </Tooltip> : null}
        <Tooltip content="用资源管理器打开">
          <Button
            iconLabel="用资源管理器打开"
            onClick={() => void invoke<boolean>('shell.openPath', { path: entry.path }).catch(notices.report)}
            size="icon-xs"
            variant="ghost"
          ><ExternalLink size={11} /></Button>
        </Tooltip>
      </div>
    </div>
    {entry.directory && open && !entry.link
      ? <div className="tree-children"><TreeLevel depth={depth + 1} path={entry.path} /></div>
      : null}
  </>
}

function Preview() {
  const store = useStore(useWorkspaceBrowserStore)
  const notices = useStore(useNoticesStore)
  const commands = useStore(useCommandsStore)

  if (store.fileLoading) return <div className="wsb-placeholder"><Spinner size="lg" /><p>正在读取文件…</p></div>
  if (store.fileError) return <div className="wsb-placeholder">
    <Alert appearance="destructive" dismissible={false} message={store.fileError} timeout={0} title="无法预览" width="100%" />
    <p className="wsb-note">目录、二进制或工作区外的路径请用资源管理器或默认程序打开。</p>
  </div>
  const file = store.file
  if (!file) return <div className="wsb-placeholder">
    <EmptyState title="选择一个文件查看内容">只读预览，不会修改文件。点击左侧文件即可打开。</EmptyState>
  </div>

  const extension = extensionOf(file.path)
  function copyPath() {
    void navigator.clipboard.writeText(file!.path)
      .then(() => notices.notify('已复制路径', file!.path, 'success'))
      .catch(notices.report)
  }
  function quote() {
    commands.queueDraft(`@${file!.path} `)
    notices.notify('已放入输入框', `${file!.path} · 回到对话页补充问题后发送。`, 'success')
  }
  function openExternal() {
    void invoke<boolean>('shell.openPath', { path: file!.path }).catch(notices.report)
  }

  return <>
    <div className="wsb-preview-head">
      <FileTypeIcon name={file.path} size={14} />
      <span className="mono wsb-preview-path" title={file.path}>{file.path}</span>
      <div className="wsb-preview-actions">
        <Tooltip content="复制工作区相对路径"><Button icon={<ClipboardCopy size={12} />} onClick={copyPath} size="xs" variant="ghost">复制路径</Button></Tooltip>
        <Tooltip content="把 @路径 引用放进对话输入框"><Button icon={<MessageSquarePlus size={12} />} onClick={quote} size="xs" variant="ghost">引用到对话</Button></Tooltip>
        <Tooltip content="用系统默认程序打开"><Button icon={<ExternalLink size={12} />} onClick={openExternal} size="xs" variant="ghost">默认程序</Button></Tooltip>
      </div>
    </div>
    <div className="wsb-preview-body">
      {file.truncated && file.kind === 'text'
        ? <Alert appearance="warning" dismissible={false} message="文件较大，仅显示前 256 KB。" timeout={0} title="已截断" width="100%" />
        : null}
      <PreviewBody file={file} extension={extension} />
    </div>
  </>
}

function PreviewBody({ file, extension }: { file: WorkspaceFileContent; extension: string }) {
  if (file.kind === 'image' && file.base64) {
    const media = IMAGE_MEDIA[extension] ?? 'application/octet-stream'
    return <div className="file-image-wrap">
      <img alt={file.path} className="file-image" src={`data:${media};base64,${file.base64}`} />
      <FileMeta file={file} />
    </div>
  }
  if (file.kind === 'text' && file.text != null) {
    if (extension === 'md' || extension === 'markdown') return <Markdown className="file-markdown" content={file.text} />
    return <>
      <CodeBlock code={file.text} filename={file.path} language={CODE_EXTENSIONS[extension] ?? 'plaintext'} maxHeight={880} showLineNumbers />
      <FileMeta file={file} />
    </>
  }
  return <div className="wsb-placeholder">
    <FileText size={20} />
    <p>二进制文件无法在页面中预览，可复制路径或使用默认程序打开。</p>
    <FileMeta file={file} />
  </div>
}

function FileMeta({ file }: { file: WorkspaceFileContent }) {
  return <p className="wsb-meta">
    <span>{formatBytes(file.size)}</span>
    <span>修改于 {formatDateTime(file.modified)}</span>
  </p>
}
