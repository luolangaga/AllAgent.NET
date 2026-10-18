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

type Deliver = (json: string) => void

const now = () => new Date().toISOString()

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
}

const reply = '收到。我先把任务拆成三步执行：\n\n1. 读取当前实现，确认渲染路径与状态来源；\n2. 在沙箱里跑一次端到端脚本并收集输出；\n3. 汇总结果，给出可复用的结论。\n\n这是**演示传输**，接入桌面端或 `/web-ui` 后会执行真实任务。'

export function createMockTransport(deliver: Deliver) {
  let streamTimer: number | undefined
  let cancelled = false

  function emit(eventName: string, data: unknown, streamId: string) {
    deliver(JSON.stringify({ type: 'event', eventName, data, streamId }))
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
      case 'todos.get': return structuredClone(todos[p.conversationId] ?? [])
      case 'todos.set': {
        todos[p.conversationId] = structuredClone((params as { todos: TodoItem[] }).todos)
        return structuredClone(todos[p.conversationId])
      }
      case 'interactions.pending': return []
      case 'chat.getStreamState': return null
      case 'workspace.files': {
        const query = String((params as { query?: string }).query ?? '').toLowerCase()
        return structuredClone(workspaceFiles
          .filter(entry => entry.path.toLowerCase().startsWith(query) || entry.name.toLowerCase().startsWith(query))
          .slice(0, 40))
      }
      case 'chat.startStream': {
        const streamId = `mock-${Date.now()}`
        startStream(p.conversationId, streamId)
        return streamId
      }
      case 'chat.cancelStream': { cancelled = true; window.clearTimeout(streamTimer); return true }
      case 'tasks.list': return structuredClone(tasks)
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
          { id: 'AllAgent.NET.SamplePlugin', version: '1.0.0', description: '演示插件包：工具、命令与 Hooks。', authors: 'AllAgent.NET', downloads: 42 },
          { id: 'LanAgent.WorkBuddy', version: '1.0.0', description: 'WorkBuddy browser login, streaming adapter and quota query.', authors: 'LanAgent', downloads: 0 },
        ],
      }
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
        { text: '/permissions', description: '切换权限模式', usage: '[ask|full|plan]' },
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
