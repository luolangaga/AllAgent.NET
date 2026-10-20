import { useEffect, useMemo, useState } from 'react'
import {
  ChevronsDownUp, ChevronsUpDown, ClipboardCopy, Copy, ExternalLink, Eye, EyeOff, FileText, FolderOpen,
  FolderTree, GitBranch, MessageSquarePlus, RefreshCw, Search, SquareTerminal,
} from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/b-button'
import { CodeBlock } from '@/components/ui/code-block'
import {
  ContextMenu, ContextMenuCheckboxItem, ContextMenuContent, ContextMenuGroup, ContextMenuItem,
  ContextMenuSeparator, ContextMenuShortcut, ContextMenuSub, ContextMenuSubContent,
  ContextMenuSubTrigger, ContextMenuTrigger,
} from '@/components/ui/b-context-menu'
import { FileTreeFromItems, type FileTreeNodeData } from '@/components/ui/file-tree'
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
import { useT } from '@/i18n'
import { usePanelsStore } from '@/stores/panels'
import { useCommandsStore } from '@/stores/commands'
import { useNoticesStore } from '@/stores/ui'
import { useTerminalStore } from '@/stores/terminal'
import { useWorkspaceStore } from '@/stores/workspace'
import { isIgnoredName, useWorkspaceBrowserStore } from '@/stores/workspaceBrowser'
import type { WorkspaceFileContent, WorkspaceFileEntry, WorkspaceTreeEntry } from '@/types'

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

function parentOf(path: string): string {
  const index = path.lastIndexOf('/')
  return index < 0 ? '.' : path.slice(0, index) || '.'
}

function absolutePath(root: string, path: string): string {
  if (!root) return path
  const separator = root.includes('\\') ? '\\' : '/'
  const cleanRoot = root.replace(/[\\/]+$/, '')
  const cleanPath = path.replace(/^\.?[\\/]/, '').replace(/\//g, separator)
  return `${cleanRoot}${separator}${cleanPath}`
}

function buildTreeNodes(entries: WorkspaceTreeEntry[] | undefined, store: ReturnType<typeof useWorkspaceBrowserStore>): FileTreeNodeData[] {
  if (!entries) return []
  return entries
    .filter(entry => !(store.hideIgnored && isIgnoredName(entry.name)))
    .map(entry => {
      const loaded = entry.directory && !entry.link && store.expanded.includes(entry.path)
        ? store.trees[entry.path]
        : undefined
      return {
        id: entry.path,
        label: entry.name,
        icon: <FileTypeIcon directory={entry.directory} name={entry.name} size={14} />,
        openIcon: entry.directory ? <FileTypeIcon directory name={entry.name} open size={14} /> : undefined,
        hasChildren: entry.directory && !entry.link,
        loading: store.loadingDirs.includes(entry.path),
        children: loaded ? buildTreeNodes(loaded, store) : undefined,
      }
    })
}

export function WorkspacePanel() {
  const tr = useT()
  const panels = useStore(usePanelsStore)
  const store = useStore(useWorkspaceBrowserStore)
  const [contextPath, setContextPath] = useState<string | null>(null)
  const open = panels.open.files

  useEffect(() => {
    if (open && !store.loaded) void store.loadTree('.')
  }, [open, store.loaded])

  const entries = useMemo(() => {
    const map = new Map<string, WorkspaceTreeEntry>()
    for (const list of Object.values(store.trees)) {
      if (!Array.isArray(list)) continue
      for (const entry of list) map.set(entry.path, entry)
    }
    for (const entry of store.matches) {
      if (map.has(entry.path)) continue
      map.set(entry.path, {
        name: entry.name,
        path: entry.path,
        directory: entry.isDirectory,
        size: 0,
        modified: '',
        link: false,
        hidden: false,
      })
    }
    return map
  }, [store.trees, store.matches])

  if (!open) return null
  const contextEntry = contextPath ? entries.get(contextPath) ?? null : null

  return <FloatingPanel
    defaultRect={{ x: Math.max(24, window.innerWidth - 880), y: 64, width: 860, height: 560 }}
    headerExtra={<Tooltip content={tr('重新读取目录与文件', 'Reload folders and files')}>
      <Button iconLabel={tr('刷新工作区文件', 'Refresh workspace files')} onClick={() => void store.refresh()} size="icon-xs" variant="ghost"><RefreshCw size={12} /></Button>
    </Tooltip>}
    icon={<FolderTree size={13} />}
    id="files"
    minHeight={320}
    minWidth={560}
    onClose={() => panels.closePanel('files')}
    title={tr('工作区文件', 'Workspace files')}
  >
    <div className="wsb-body">
      <div className="wsb-side">
        <div className="wsb-tools">
          <Input
            aria-label={tr('搜索工作区文件', 'Search workspace files')}
            onValueChange={value => void store.search(value)}
            placeholder={tr('搜索文件…', 'Search files…')}
            size="sm"
            startAdornment={<Search size={13} />}
            value={store.query}
          />
          <Tooltip content={store.hideIgnored ? tr('显示 node_modules、.git 等', 'Show node_modules, .git and similar') : tr('隐藏常见忽略目录', 'Hide common ignored folders')}>
            <Button
              iconLabel={tr('切换忽略目录显示', 'Toggle ignored folders')}
              onClick={() => { store.hideIgnored = !store.hideIgnored }}
              size="icon-sm"
              variant="ghost"
            >{store.hideIgnored ? <EyeOff size={13} /> : <Eye size={13} />}</Button>
          </Tooltip>
        </div>
        <ContextMenu onOpenChange={open => { if (!open) setContextPath(null) }}>
          <ContextMenuTrigger asChild>
            <div
              className="wsb-tree"
              onContextMenu={event => {
                if (!(event.target as HTMLElement).closest('[role="treeitem"]')) setContextPath(null)
              }}
            >
              {store.query.trim()
                ? <SearchResults onContextMenu={entry => setContextPath(entry.path)} />
                : <WorkspaceTree entries={entries} onContextMenu={setContextPath} />}
            </div>
          </ContextMenuTrigger>
          <WorkspaceContextMenu entry={contextEntry} onDone={() => setContextPath(null)} />
        </ContextMenu>
      </div>
      <div className="wsb-preview">
        <Preview />
      </div>
    </div>
  </FloatingPanel>
}

function WorkspaceTree({ entries, onContextMenu }: {
  entries: Map<string, WorkspaceTreeEntry>
  onContextMenu: (path: string) => void
}) {
  const tr = useT()
  const store = useStore(useWorkspaceBrowserStore)
  const notices = useStore(useNoticesStore)
  const root = store.trees['.']
  const nodes = useMemo(() => buildTreeNodes(root, store), [root, store.expanded, store.hideIgnored, store.loadingDirs, store.trees])

  if (!root) {
    return store.errors['.']
      ? <p className="wsb-note">{store.errors['.']}</p>
      : <p className="wsb-note"><Spinner size="sm" />{tr('正在读取…', 'Loading…')}</p>
  }

  return <FileTreeFromItems
    className="wsb-filetree"
    expandedIds={store.expanded.filter(path => path !== '.')}
    indentSize={14}
    items={nodes}
    maxHeight="100%"
    onLoadChildren={path => void store.loadTree(path)}
    onNodeClick={path => {
      const entry = entries.get(path)
      if (!entry) return
      if (entry.link) {
        void invoke<boolean>('shell.openPath', { path }).catch(notices.report)
        return
      }
      if (!entry.directory) void store.openFile(path)
    }}
    onNodeContextMenu={path => onContextMenu(path)}
    onNodeExpand={(path, expanded) => store.setDirectoryExpanded(path, expanded)}
    onSelectedIdsChange={() => { }}
    selectedIds={store.file ? [store.file.path] : []}
    truncate
  />
}

function WorkspaceContextMenu({ entry, onDone }: { entry: WorkspaceTreeEntry | null; onDone: () => void }) {
  const tr = useT()
  const store = useStore(useWorkspaceBrowserStore)
  const panels = useStore(usePanelsStore)
  const terminal = useStore(useTerminalStore)
  const notices = useStore(useNoticesStore)
  const commands = useStore(useCommandsStore)
  const workspace = useStore(useWorkspaceStore)
  const path = entry?.path ?? ''
  const directory = entry?.directory ?? false
  const parent = path ? parentOf(path) : '.'
  const absolute = path ? absolutePath(workspace.workspace, path) : workspace.workspace
  const expanded = path ? store.expanded.includes(path) : false

  function copy(value: string, label: string) {
    void navigator.clipboard.writeText(value)
      .then(() => notices.notify(tr('已复制', 'Copied'), label, 'success'))
      .catch(notices.report)
  }

  async function openInTerminal() {
    try {
      await terminal.create({ cwd: directory ? path : parent })
      panels.openPanel('terminal')
    } catch (error) {
      notices.report(error)
    }
  }

  function openExternal() {
    if (path) void invoke<boolean>('shell.openPath', { path }).catch(notices.report)
  }

  function reveal() {
    if (path) void invoke<boolean>('shell.openPath', { path: directory ? path : parent }).catch(notices.report)
  }

  function quote() {
    commands.queueDraft(`@${path} `)
    notices.notify(tr('已放入输入框', 'Added to composer'), path, 'success')
  }

  function refreshDirectory() {
    if (!path) { void store.refresh(); return }
    if (directory) void store.loadTree(path)
    else void store.loadTree(parent)
  }

  if (!entry) {
    return <ContextMenuContent className="w-60" collisionPadding={8}>
      <div className="max-w-[220px] truncate px-3 py-1 font-medium text-muted-foreground text-xs">{tr('工作区', 'Workspace')}</div>
      <ContextMenuGroup>
        <ContextMenuItem onClick={() => void store.refresh()}><RefreshCw size={15} />{tr('重新加载文件树', 'Reload file tree')}</ContextMenuItem>
        <ContextMenuItem onClick={() => { store.expandLoaded(); onDone() }}><ChevronsUpDown size={15} />{tr('展开已加载目录', 'Expand loaded folders')}</ContextMenuItem>
        <ContextMenuItem onClick={() => { store.collapseAll(); onDone() }}><ChevronsDownUp size={15} />{tr('折叠全部目录', 'Collapse all folders')}</ContextMenuItem>
        <ContextMenuCheckboxItem
          checked={store.hideIgnored}
          onCheckedChange={value => { store.hideIgnored = value }}
        >{tr('隐藏忽略目录', 'Hide ignored folders')}</ContextMenuCheckboxItem>
      </ContextMenuGroup>
      <ContextMenuSeparator />
      <ContextMenuItem onClick={() => void store.search('')}><Search size={15} />{tr('清除搜索', 'Clear search')}</ContextMenuItem>
    </ContextMenuContent>
  }

  return <ContextMenuContent className="w-64" collisionPadding={8}>
    <div className="max-w-[220px] truncate px-3 py-1 font-medium text-muted-foreground text-xs">{entry.name}</div>
    {entry.path !== entry.name ? <div className="wsb-context-path">{entry.path}{directory ? tr(' · 目录', ' · folder') : ''}</div> : null}
    <ContextMenuSeparator />
    <ContextMenuGroup>
      {directory
        ? <ContextMenuItem onClick={() => { store.setDirectoryExpanded(path, !expanded); onDone() }}>
          <FolderOpen size={15} />{expanded ? tr('折叠目录', 'Collapse folder') : tr('展开目录', 'Expand folder')}
        </ContextMenuItem>
        : <ContextMenuItem onClick={() => void store.openFile(path)}>
          <FileText size={15} />{tr('预览文件', 'Preview file')}<ContextMenuShortcut>↵</ContextMenuShortcut>
        </ContextMenuItem>}
      <ContextMenuSub>
        <ContextMenuSubTrigger><ExternalLink size={15} />{tr('打开方式', 'Open with')}</ContextMenuSubTrigger>
        <ContextMenuSubContent className="w-56">
          <ContextMenuItem onClick={openExternal}>{tr('系统默认程序', 'Default application')}</ContextMenuItem>
          <ContextMenuItem onClick={reveal}>{directory ? tr('在资源管理器中打开', 'Open in Explorer') : tr('打开所在文件夹', 'Open containing folder')}</ContextMenuItem>
          <ContextMenuItem onClick={() => void openInTerminal()}><SquareTerminal size={15} />{tr('在此目录打开终端', 'Open terminal here')}</ContextMenuItem>
          <ContextMenuItem onClick={() => panels.openPanel('git')}><GitBranch size={15} />{tr('在 Git 面板中查看', 'Open Git panel')}<ContextMenuShortcut>Ctrl+Shift+G</ContextMenuShortcut></ContextMenuItem>
        </ContextMenuSubContent>
      </ContextMenuSub>
    </ContextMenuGroup>
    <ContextMenuSeparator />
    <ContextMenuSub>
      <ContextMenuSubTrigger><Copy size={15} />{tr('复制', 'Copy')}</ContextMenuSubTrigger>
      <ContextMenuSubContent className="w-56">
        <ContextMenuItem onClick={() => copy(path, tr('相对路径', 'relative path'))}>{tr('相对路径', 'Relative path')}</ContextMenuItem>
        <ContextMenuItem onClick={() => copy(absolute, tr('绝对路径', 'absolute path'))}>{tr('绝对路径', 'Absolute path')}</ContextMenuItem>
        <ContextMenuItem onClick={() => copy(entry.name, tr('文件名', 'file name'))}>{tr('文件名', 'File name')}</ContextMenuItem>
        <ContextMenuItem onClick={() => copy(`[${entry.name}](${path})`, tr('Markdown 链接', 'Markdown link'))}>{tr('Markdown 链接', 'Markdown link')}</ContextMenuItem>
        <ContextMenuItem onClick={() => copy(`@${path}`, tr('@ 引用', '@ reference'))}>{tr('@ 引用', '@ reference')}</ContextMenuItem>
      </ContextMenuSubContent>
    </ContextMenuSub>
    <ContextMenuItem onClick={quote}><MessageSquarePlus size={15} />{tr('引用到对话', 'Quote in chat')}</ContextMenuItem>
    <ContextMenuItem onClick={() => void store.search(entry.name)}><Search size={15} />{tr('查找同名文件', 'Find files with this name')}</ContextMenuItem>
    <ContextMenuSeparator />
    <ContextMenuGroup>
      <ContextMenuItem onClick={refreshDirectory}><RefreshCw size={15} />{tr('刷新此目录', 'Refresh this folder')}</ContextMenuItem>
      <ContextMenuItem onClick={() => { store.expandLoaded(); onDone() }}><ChevronsUpDown size={15} />{tr('展开已加载目录', 'Expand loaded folders')}</ContextMenuItem>
      <ContextMenuItem onClick={() => { store.collapseAll(); onDone() }}><ChevronsDownUp size={15} />{tr('折叠全部目录', 'Collapse all folders')}</ContextMenuItem>
      <ContextMenuCheckboxItem
        checked={store.hideIgnored}
        onCheckedChange={value => { store.hideIgnored = value }}
      >{tr('隐藏忽略目录', 'Hide ignored folders')}</ContextMenuCheckboxItem>
    </ContextMenuGroup>
    <ContextMenuSeparator />
    <ContextMenuItem onClick={() => panels.openPanel('git')}><GitBranch size={15} />{tr('打开 Git 面板', 'Open Git panel')}<ContextMenuShortcut>Ctrl+Shift+G</ContextMenuShortcut></ContextMenuItem>
  </ContextMenuContent>
}

function SearchResults({ onContextMenu }: { onContextMenu: (entry: WorkspaceFileEntry) => void }) {
  const tr = useT()
  const store = useStore(useWorkspaceBrowserStore)
  if (store.searching && !store.matches.length) return <p className="wsb-note"><Spinner size="sm" />{tr('正在搜索…', 'Searching…')}</p>
  if (!store.matches.length) return <p className="wsb-note">{tr('没有匹配的文件。', 'No matching files.')}</p>
  return <div className="wsb-results">
    {store.matches.map(entry => <button
      className="tree-row tree-row-flat"
      key={entry.path}
      onClick={() => {
        if (entry.isDirectory) {
          void store.search('')
          store.setDirectoryExpanded(entry.path, true)
        } else {
          void store.openFile(entry.path)
        }
      }}
      onContextMenu={() => onContextMenu(entry)}
      type="button"
    >
      <FileTypeIcon directory={entry.isDirectory} name={entry.name} size={14} />
      <span className="tree-name" title={entry.path}>{entry.name}</span>
      <span className="tree-path mono">{entry.path}</span>
    </button>)}
  </div>
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
