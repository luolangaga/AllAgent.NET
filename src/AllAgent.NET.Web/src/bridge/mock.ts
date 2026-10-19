import type {
  AgentRoleDefinition,
  ApiConfig,
  AppSettings,
  BackgroundTask,
  Conversation,
  McpServerConfig,
  Message,
  Skill,
  TodoItem,
} from '@/types'
import { permissionLabel } from '@/lib/permissions'
import { utf8ToBase64 } from '@/lib/base64'

type Deliver = (json: string) => void

const now = () => new Date().toISOString()

/** Demo workspace and session controls: the composer's permission chip stays usable without a host. */
const demoWorkspace = {
  workspace: 'C:\\demo\\allagent-web',
  dataDirectory: 'C:\\demo\\allagent-web\\.allagent\\data',
  sharedDirectory: 'C:\\Users\\demo\\AppData\\Roaming\\AllAgent.NET',
}
const demoControls = {
  permissionMode: 'restricted',
  permissionLabel: permissionLabel('restricted'),
  sandboxMode: 'auto',
  sandboxEffective: '首次任务时验证',
  running: false,
}

const conversations: Conversation[] = [
  { id: 'demo-web', title: '把 Web 工作台升级为商业级产品', modelId: 'gpt-5.1', providerId: 'openai', createdAt: now(), updatedAt: now() },
  { id: 'demo-sandbox', title: '核对 Hyperlight 沙箱的启动指标', modelId: 'claude-sonnet-4-6', providerId: 'anthropic', createdAt: now(), updatedAt: now() },
  { id: 'demo-release', title: '整理 1.0.7 的发布说明', modelId: 'gpt-5.1', providerId: 'openai', createdAt: now(), updatedAt: now() },
]

const messages: Record<string, Message[]> = {
  'demo-web': [
    { id: 'm1', role: 'user', content: '帮我把 Web 工作台重做成商业级产品：新外壳、完整功能、暗色模式。', createdAt: now(), status: 'done' },
    {
      id: 'm2',
      role: 'assistant',
      content: '已经完成主体重构，并且补齐了三个会话缺陷：\n\n1. **历史加载竞态** — 迟到的历史不会再覆盖正在生成的消息；\n2. **工具拒绝** — 策略拒绝只会在轨迹行内标红，不再终止流；\n3. **并发建会话** — 生成过程中新建会话会被拒绝，当前界面不会丢失。\n\n新的桥接层同时支持 WebView、WebSocket 与演示三种传输，终端 `/web-ui` 与桌面端共用同一份前端。',
      reasoningContent: '先确认缺陷边界：三个问题都出在会话 store 的世代控制。修复思路是用 selectionVersion + streaming 双重校验，并把可恢复的拒绝从 agent.error 拆成独立事件。',
      createdAt: now(),
      status: 'done',
      toolCalls: [
        { id: 't1', name: 'execute_code', args: '{"code":"print(1)"}', status: 'done', output: '{"ok":true,"data":{"stdout":"1\\n","exit_code":0}}', outputLines: ['1'] },
        { id: 't2', parentId: 't1', name: 'file_read', args: '{"path":"src/App.tsx"}', status: 'done', output: '{"ok":true,"data":{"content":"…"}}' },
        { id: 't3', name: 'file_edit', args: '{"path":"src/views/Chat.tsx","operation":"write"}', status: 'error', rejected: true, note: '未执行 file_edit: plan mode 只读' },
        { id: 't4', name: 'todo_write', args: '{"todos":[]}', status: 'done', output: '{"ok":true,"data":{"todos":[]}}' },
        { id: 't5', name: 'subagent', args: '{"tasks":[{"agent":"general","task":"整理 1.0.7 的发布说明草稿"},{"agent":"reviewer","task":"审阅商业级改版的交互细节"}]}', status: 'running', output: '' },
      ],
    },
  ],
  'demo-sandbox': [
    { id: 's1', role: 'user', content: 'Hyperlight 与 Monty 的启动耗时分别统计一下。', createdAt: now(), status: 'done' },
  ],
  'demo-release': [
    { id: 'dr1', role: 'user', content: '整理 1.0.7 的发布说明草稿。', createdAt: now(), status: 'done' },
    { id: 'dr2', role: 'assistant', content: '发布说明草稿已完成：按「新增 / 修复 / 内部」归类 9 条变更，已在最终输出中给出全文。', createdAt: now(), status: 'done' },
  ],
}

const todos: Record<string, TodoItem[]> = {
  'demo-web': [
    { id: 'td1', content: '重建 App 外壳与导航', status: 'completed' },
    { id: 'td2', content: '桥接层支持 WebSocket 传输', status: 'completed' },
    { id: 'td3', content: '终端 /web-ui 服务端联调', status: 'in_progress' },
    { id: 'td4', content: '管理页与设置页回归', status: 'pending' },
  ],
}

const apiConfigs: ApiConfig[] = [
  { id: 'cfg-openai', providerId: 'openai', displayName: 'OpenAI · 生产', baseUrl: 'https://api.openai.com/v1', apiKey: '••••••••', defaultModel: 'gpt-5.1', temperature: 0.7, maxTokens: 8192, isEnabled: true, extraHeadersJson: null, thinkingEnabled: true, thinkingBudget: 8000 },
  { id: 'cfg-anthropic', providerId: 'anthropic', displayName: 'Anthropic · 备用', baseUrl: 'https://api.anthropic.com', apiKey: '••••••••', defaultModel: 'claude-sonnet-4-6', temperature: 0.6, maxTokens: 8192, isEnabled: true, extraHeadersJson: null, thinkingEnabled: true, thinkingBudget: 12000 },
]

const skills: Skill[] = [
  { id: 'sk-1', name: 'file_read', description: '读取工作区内的文本文件，支持行号窗口。', parametersJson: '{"type":"object","properties":{"path":{"type":"string"}}}', type: 'Builtin', isEnabled: true },
  { id: 'sk-2', name: 'file_edit', description: '精确替换文件内容，返回 diff 预览。', parametersJson: '{"type":"object","properties":{"path":{"type":"string"}}}', type: 'Builtin', isEnabled: true },
  { id: 'sk-3', name: 'release_notes', description: '把提交记录整理成发布说明。', parametersJson: '{"type":"object","properties":{"range":{"type":"string"}}}', commandTemplate: null, type: 'Custom', isEnabled: false },
]

const mcpServers: McpServerConfig[] = [
  { id: 'mcp-1', name: 'everything', command: 'npx', arguments: '-y @modelcontextprotocol/server-everything', isEnabled: true, createdAt: now() },
]

const tasks: BackgroundTask[] = [
  { id: 'job-1', title: '后台任务 · 统计 Hyperlight 启动耗时', sessionId: 'demo-sandbox', kind: '后台任务', state: 'running', createdAt: now(), updatedAt: now(), output: '', currentStep: '运行 12 轮冷启动采样' },
  { id: 'job-2', title: 'general · 整理 1.0.7 的发布说明草稿', sessionId: 'demo-release', kind: '子代理', state: 'completed', createdAt: now(), updatedAt: now(), parentToolCallId: 't5', steps: ['读取提交记录', '归类用户可见变更', '输出发布说明草稿'], finalOutput: '发布说明草稿已完成，包含 9 条变更。' },
  { id: 'job-3', title: 'reviewer · 审阅商业级改版的交互细节', sessionId: 'demo-review', kind: '子代理', state: 'running', createdAt: now(), updatedAt: now(), parentToolCallId: 't5', steps: ['读取 Chat 视图', '检查命令弹窗的键盘交互'], currentStep: '检查命令弹窗的键盘交互', notifyParent: true },
]

const agents: AgentRoleDefinition[] = [
  { name: 'general', description: '通用实现与研究', instructions: 'Complete the delegated task.', readOnly: false, execution: { maxConcurrent: 6, maxDepth: 1, failurePolicy: 'return' } },
]

// PowerShell 能力商店: the verified module catalog a host with the bundled SDK worker reports.
const demoPowershellModules = [
  { name: 'Pester', version: '5.7.1', commandCount: 12, error: null as string | null },
  { name: 'PSReadLine', version: '2.3.6', commandCount: 9, error: null as string | null },
  { name: 'Contoso.Unsupported', version: '1.0.4', commandCount: 0, error: '无法加载文件，因为在此系统上禁止运行脚本。' },
]

function powershellStatus() {
  return {
    workerAvailable: true,
    workerPath: 'C:\\demo\\allagent\\AllAgent.NET.PowerShell.exe',
    root: 'C:\\Users\\demo\\AppData\\Roaming\\AllAgent.NET\\powershell\\Modules',
    modules: structuredClone(demoPowershellModules),
  }
}

// Plugin web extensions the demo transport serves so the preview shows a real contribution:
// a sidebar page, a custom tool card and a palette command, all pointing at a self-contained
// data: page (the real host serves plugin frames from its own loopback origin instead).
const demoPlugin = {
  id: 'sample',
  name: '示例插件',
  packageId: 'AllAgent.NET.SamplePlugin',
  version: '1.0.0',
  enabled: true,
  error: null as string | null,
  tools: ['example_clock'],
  commands: ['hello'],
  web: { webEnabled: true, pages: 1, panels: 0, toolCards: 1, capabilities: ['notifications', 'storage'], error: null },
}
let demoWebEnabled = true
const demoEntry = 'data:text/html;charset=utf-8,' + encodeURIComponent(`<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
<title>示例插件 · 仪表盘</title><style>
:root{color-scheme:light dark;font-family:"Segoe UI Variable","Segoe UI",system-ui,sans-serif}
body{margin:0;padding:20px;background:light-dark(#ffffff,#131316);color:light-dark(#111113,#f5f5f6)}
.badge{display:inline-flex;align-items:center;font-size:11px;padding:2px 9px;border-radius:999px;background:light-dark(#f2f2f1,#1a1a1e);color:light-dark(#666,#9d9da6)}
h1{font-size:16px;margin:12px 0 4px}p{color:light-dark(#666,#9d9da6);font-size:12px;line-height:1.8;margin:0 0 16px}
.grid{display:grid;gap:10px;grid-template-columns:repeat(auto-fit,minmax(140px,1fr))}
.card{border:1px solid light-dark(#e5e5e3,#26262c);border-radius:12px;padding:12px}
.card span{font-size:11px;color:light-dark(#666,#9d9da6)}.card b{display:block;font-size:20px;margin-top:6px;font-weight:600}
</style></head><body>
<span class="badge">演示传输 · 真实宿主中这里是独立源的沙箱 iframe + SDK</span>
<h1>示例插件仪表盘</h1><p>页面由插件自绘，通过 SDK 调用本插件的后端桥方法与宿主能力；主题、上下文与事件由宿主同步。</p>
<div class="grid"><div class="card"><span>今日调用</span><b>128</b></div><div class="card"><span>平均耗时</span><b>42 ms</b></div><div class="card"><span>成功率</span><b>99.2%</b></div></div>
</body></html>`)

// Stand-in workspace tree so the composer's `@` picker is usable in the dev preview.
const workspaceFiles = [
  { path: 'src', name: 'src', isDirectory: true },
  { path: 'src/App.tsx', name: 'App.tsx', isDirectory: false },
  { path: 'src/components', name: 'components', isDirectory: true },
  { path: 'src/components/PromptComposer.tsx', name: 'PromptComposer.tsx', isDirectory: false },
  { path: 'src/components/MessageBubble.tsx', name: 'MessageBubble.tsx', isDirectory: false },
  { path: 'src/stores/conversations.ts', name: 'conversations.ts', isDirectory: false },
  { path: 'src/styles/app.css', name: 'app.css', isDirectory: false },
  { path: 'tests', name: 'tests', isDirectory: true },
  { path: 'tests/bridge.test.ts', name: 'bridge.test.ts', isDirectory: false },
  { path: 'README.md', name: 'README.md', isDirectory: false },
  { path: 'package.json', name: 'package.json', isDirectory: false },
]

// ── File bubble demo ─────────────────────────────────────────────────────────
// A small tree with real contents so the preview pane (text, Markdown, image) is explorable
// without a host. Unknown paths throw, which the page turns into an external-open fallback.

type DemoTreeEntry = { name: string; path: string; directory: boolean; size: number; modified: string; link: boolean; hidden: boolean }

function demoFileEntry(path: string, directory: boolean, size = 0): DemoTreeEntry {
  const name = path.split('/').pop() ?? path
  return { name, path, directory, size, modified: now(), link: false, hidden: name.startsWith('.') }
}

const demoTree: Record<string, DemoTreeEntry[]> = {
  '.': [demoFileEntry('src', true), demoFileEntry('tests', true), demoFileEntry('docs', true), demoFileEntry('README.md', false, 2431), demoFileEntry('package.json', false, 862), demoFileEntry('.gitignore', false, 118)],
  'src': [demoFileEntry('src/App.tsx', false, 15420), demoFileEntry('src/components', true), demoFileEntry('src/stores', true), demoFileEntry('src/styles', true)],
  'src/components': [demoFileEntry('src/components/PromptComposer.tsx', false, 9210), demoFileEntry('src/components/MessageBubble.tsx', false, 11240), demoFileEntry('src/components/Notices.tsx', false, 1680)],
  'src/stores': [demoFileEntry('src/stores/conversations.ts', false, 18400), demoFileEntry('src/stores/git.ts', false, 5200)],
  'src/styles': [demoFileEntry('src/styles/app.css', false, 26800)],
  'tests': [demoFileEntry('tests/bridge.test.ts', false, 3120)],
  'docs': [demoFileEntry('docs/plan.md', false, 1720)],
}

const demoFileText = (path: string): string => {
  if (path.endsWith('.md')) return `# ${path}\n\n演示传输中的 Markdown 预览。\n\n- 标题、列表、**强调**与 \`行内代码\` 都会渲染\n- 接入本地后端后展示真实文件内容\n\n\`\`\`ts\nconst hello = 'world'\n\`\`\`\n`
  if (path.endsWith('.json')) return `{\n  "name": "allagent-web",\n  "version": "2.0.0",\n  "private": true,\n  "note": "演示传输内容"\n}\n`
  return `// ${path}\n// 演示传输：这里显示占位内容；接入本地宿主后读取真实文件。\n\nexport function demo() {\n  return '${path}'\n}\n`
}

// ── Git panel demo ───────────────────────────────────────────────────────────

// A small fork-and-merge history so the commit graph shows lanes, a merge and a tag.
const demoGitCommits = [
  { hash: 'a1b2c3d4e5f60718293a4b5c6d7e8f9012345678', short: 'a1b2c3d', author: 'Luo Lan', authorEmail: 'luolan@example.com', date: now(), subject: 'Web 工作台：文件、Git 与终端浮窗', refs: ['HEAD → main', 'origin/main'], parents: ['b2c3d4e5f60718293a4b5c6d7e8f901234567890', 'c3d4e5f60718293a4b5c6d7e8f90123456789012'] },
  { hash: 'c3d4e5f60718293a4b5c6d7e8f90123456789012', short: 'c3d4e5f', author: 'Luo Lan', authorEmail: 'luolan@example.com', date: new Date(Date.now() - 3600000).toISOString(), subject: '浮窗面板：Git 面板与集成终端', refs: ['feature/panels'], parents: ['d4e5f60718293a4b5c6d7e8f901234567890123'] },
  { hash: 'b2c3d4e5f60718293a4b5c6d7e8f901234567890', short: 'b2c3d4e', author: 'Luo Lan', authorEmail: 'luolan@example.com', date: new Date(Date.now() - 86400000).toISOString(), subject: '修复会话切换时的历史竞态', refs: [], parents: ['d4e5f60718293a4b5c6d7e8f901234567890123'] },
  { hash: 'd4e5f60718293a4b5c6d7e8f901234567890123', short: 'd4e5f60', author: 'AllAgent Bot', authorEmail: 'bot@example.com', date: new Date(Date.now() - 172800000).toISOString(), subject: 'Web UI 外壳与桥接层重构', refs: [], parents: ['e5f60718293a4b5c6d7e8f9012345678901234567'] },
  { hash: 'e5f60718293a4b5c6d7e8f9012345678901234567', short: 'e5f6071', author: 'Luo Lan', authorEmail: 'luolan@example.com', date: new Date(Date.now() - 432000000).toISOString(), subject: '初始提交', refs: ['tag: v1.0.0'], parents: [] },
]

const demoGit = {
  branch: 'main', detached: false, upstream: 'origin/main' as string | null, ahead: 1, behind: 0, operation: null as string | null,
  staged: [{ path: 'src/App.tsx', originalPath: null as string | null, index: 'M', worktree: '.' }],
  unstaged: [
    { path: 'src/styles/app.css', originalPath: null as string | null, index: '.', worktree: 'M' },
    { path: 'src/stores/git.ts', originalPath: null as string | null, index: '.', worktree: 'M' },
  ],
  untracked: [{ path: 'src/components/GitPanel.tsx', originalPath: null as string | null, index: '?', worktree: '?' }],
  conflicted: [] as Array<{ path: string; originalPath: string | null; index: string; worktree: string }>,
  stashes: [{ index: 0, ref: 'stash@{0}', message: 'WIP on main: 调试中的面板改动', date: now() }],
}

const demoDiffLines = (path: string) => [
  { kind: 'context' as const, oldLine: 1, newLine: 1, text: `// ${path}` },
  { kind: 'removed' as const, oldLine: 2, newLine: null, text: 'const theme = "light"' },
  { kind: 'added' as const, oldLine: null, newLine: 2, text: 'const theme = "system"' },
  { kind: 'added' as const, oldLine: null, newLine: 3, text: 'const panels = ["files", "git", "terminal"]' },
  { kind: 'context' as const, oldLine: 3, newLine: 4, text: '' },
]

// ── Integrated terminal demo ─────────────────────────────────────────────────

const demoShells = [
  { id: 'powershell', label: 'Windows PowerShell' },
  { id: 'cmd', label: '命令提示符' },
]

const demoSessions: Array<{
  id: string; title: string; shellId: string; shellLabel: string; cwd: string
  cols: number; rows: number; alive: boolean; exitCode: number | null; sequence: number
}> = []

const settings: AppSettings = {
  browserUseEnabled: true,
  browserUseMode: 'builtin',
  browserExecutablePath: '',
  browserCdpUrl: '',
  theme: 'system',
  language: 'zh',
  defaultModel: '',
  streamingEnabled: true,
  defaultProviderId: 'cfg-openai',
  gitCommitConfigId: '',
}

const reply = '收到。我先把任务拆成三步执行：\n\n1. 读取当前实现，确认渲染路径与状态来源；\n2. 在沙箱里跑一次端到端脚本并收集输出；\n3. 汇总结果，给出可复用的结论。\n\n这是**演示传输**，接入桌面端或 `/web-ui` 后会执行真实任务。'

export function createMockTransport(deliver: Deliver) {
  let streamTimer: number | undefined
  let cancelled = false
  let taskTick = 0

  function emit(eventName: string, data: unknown, streamId: string) {
    deliver(JSON.stringify({ type: 'event', eventName, data, streamId }))
  }

  // ── 集成终端演示：带提示符的回显 shell ─────────────────────────────────────
  function termEmit(id: string, text: string) {
    const session = demoSessions.find(item => item.id === id)
    if (!session) return
    session.sequence += 1
    emit('terminal.output', { base64: utf8ToBase64(text) }, id)
  }
  function termPrompt(id: string) {
    termEmit(id, 'PS C:\\demo\\allagent-web> ')
  }
  function termRun(id: string, command: string) {
    const trimmed = command.trim()
    if (trimmed === 'git status') {
      termEmit(id, "On branch main\r\nYour branch is ahead of 'origin/main' by 1 commit.\r\n\r\nChanges not staged for commit:\r\n\tmodified:   src/styles/app.css\r\n\r\nUntracked files:\r\n\tsrc/components/GitPanel.tsx\r\n\r\n")
    } else if (trimmed === 'dir' || trimmed === 'ls') {
      termEmit(id, '\r\n    目录: C:\\demo\\allagent-web\r\n\r\nMode   LastWriteTime       Length Name\r\n----   -------------       ------ ----\r\nd----  2026/10/19 09:12        src\r\n----a-  2026/10/19 09:12       2431 README.md\r\n\r\n')
    } else if (trimmed === 'cls' || trimmed === 'clear') {
      termEmit(id, '\u001b[2J\u001b[H')
    } else if (trimmed.length) {
      termEmit(id, `\r\n\u001b[33m演示传输\u001b[0m：接入本地宿主后，「${trimmed}」会执行真实命令并返回真实输出。\r\n`)
    }
    termPrompt(id)
  }

  // ── Git 演示：内存状态 + 流式作业 ─────────────────────────────────────────
  function gitStatusDto() {
    return {
      branch: demoGit.branch, detached: demoGit.detached, upstream: demoGit.upstream, ahead: demoGit.ahead,
      behind: demoGit.behind, operation: demoGit.operation,
      staged: structuredClone(demoGit.staged), unstaged: structuredClone(demoGit.unstaged),
      untracked: structuredClone(demoGit.untracked), conflicted: structuredClone(demoGit.conflicted),
    }
  }
  function moveFiles(paths: string[], from: Array<{ path: string; originalPath: string | null; index: string; worktree: string }>, to: typeof from, index: string, worktree: string) {
    const moved = from.filter(entry => paths.includes(entry.path))
    for (const entry of moved) {
      from.splice(from.indexOf(entry), 1)
      to.push({ ...entry, index, worktree })
    }
  }
  function startGitJob(label: string): { id: string; label: string } {
    const id = `job-${crypto.randomUUID()}`
    const lines = label.includes('推送')
      ? ['枚举对象中: 38, 完成.', '写入对象中: 100% (38/38), 完成.', 'To github.com:example/allagent.git', '   a1b2c3d..f9e8d7c  main -> main']
      : ['来自 github.com:example/allagent (fetch)', ' * [新分支]      feature/panels -> origin/feature/panels']
    lines.forEach((text, index) => {
      window.setTimeout(() => emit('git.output', { jobId: id, text, error: false }, ''), 140 * (index + 1))
    })
    window.setTimeout(() => emit('git.done', { jobId: id, label, ok: true, message: label + '完成。' }, ''), 160 * (lines.length + 1))
    return { id, label }
  }

  function startStream(conversationId: string, streamId: string) {
    cancelled = false
    const answer = reply
    const words = answer.split(/(\s+)/)
    let index = 0
    const thinking = '演示模式：先拆解任务，再逐步执行，最后汇总结论。'
    let thinkingIndex = 0
    const tick = () => {
      if (cancelled) return
      if (thinkingIndex < thinking.length) {
        emit('agent.thinking', { text: thinking.slice(thinkingIndex, thinkingIndex + 6) }, streamId)
        thinkingIndex += 6
        streamTimer = window.setTimeout(tick, 90)
        return
      }
      if (index < 4) {
        index += 1
        const id = `tool-${index}`
        emit('agent.toolCallStarted', { id, name: index === 1 ? 'execute_code' : 'file_read', args: index === 1 ? '{"code":"print(1)"}' : '{"path":"src/App.tsx"}' }, streamId)
        window.setTimeout(() => emit('agent.toolCallOutput', { id, line: 'running…', isError: false }, streamId), 120)
        window.setTimeout(() => emit('agent.toolCallCompleted', { id, name: index === 1 ? 'execute_code' : 'file_read', result: '{"ok":true,"data":{"exit_code":0}}' }, streamId), 320)
        streamTimer = window.setTimeout(tick, 460)
        return
      }
      if (index < 4 + words.length) {
        const text = words[index - 4] ?? ''
        emit('agent.token', { text }, streamId)
        index += 1
        streamTimer = window.setTimeout(tick, 26)
        return
      }
      emit('agent.contextUsage', { usedTokens: 1840 + conversationId.length * 37, capacityTokens: 258000, estimated: false }, streamId)
      emit('agent.done', { reason: 'stop' }, streamId)
    }
    streamTimer = window.setTimeout(tick, 200)
  }

  function call(method: string, params?: unknown): unknown {
    const p = (params ?? {}) as Record<string, string>
    switch (method) {
      case 'conversations.list': return structuredClone(conversations)
      case 'conversations.create': {
        const conversation: Conversation = { id: crypto.randomUUID(), title: 'New Conversation', modelId: '', providerId: '', createdAt: now(), updatedAt: now() }
        conversations.unshift(conversation)
        messages[conversation.id] = []
        return structuredClone(conversation)
      }
      case 'conversations.rename': {
        const found = conversations.find(item => item.id === p.id)
        if (found) found.title = p.title
        return true
      }
      case 'conversations.delete': {
        const index = conversations.findIndex(item => item.id === p.id)
        if (index >= 0) conversations.splice(index, 1)
        delete messages[p.id]
        return true
      }
      case 'conversations.getMessages': return structuredClone(messages[p.conversationId] ?? [])
      case 'conversations.running': return []
      case 'conversations.cancelRun': return false
      case 'todos.get': return structuredClone(todos[p.conversationId] ?? [])
      case 'todos.set': {
        todos[p.conversationId] = structuredClone((params as { todos: TodoItem[] }).todos)
        return structuredClone(todos[p.conversationId])
      }
      case 'interactions.pending': return []
      case 'chat.getStreamState': return null
      case 'workspace.get': return structuredClone(demoWorkspace)
      case 'shell.openPath': return true
      case 'workspace.recent': return []
      case 'workspaces.list': return [{ ...structuredClone(demoWorkspace), name: 'allagent-web', sessionCount: conversations.length, isCurrent: true, runningCount: 0 }]
      case 'session.controls': return structuredClone(demoControls)
      // Footer and cache counters a real host serves; the demo hides the provider latency.
      case 'session.footer': return {
        refreshing: false,
        items: [
          { label: 'Codex 额度', value: '五小时剩余 92% · 09-24 15:00 重置' },
          { label: '每周剩余', value: '68%' },
        ],
      }
      case 'conversations.usage': return {
        contextUsedTokens: 24800,
        contextEstimated: false,
        cacheHitRate: 86.4,
        cacheInputTokens: 128400,
        cachedInputTokens: 110937,
        cacheWriteInputTokens: 4820,
        cacheRequests: 9,
      }
      case 'session.setPermission': {
        demoControls.permissionMode = String((params as { mode?: string }).mode ?? demoControls.permissionMode)
        demoControls.permissionLabel = permissionLabel(demoControls.permissionMode)
        return structuredClone(demoControls)
      }
      case 'session.setSandbox': {
        demoControls.sandboxMode = String((params as { mode?: string }).mode ?? demoControls.sandboxMode)
        demoControls.sandboxEffective = '演示传输 · 未连接沙箱'
        return structuredClone(demoControls)
      }
      case 'workspace.files': {
        const query = String((params as { query?: string }).query ?? '').toLowerCase()
        return structuredClone(workspaceFiles
          .filter(entry => entry.path.toLowerCase().startsWith(query) || entry.name.toLowerCase().startsWith(query))
          .slice(0, 40))
      }
      // ── 文件气泡（只读预览） ───────────────────────────────────────────────
      case 'workspace.tree': {
        const requested = String((params as { path?: string }).path ?? '.').replace(/\\/g, '/')
        const directory = requested === '' ? '.' : requested
        if (!(directory in demoTree)) throw new Error('目录不存在：' + directory)
        return structuredClone(demoTree[directory])
      }
      case 'workspace.readFile': {
        const path = String((params as { path?: string }).path ?? '').replace(/\\/g, '/')
        if (!path || path in demoTree || workspaceFiles.some(entry => entry.path === path && entry.isDirectory))
          throw new Error('这是一个目录，无法预览文件内容。')
        if (!workspaceFiles.some(entry => entry.path === path) && !path.startsWith('docs/'))
          throw new Error('文件不存在：' + path)
        const text = demoFileText(path)
        return { path, kind: 'text', size: text.length, modified: now(), text, base64: null, truncated: false }
      }
      // ── Git 面板 ───────────────────────────────────────────────────────────
      case 'git.summary': return {
        available: true, version: 'git version 2.51.0.windows.1', repo: true, root: demoWorkspace.workspace,
        branch: demoGit.branch, detached: demoGit.detached, upstream: demoGit.upstream, ahead: demoGit.ahead,
        behind: demoGit.behind, operation: demoGit.operation,
        staged: demoGit.staged.length, unstaged: demoGit.unstaged.length, untracked: demoGit.untracked.length,
        conflicted: demoGit.conflicted.length,
      }
      case 'git.status': return gitStatusDto()
      case 'git.diff': {
        const target = params as { path?: string | null; staged?: boolean; commit?: string | null }
        const path = target.path ?? '全部更改'
        return { path, commit: target.commit ?? null, staged: Boolean(target.staged), added: 2, removed: 1, truncated: false, binary: false, lines: demoDiffLines(path), note: null }
      }
      case 'git.log': {
        const options = (params ?? {}) as { skip?: number; take?: number; ref?: string | null; grep?: string | null }
        let commits = structuredClone(demoGitCommits)
        if (options.ref) commits = commits.filter(commit => commit.refs.some(ref => ref.replace(/^HEAD -> /, '').replace(/^tag: /, '') === options.ref || ref.includes(`/${options.ref}`)))
        if (options.grep) commits = commits.filter(commit => commit.subject.toLowerCase().includes(options.grep!.toLowerCase()))
        const skip = options.skip ?? 0
        const take = options.take ?? 60
        return { commits: commits.slice(skip, skip + take), more: commits.length > skip + take }
      }
      case 'git.show': {
        const hash = String((params as { hash?: string }).hash ?? '')
        const commit = demoGitCommits.find(item => item.hash === hash) ?? demoGitCommits[0]
        return {
          commit: structuredClone(commit),
          body: '演示传输：提交详情来自内置示例数据；接入本地宿主后显示真实提交正文。',
          files: [
            { path: 'src/App.tsx', status: 'M', originalPath: null },
            { path: 'src/components/GitPanel.tsx', status: 'A', originalPath: null },
          ],
        }
      }
      case 'git.branches': return {
        current: demoGit.branch,
        local: [
          { name: 'main', current: true, upstream: 'origin/main', date: now(), subject: 'Web 工作台：文件、Git 与终端浮窗' },
          { name: 'feature/panels', current: false, upstream: null, date: now(), subject: '浮窗面板原型' },
        ],
        remote: [{ name: 'origin/main', date: now(), subject: 'Web 工作台：文件、Git 与终端浮窗' }],
        tags: [{ name: 'v1.0.0', date: now() }],
      }
      case 'git.stashList': return structuredClone(demoGit.stashes)
      case 'git.remotes': return [{ name: 'origin', url: 'https://github.com/example/allagent.git', pushUrl: '' }]
      case 'git.identity': return { name: 'Luo Lan', email: 'luolan@example.com' }
      case 'git.setIdentity': return { name: (params as { name: string }).name, email: (params as { email: string }).email }
      case 'git.installGit': return { ok: true, message: '演示传输：Git 已就绪（模拟）。' }
      case 'git.generateCommitMessage': return 'feat: 浮窗面板支持文件、Git 与终端\n\n- 文件气泡提供只读预览与引用到对话\n- Git 面板覆盖暂存、提交、推送与分支管理\n- 演示传输返回示例信息，接入本地宿主后由真实模型生成'
      case 'git.stage': {
        if ((params as { all?: boolean }).all) {
          moveFiles(demoGit.unstaged.map(item => item.path), demoGit.unstaged, demoGit.staged, 'M', '.')
          moveFiles(demoGit.untracked.map(item => item.path), demoGit.untracked, demoGit.staged, 'A', '.')
        } else moveFiles((params as { paths?: string[] }).paths ?? [], [...demoGit.unstaged, ...demoGit.untracked], demoGit.staged, 'M', '.')
        return gitStatusDto()
      }
      case 'git.unstage': {
        const paths = (params as { all?: boolean }).all ? demoGit.staged.map(item => item.path) : (params as { paths?: string[] }).paths ?? []
        moveFiles(paths, demoGit.staged, demoGit.unstaged, '.', 'M')
        return gitStatusDto()
      }
      case 'git.discard': {
        const paths = (params as { paths?: string[] }).paths ?? []
        const remove = (list: Array<{ path: string }>) => {
          for (const path of paths) {
            const index = list.findIndex(item => item.path === path)
            if (index >= 0) list.splice(index, 1)
          }
        }
        remove(demoGit.staged)
        remove(demoGit.unstaged)
        remove(demoGit.untracked)
        return gitStatusDto()
      }
      case 'git.resolveConflict': {
        const path = (params as { path?: string }).path ?? ''
        moveFiles([path], demoGit.conflicted, demoGit.staged, 'M', '.')
        return gitStatusDto()
      }
      case 'git.commit': {
        demoGit.staged = []
        demoGitCommits.unshift({
          hash: crypto.randomUUID().replace(/-/g, ''),
          short: crypto.randomUUID().slice(0, 7),
          author: 'Luo Lan', authorEmail: 'luolan@example.com', date: now(),
          subject: (params as { message?: string }).message?.split('\n')[0] || '演示提交',
          refs: ['HEAD → main'],
          parents: [demoGitCommits[0].hash],
        })
        return gitStatusDto()
      }
      case 'git.switch': {
        demoGit.branch = String((params as { name?: string }).name ?? demoGit.branch)
        demoGit.upstream = demoGit.branch === 'main' ? 'origin/main' : null
        return gitStatusDto()
      }
      case 'git.deleteBranch': return gitStatusDto()
      case 'git.merge': case 'git.rebase': case 'git.resetSoft': return gitStatusDto()
      case 'git.undoLastCommit': {
        if (demoGitCommits.length > 1) demoGitCommits.shift()
        return gitStatusDto()
      }
      case 'git.operationContinue': case 'git.operationAbort': {
        demoGit.operation = null
        demoGit.conflicted = []
        return gitStatusDto()
      }
      case 'git.stashPush': {
        demoGit.stashes.unshift({ index: 0, ref: 'stash@{0}', message: (params as { message?: string }).message || 'WIP on main', date: now() })
        demoGit.staged = []
        demoGit.unstaged = []
        return gitStatusDto()
      }
      case 'git.stashApply': case 'git.stashPop': {
        if ((params as { index?: number }).index === undefined || (params as { index?: number }).index === 0) demoGit.stashes.shift()
        return gitStatusDto()
      }
      case 'git.stashDrop': {
        const index = (params as { index?: number }).index ?? 0
        demoGit.stashes.splice(index, 1)
        return gitStatusDto()
      }
      case 'git.fetch': return startGitJob('获取远程更新')
      case 'git.pull': return startGitJob('拉取更新')
      case 'git.push': return startGitJob('推送')
      case 'git.init': return gitStatusDto()
      // ── 集成终端 ───────────────────────────────────────────────────────────
      case 'terminal.shells': return structuredClone(demoShells)
      case 'terminal.list': return structuredClone(demoSessions)
      case 'terminal.create': {
        const shellId = String((params as { shell?: string }).shell ?? demoShells[0].id)
        const shell = demoShells.find(item => item.id === shellId) ?? demoShells[0]
        const session = {
          id: crypto.randomUUID(),
          title: shell.label + (demoSessions.length ? ` (${demoSessions.length + 1})` : ''),
          shellId: shell.id, shellLabel: shell.label,
          cwd: String((params as { cwd?: string }).cwd ?? demoWorkspace.workspace),
          cols: Number((params as { cols?: number }).cols ?? 120), rows: Number((params as { rows?: number }).rows ?? 30),
          alive: true, exitCode: null, sequence: 0,
        }
        demoSessions.push(session)
        window.setTimeout(() => termEmit(session.id, '\u001b[32m演示传输 · 此终端由浏览器模拟\u001b[0m\r\n输入 dir、git status 或任意命令试试；接入本地宿主后即为真实 ConPTY shell。\r\n'), 60)
        window.setTimeout(() => termPrompt(session.id), 120)
        return structuredClone(session)
      }
      case 'terminal.attach': {
        const id = String((params as { id?: string }).id ?? '')
        const session = demoSessions.find(item => item.id === id) ?? demoSessions[0]
        if (!session) throw new Error('终端会话不存在或已关闭：' + id)
        return { session: structuredClone(session), replay: '', sequence: session.sequence }
      }
      case 'terminal.write': {
        const id = String((params as { id?: string }).id ?? '')
        const data = String((params as { data?: string }).data ?? '')
        const text = new TextDecoder().decode(Uint8Array.from(atob(data), char => char.charCodeAt(0)))
        if (text.includes('\r')) {
          const command = text.replace(/\r/g, '')
          termEmit(id, command + '\r\n')
          termRun(id, command)
        }
        return true
      }
      case 'terminal.resize': {
        const id = String((params as { id?: string }).id ?? '')
        const session = demoSessions.find(item => item.id === id)
        if (session) {
          session.cols = Number((params as { cols?: number }).cols ?? session.cols)
          session.rows = Number((params as { rows?: number }).rows ?? session.rows)
        }
        return true
      }
      case 'terminal.close': {
        const id = String((params as { id?: string }).id ?? '')
        const index = demoSessions.findIndex(item => item.id === id)
        if (index >= 0) demoSessions.splice(index, 1)
        return true
      }
      case 'chat.startStream': {
        const streamId = `mock-${Date.now()}`
        startStream(p.conversationId, streamId)
        return streamId
      }
      case 'chat.cancelStream': { cancelled = true; window.clearTimeout(streamTimer); return true }
      case 'chat.compact': {
        const streamId = `mock-compact-${Date.now()}`
        window.setTimeout(() => emit('agent.notice', { message: '正在整理上下文；完整消息历史仍保留。' }, streamId), 120)
        window.setTimeout(() => {
          emit('agent.notice', { message: '上下文已整理，完整历史仍可查看。' }, streamId)
          emit('agent.done', { reason: 'stop' }, streamId)
        }, 900)
        return streamId
      }
      // Demo transport: advance the running child on every poll so live step refresh is visible,
      // and let it finish after a few ticks to show the running → completed transition.
      case 'tasks.list': {
        const job = tasks.find(item => item.id === 'job-3')
        if (job?.state === 'running' && job.steps?.length) {
          taskTick += 1
          if (taskTick >= 12) {
            job.state = 'completed'
            job.currentStep = null
            job.finalOutput = '商业级改版交互审阅完成：未发现阻断问题，2 条建议已汇总。'
          } else {
            job.currentStep = job.steps[taskTick % job.steps.length]
          }
          job.updatedAt = now()
          const notes = messages['demo-review'] ?? []
          if (taskTick % 2 === 0 && notes.length < 2) {
            notes.push({
              id: `dr-live-${notes.length + 1}`,
              role: 'assistant',
              content: notes.length === 0 ? '已定位命令弹窗的键盘处理分支。' : '确认 Esc/Tab 的焦点迁移路径，无回归风险。',
              createdAt: now(),
              status: 'done',
            })
            messages['demo-review'] = notes
          }
        }
        return structuredClone(tasks)
      }
      case 'tasks.get': return structuredClone(tasks.find(item => item.id === p.id) ?? null)
      case 'tasks.cancel': return true
      case 'skills.list': return structuredClone(skills)
      case 'skills.toggle': { const skill = skills.find(item => item.id === p.id); if (skill) skill.isEnabled = Boolean((params as { enabled: boolean }).enabled); return true }
      case 'skills.delete': { const index = skills.findIndex(item => item.id === p.id); if (index >= 0) skills.splice(index, 1); return true }
      case 'skills.import': { const skill = { id: crypto.randomUUID(), name: 'imported_skill', description: '来自演示传输的导入', parametersJson: '{}', type: 'Custom' as const, isEnabled: true }; skills.push(skill); return structuredClone(skill) }
      case 'skills.importZip': return []
      case 'apiConfigs.list': return structuredClone(apiConfigs)
      case 'apiConfigs.save': {
        const incoming = params as ApiConfig & { id?: string | null }
        const found = incoming.id ? apiConfigs.find(item => item.id === incoming.id) : undefined
        if (found) { Object.assign(found, incoming); return structuredClone(found) }
        const created = { ...incoming, id: crypto.randomUUID(), apiKey: '••••••••' } as ApiConfig
        apiConfigs.push(created)
        return structuredClone(created)
      }
      case 'apiConfigs.delete': { const index = apiConfigs.findIndex(item => item.id === p.id); if (index >= 0) apiConfigs.splice(index, 1); return true }
      case 'apiConfigs.test': return true
      case 'mcpServers.list': return structuredClone(mcpServers)
      case 'mcpServers.save': {
        const incoming = params as McpServerConfig & { id?: string | null }
        const found = incoming.id ? mcpServers.find(item => item.id === incoming.id) : undefined
        if (found) { Object.assign(found, incoming, { isConnected: false }); return structuredClone(found) }
        const created = { ...incoming, id: crypto.randomUUID(), createdAt: now() } as McpServerConfig
        mcpServers.push(created)
        return structuredClone(created)
      }
      case 'mcpServers.delete': { const index = mcpServers.findIndex(item => item.id === p.id); if (index >= 0) mcpServers.splice(index, 1); return true }
      case 'mcpServers.connect': { const server = mcpServers.find(item => item.id === p.id); if (!server?.isEnabled) return false; server.isConnected = true; return true }
      case 'mcpServers.disconnect': { const server = mcpServers.find(item => item.id === p.id); if (server) server.isConnected = false; return true }
      case 'agents.list': return structuredClone(agents)
      case 'agents.import': { const parsed = JSON.parse((params as { json: string }).json) as AgentRoleDefinition[]; agents.splice(0, agents.length, ...parsed); return true }
      case 'settings.get': return structuredClone(settings)
      case 'settings.save': { Object.assign(settings, params); return true }
      case 'agent.approvalRespond': return true
      case 'agent.questionAnswer': return true
      case 'plugins.search': return {
        totalHits: 2,
        packages: [
          { id: 'AllAgent.NET.SamplePlugin', version: '1.0.0', description: '演示插件包：工具、命令、Hooks 与 Web 界面扩展。', authors: 'AllAgent.NET', downloads: 42 },
          { id: 'LanAgent.WorkBuddy', version: '1.0.0', description: 'WorkBuddy browser login, streaming adapter and quota query.', authors: 'LanAgent', downloads: 0 },
        ],
      }
      case 'plugins.list': return {
        plugins: [{ ...structuredClone(demoPlugin), enabled: demoPlugin.enabled }],
        providers: [],
        authentication: [],
        hooks: [{ name: 'sample.audit', enabled: true }],
      }
      case 'plugins.setEnabled': { demoPlugin.enabled = Boolean((params as { enabled?: boolean }).enabled); return true }
      case 'plugins.remove': return true
      case 'plugins.setHookEnabled': return true
      case 'plugins.runCommand': return '演示传输：插件命令会在这里返回文本输出。'
      // PowerShell 能力商店: a verified module catalog, a Gallery search and a download that
      // appends to the installed list so the install flow has a real end state.
      case 'powershell.status': return powershellStatus()
      case 'powershell.refresh': return powershellStatus()
      case 'powershell.search': return [
        { name: 'Pester', version: '5.7.1', author: 'Pester Team', description: 'PowerShell 单元测试与模拟框架，支持代码覆盖率与并发执行。' },
        { name: 'PSScriptAnalyzer', version: '1.22.0', author: 'Microsoft Corporation', description: 'PowerShell 脚本静态分析与最佳实践检查。' },
        { name: 'PSReadLine', version: '2.3.6', author: 'Microsoft Corporation', description: '提供命令行编辑、语法着色和交互历史。' },
      ]
      case 'powershell.install': {
        const name = String((params as { name?: string }).name ?? 'Module')
        const version = String((params as { version?: string }).version ?? '1.0.0')
        if (!demoPowershellModules.some(module => module.name === name && module.version === version))
          demoPowershellModules.push({ name, version, commandCount: 4, error: null })
        return powershellStatus()
      }
      case 'powershell.commands': return {
        name: p.name,
        version: p.version,
        total: 4,
        commands: ['Invoke-Pester', 'New-PesterConfiguration', 'Get-PesterResult', 'Set-ItResult'],
        error: null,
      }
      // Plugin web extensions: the contribution snapshot the shell renders (pages, tool cards,
      // palette commands) and the per-plugin soft switch.
      case 'web.contributions': return {
        sdkVersion: 1,
        assetBaseUrl: null,
        plugins: [{
          packageId: demoPlugin.packageId,
          pluginId: demoPlugin.id,
          name: demoPlugin.name,
          version: demoPlugin.version,
          enabled: demoPlugin.enabled,
          webEnabled: demoWebEnabled,
          active: demoPlugin.enabled && demoWebEnabled,
          capabilities: ['notifications', 'storage'],
          contributes: {
            pages: [{ id: 'dashboard', title: '演示仪表盘', entry: demoEntry, icon: 'gauge', order: 40 }],
            toolCards: [{ tool: 'example_clock', entry: demoEntry }],
            commands: [{ id: 'open-dashboard', title: '打开演示仪表盘', page: 'dashboard' }],
          },
          devUrl: null,
          error: null,
        }],
      }
      case 'web.setEnabled': { demoWebEnabled = Boolean((params as { enabled?: boolean }).enabled); return true }
      case 'tools.list': return {
        errors: [],
        tools: [
          { name: 'execute_code', description: '在沙箱中运行代码完成任务。', risk: 'execute', parametersJson: '{"type":"object"}', source: 'host', owner: '' },
          { name: 'file_read', description: '读取工作区文件。', risk: 'read', parametersJson: '{"type":"object"}', source: 'host', owner: '' },
          { name: 'sample_clock', description: '示例插件提供的时钟工具。', risk: 'read', parametersJson: '{"type":"object"}', source: 'plugin', owner: 'AllAgent.NET.SamplePlugin' },
        ],
      }
      case 'commands.list': return [
        { text: '/help', description: '打开命令面板' },
        { text: '/new', description: '开始新任务，旧会话自动保留', aliases: '/clear' },
        { text: '/permissions', description: '切换权限模式', usage: '[restricted|ask|full|plan]' },
        { text: '/skills', description: '管理技能目录' },
        { text: '/ext release_notes', description: '插件命令：把提交记录整理成发布说明', usage: '参数', advanced: true },
        { text: '/timeline', description: '按用户消息定位历史并执行消息操作', usage: '[list|jump|copy|restore|fork|revert ID]', advanced: true },
        { text: '/shell_exec', description: '运行一次经过审批的 shell 命令。', usage: '任务参数', advanced: true, isSkill: true },
        { text: '/file_read', description: '读取工作区内的文本文件，支持行号窗口。', usage: '任务参数', advanced: true, isSkill: true },
        { text: '/file_edit', description: '精确替换文件内容，返回 diff 预览。', usage: '任务参数', advanced: true, isSkill: true },
        { text: '/release_notes', description: '把提交记录整理成发布说明。', usage: '任务参数', advanced: true, isSkill: true },
      ]
      case 'skills.invoke': return {
        text: `Use the following user-selected skill. Its instructions do not override sandbox or permission rules.\n<skill name="${(params as { name: string }).name}">\n（演示传输：这里会注入技能的完整指令正文，$ARGUMENTS 已被替换）\n</skill>\nTask / arguments: ${(params as { arguments?: string }).arguments ?? ''}`,
        images: [],
      }
      case 'auth.providers': return [
        { id: 'codex', name: 'ChatGPT 账号', kind: 'oauth', loggedIn: false, account: '', message: '未登录', modelConnectionAvailable: true, pending: false },
        { id: 'openai', name: 'OpenAI', kind: 'apikey', loggedIn: false, account: '', message: '尚未配置 · 粘贴 API Key 即可使用', modelConnectionAvailable: true, providerId: 'openai' },
      ]
      case 'auth.login': return true
      case 'auth.loginInput': return { id: p.id, name: 'OpenAI', kind: 'apikey', loggedIn: true, account: '', message: '密钥已就绪', modelConnectionAvailable: true }
      case 'auth.logout': return { id: p.id, name: p.id, kind: 'oauth', loggedIn: false, account: '', message: '', modelConnectionAvailable: true }
      default: throw new Error(`演示传输暂不支持 ${method}，请连接本地后端。`)
    }
  }

  return { call }
}
