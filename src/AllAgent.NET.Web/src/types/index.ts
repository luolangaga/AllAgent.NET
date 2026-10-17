// Shared TypeScript contracts for the AllAgent.NET web workspace.
// Field names mirror the camelCase JSON that the C# bridge emits.

export type TodoStatus = 'pending' | 'in_progress' | 'completed'

export interface TodoItem {
  id: string
  content: string
  status: TodoStatus
}

export interface Conversation {
  id: string
  title: string
  modelId: string
  providerId: string
  createdAt: string
  updatedAt: string
  workspace?: string
  workspaceName?: string
  agentRole?: string | null
  parentId?: string | null
}

export type MessageRole = 'user' | 'assistant' | 'tool' | 'system'
export interface ReasoningPart {
  type: 'reasoning'
  text: string
  iteration: number
  status: 'streaming' | 'done' | 'interrupted'
  startedAt?: number
  completedAt?: number
}
export type MessagePart = { type: 'text'; text: string } | { type: 'tool'; id: string } | ReasoningPart

export interface Message {
  id: string
  role: MessageRole
  content: string
  toolCallsJson?: string
  hostToolTraceJson?: string
  isHostActivity?: boolean
  isInterrupted?: boolean
  toolCallId?: string
  toolName?: string
  reasoningContent?: string
  createdAt: string
  status?: 'streaming' | 'done' | 'error'
  /** Fatal run error reported by the backend; rendered as an inline alert. */
  error?: string
  finishReason?: string
  notice?: string
  toolCalls?: ToolCall[]
  /** Ordered live output: text stays at the point where it was emitted. */
  parts?: MessagePart[]
  iteration?: { current: number; maximum: number; toolCalls: number }
}

export type ToolCallStatus = 'running' | 'done' | 'error' | 'cancelled'

export interface ToolCall {
  id: string
  parentId?: string
  name: string
  args: string
  output?: string
  status: ToolCallStatus
  /** Set when the host policy rejected the call; the stream keeps running. */
  rejected?: boolean
  rejectionKind?: 'protocol' | 'policy'
  /** Short host note shown inside the trace row (rejection reason, interruption hint). */
  note?: string
  outputLines?: string[]
}

export type SkillType = 'Builtin' | 'Shell' | 'Custom'

export interface Skill {
  id: string
  name: string
  description: string
  parametersJson: string
  commandTemplate?: string | null
  type: SkillType
  isEnabled: boolean
}

export interface ApiConfig {
  id: string
  providerId: string
  displayName: string
  baseUrl: string
  apiKey: string
  defaultModel: string
  temperature: number
  maxTokens: number
  isEnabled: boolean
  extraHeadersJson?: string | null
  thinkingEnabled: boolean
  thinkingBudget: number
  contextWindowTokens?: number
  protocol?: string
  reasoningProtocol?: string
  reasoningEffort?: string
  models?: string[] | null
  apiKeyEnvironmentVariable?: string | null
  authenticationProviderId?: string | null
  effectiveContextTokens?: number
  effectiveProtocol?: string
}

export interface AppSettings {
  browserUseEnabled: boolean
  browserUseMode: 'builtin' | 'chromium'
  browserExecutablePath: string
  browserCdpUrl: string
  theme: 'light' | 'dark' | 'system'
  language: 'zh' | 'en'
  defaultModel: string
  streamingEnabled: boolean
  defaultProviderId: string
}

export interface McpServerConfig {
  id: string
  name: string
  command: string
  arguments: string
  isEnabled: boolean
  isConnected?: boolean
  createdAt: string
}

export type TaskState =
  | 'queued'
  | 'running'
  | 'awaitingApproval'
  | 'awaitingAnswer'
  | 'completed'
  | 'failed'
  | 'cancelled'
  | 'interrupted'
  | 'cancelling'

export interface BackgroundTask {
  id: string
  title: string
  sessionId: string
  kind: string
  state: TaskState | number
  createdAt: string
  updatedAt: string
  output?: string
  error?: string | null
  finalOutput?: string | null
  ownerId?: string | null
  parentSessionId?: string | null
  parentToolCallId?: string | null
  notifyParent?: boolean
  currentStep?: string | null
  steps?: string[]
}

export interface AgentRoleDefinition {
  name: string
  description?: string
  instructions?: string
  readOnly?: boolean
  model?: string | null
  provider?: string | null
  tools?: string[] | null
  execution?: Record<string, unknown> | null
}

export interface ContextUsage {
  usedTokens: number
  capacityTokens: number
  estimated: boolean
}

export interface ApprovalRequest {
  requestId: string
  name: string
  risk: string
  args: string
  source?: string | null
  requiresExplicitApproval?: boolean
}

export interface AgentQuestionRequest {
  id: string
  question: string
  choices: string[]
  multiple: boolean
}

export interface SkillCatalogSnapshot {
  skills: SkillInfo[]
  diagnostics: string[]
}

export interface SkillInfo {
  name: string
  description: string
  scope: string
  enabled: boolean
  userInvocable: boolean
  modelInvocable: boolean
}

export interface WorkspaceInfo {
  workspace: string
  dataDirectory: string
  sharedDirectory: string
  workspacesRoot?: string
}

export interface WorkspaceEntry {
  workspace: string
  name: string
  dataDirectory: string
  sessionCount: number
  isCurrent: boolean
}

export interface SessionControlState {
  permissionMode: 'ask' | 'full' | 'plan' | string
  permissionLabel: string
  sandboxMode: 'auto' | 'hyperlight' | 'monty' | string
  sandboxEffective: string
  running: boolean
}

export interface ReasoningOptions {
  protocol: string
  protocols: string[]
  efforts: string[]
  effort: string
  budget: number
  minimumBudget: number
  maximumBudget: number
  label: string
}

export interface PluginStatusInfo {
  id: string
  name: string
  packageId: string
  version: string
  enabled: boolean
  error?: string | null
}

export interface PluginPackageInfo {
  id: string
  version: string
  description: string
  authors: string
  downloads: number
}

export interface PluginAuthInfo {
  id: string
  name: string
}

export interface HookInfo {
  name: string
  enabled: boolean
}

export interface PluginsSnapshot {
  plugins: PluginStatusInfo[]
  providers: Array<{ providerId: string }>
  authentication: PluginAuthInfo[]
  hooks: HookInfo[]
}

export interface InstalledPluginInfo {
  packageId: string
  version: string
  directory: string
  enabled: boolean
}
