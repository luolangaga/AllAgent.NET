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
export interface NoticePart {
  type: 'notice'
  id: string
  text: string
  /** `retry` covers transient connection failures; `info` covers run bookkeeping. */
  kind: 'info' | 'retry'
  status: 'pending' | 'settled' | 'failed'
  /** Consecutive retries folded into this single timeline line. */
  attempts: number
}
export type MessagePart = { type: 'text'; text: string } | { type: 'tool'; id: string } | ReasoningPart | NoticePart

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
  toolCalls?: ToolCall[]
  /** Ordered live output: text stays at the point where it was emitted. */
  parts?: MessagePart[]
  iteration?: { current: number; maximum: number; toolCalls: number }
  /** Images attached to a user turn; mirrors the host's ImageAttachment record. */
  images?: ImageAttachment[] | null
}

/** How input submitted during a run is delivered: steer the current turn or wait for idle. */
export type InputDelivery = 'steer' | 'queue'

/** A prompt parked in the host inbox while a turn is already running. */
export interface PendingInput {
  id: string
  draft: string
  delivery: InputDelivery
  createdAt: string
}

/** Inline image carried by a user message. */
export interface ImageAttachment {
  name: string
  mediaType: string
  base64: string
}

/** Workspace-relative path offered by the composer's `@` picker. */
export interface WorkspaceFileEntry {
  path: string
  name: string
  isDirectory: boolean
}

export type ToolCallStatus = 'running' | 'done' | 'error' | 'cancelled'

/** Host-neutral card a tool attached to ToolResult.Card; both the collapsed row and the body use it. */
export interface AgentToolCardField {
  label: string
  value: string
  tone?: 'ok' | 'error' | 'add' | 'del' | 'muted' | string | null
}

export interface AgentToolCard {
  badge?: string | null
  title?: string | null
  summary?: string | null
  body?: 'text' | 'code' | 'paths' | 'search' | 'json' | string | null
  text?: string | null
  paths?: string[] | null
  fields?: AgentToolCardField[] | null
}

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
  modelContextWindows?: Record<string, number> | null
  protocol?: string
  reasoningProtocol?: string
  reasoningEffort?: string
  models?: string[] | null
  apiKeyEnvironmentVariable?: string | null
  authenticationProviderId?: string | null
  effectiveContextTokens?: number
  effectiveProtocol?: string
  effectiveMaxTokens?: number
}

export type Language = 'zh' | 'en'
/** Persisted preference; auto follows the operating system language. */
export type LanguageSetting = 'auto' | Language

export interface AppSettings {
  browserUseEnabled: boolean
  browserUseMode: 'builtin' | 'chromium'
  browserExecutablePath: string
  browserCdpUrl: string
  theme: 'light' | 'dark' | 'system'
  language: LanguageSetting
  /** Resolved by the host so auto follows the same system language in TUI and GUI. */
  languageResolved: Language
  defaultModel: string
  streamingEnabled: boolean
  defaultProviderId: string
  /** API config for AI commit messages; empty follows the model selected in the composer. */
  gitCommitConfigId: string
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

/** One contribution to the footer line under the composer (account quota, plugin text, …). */
export interface ModelFooterItem {
  label: string
  value: string
}

export interface ModelFooterSnapshot {
  items: ModelFooterItem[]
  /** The host is still fetching from a provider; poll again shortly instead of waiting a minute. */
  refreshing: boolean
}

/** Cache and context counters the host keeps per conversation (terminal sidebar shows the same). */
export interface ConversationUsage {
  contextUsedTokens: number | null
  contextEstimated: boolean
  cacheHitRate: number | null
  cacheInputTokens: number
  cachedInputTokens: number
  cacheWriteInputTokens: number
  cacheRequests: number
}

/** Lifetime per-model usage plus local-day buckets for the settings charts. */
export interface UsageDay {
  date: string
  inputTokens: number
  outputTokens: number
  requests: number
  requestsWithUsage: number
}

export interface UsageModel {
  providerId: string
  modelId: string
  inputTokens: number
  outputTokens: number
  requests: number
  requestsWithUsage: number
}

export interface UsageTotals {
  inputTokens: number
  outputTokens: number
  requests: number
  requestsWithUsage: number
  activeDays: number
}

export interface UsageStats {
  models: UsageModel[]
  days: UsageDay[]
  totals: UsageTotals
}

export interface ApprovalRequest {
  requestId: string
  name: string
  risk: string
  args: string
  source?: string | null
  requiresExplicitApproval?: boolean
}

export type PlanStatus = 'proposed' | 'approved' | 'rejected'

/** The latest plan stored with a conversation. Markdown is the single content source. */
export interface PlanDocument {
  id: string
  title: string
  markdown: string
  status: PlanStatus | string
  feedback?: string | null
  createdAt: string
  decidedAt?: string | null
}

/** A plan waiting for the person to approve or reject it. */
export interface PlanProposalRequest {
  id: string
  conversationId?: string
  title: string
  markdown: string
  status: PlanStatus | string
  createdAt: string
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

/** Auto-update state reported by the host. */
export interface UpdateStatus {
  currentVersion: string
  latestVersion?: string | null
  updateAvailable: boolean
  source: string
  installKind: string
  architecture: string
  releaseUrl?: string | null
  releaseNotes?: string | null
  error?: string | null
  lastResult?: string | null
  lastError?: string | null
  checkedAt: string
  fromCache: boolean
  logPath?: string | null
}

export interface UpdateSettings {
  source: string
  channel: string
  autoCheck: boolean
  autoInstall: boolean
  requireSignature: boolean
  requireChecksum: boolean
}

export interface UpdateInvokeResult {
  started: boolean
  message: string
  version?: string | null
  logPath?: string | null
}

/** LAN listening and pairing state. Local requests never require the password. */
export interface WebUiNetworkInfo {
  lan: boolean
  port: number
  url: string
  localUrl: string
  urls: string[]
  authenticationRequired: boolean
  passwordConfigured: boolean
  accessUrl?: string | null
  qrDataUrl?: string | null
}

export interface WorkspaceEntry {
  workspace: string
  name: string
  dataDirectory: string
  sessionCount: number
  isCurrent: boolean
  runningCount?: number
}

/** A conversation with a run in flight, in any workspace the host has open. */
export interface RunningConversation {
  conversationId: string
  title: string
  workspace: string
  workspaceName: string
  dataDirectory: string
  state: 'running' | 'awaiting-approval' | 'awaiting-answer' | 'awaiting-plan' | string
}

export interface SessionControlState {
  permissionMode: 'restricted' | 'ask' | 'full' | 'plan' | 'simple' | string
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

/** Effective context window for one model, plus the manual override if one is recorded. */
export interface ModelContextInfo {
  model: string
  capacity: number
  manual: number | null
  defaultWindow: number
}

export interface PluginStatusInfo {
  id: string
  name: string
  packageId: string
  version: string
  enabled: boolean
  error?: string | null
  tools?: string[] | null
  commands?: string[] | null
  /** Web extension summary when the package ships a `web` block. */
  web?: PluginWebStatusInfo | null
}

export interface PluginWebStatusInfo {
  webEnabled: boolean
  pages: number
  panels: number
  toolCards: number
  capabilities: string[]
  error?: string | null
}

export interface PluginPackageInfo {
  id: string
  version: string
  description: string
  authors: string
  downloads: number
}

export interface PluginSearchResult {
  totalHits: number
  packages: PluginPackageInfo[]
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

// PowerShell 能力商店: the shared module directory the host import-validated with its bundled SDK.
export interface PowerShellModuleInfo {
  name: string
  version: string
  commandCount: number
  /** Import failure reported by the worker; a module with an error is not a capability. */
  error?: string | null
}

export interface PowerShellStoreStatus {
  workerAvailable: boolean
  workerPath: string
  root: string
  modules: PowerShellModuleInfo[]
}

/** One PowerShell Gallery match; installing uses the exact name and version. */
export interface PowerShellPackageInfo {
  name: string
  version: string
  author: string
  description: string
}

export interface PowerShellCommandPage {
  name: string
  version: string
  total: number
  commands: string[]
  error?: string | null
}

// Account login state from the terminal /login list (OAuth providers, paste-in keys, API presets).
export interface AuthProviderInfo {
  id: string
  name: string
  kind: 'oauth' | 'interactive' | 'apikey' | string
  loggedIn: boolean
  account: string
  message: string
  modelConnectionAvailable: boolean
  inputPrompt?: string | null
  providerId?: string | null
  configId?: string | null
  pending?: boolean
}

// Tool catalog entry with source attribution (host / plugin package / MCP server / skill / on-demand).
export interface ToolInfo {
  name: string
  description: string
  risk: 'read' | 'write' | 'execute' | string
  parametersJson: string
  source: 'host' | 'plugin' | 'mcp' | 'skill' | string
  owner: string
}

export interface ToolsSnapshot {
  tools: ToolInfo[]
  errors: string[]
}

// Web command catalog projected from the terminal's slash commands, plugin commands and skills.
export interface CommandInfo {
  text: string
  description: string
  usage?: string
  advanced?: boolean
  isSkill?: boolean
  aliases?: string
  terminalOnly?: boolean
  target?: string
}

// Expansion of a user-invocable skill: the instructions wrapped around the task arguments,
// plus any images referenced by those arguments. Only the prompt goes to the model — the
// transcript keeps the original slash command.
export interface SkillInvokeResult {
  text: string
  images: ImageAttachment[]
}

// ── Plugin web extensions ─────────────────────────────────────────────────────
// Declarative contributions plus the sandboxed frame payload the host serves from its own
// loopback origin. Entries are paths relative to the plugin install directory (inside web/).

export interface PluginWebPage {
  id: string
  title: string
  entry: string
  icon?: string | null
  order?: number
}

export interface PluginWebPanel {
  id: string
  title: string
  entry: string
  icon?: string | null
  order?: number
}

export interface PluginWebToolCard {
  tool: string
  entry: string
}

export interface PluginWebCommand {
  id: string
  title: string
  page?: string | null
  panel?: string | null
}

export interface PluginWebSettingField {
  key: string
  label: string
  type?: string
  options?: string[] | null
  default?: string | null
  description?: string | null
  placeholder?: string | null
}

export interface PluginWebSettingsSection {
  id: string
  title: string
  fields?: PluginWebSettingField[] | null
  /** Optional plugin-rendered frame for status and actions, shown below the native fields. */
  entry?: string | null
}

export interface PluginSettingsSnapshot {
  packageId: string
  values: Record<string, string>
}

export interface PluginWebComposerAction {
  id: string
  title: string
  page?: string | null
  icon?: string | null
}

export interface PluginWebStatusItem {
  id: string
  label: string
}

export interface PluginWebContributions {
  pages?: PluginWebPage[] | null
  panels?: PluginWebPanel[] | null
  toolCards?: PluginWebToolCard[] | null
  commands?: PluginWebCommand[] | null
  settings?: PluginWebSettingsSection[] | null
  composerActions?: PluginWebComposerAction[] | null
  statusBar?: PluginWebStatusItem[] | null
}

export interface PluginWebInfo {
  packageId: string
  pluginId: string
  name: string
  version: string
  enabled: boolean
  webEnabled: boolean
  active: boolean
  capabilities: string[]
  contributes: PluginWebContributions | null
  devUrl?: string | null
  error?: string | null
}

export interface WebContributionsSnapshot {
  sdkVersion: number
  assetBaseUrl?: string | null
  plugins: PluginWebInfo[]
}

// ── Workspace browser (read-only file bubble) ─────────────────────────────────
// workspace.tree lists one directory level; workspace.readFile classifies by content.

export interface WorkspaceTreeEntry {
  name: string
  /** Workspace-relative path with forward slashes. */
  path: string
  directory: boolean
  size: number
  modified: string
  /** Symlink/junction entry; never expanded by the tree. */
  link: boolean
  hidden: boolean
}

export interface WorkspaceTreeResult {
  path: string
  entries: WorkspaceTreeEntry[]
  truncated: boolean
}

export type WorkspaceFileKind = 'text' | 'image' | 'binary'

export interface WorkspaceFileContent {
  path: string
  kind: WorkspaceFileKind
  size: number
  modified: string
  text?: string | null
  base64?: string | null
  truncated: boolean
}

// ── Git panel ────────────────────────────────────────────────────────────────
// Mirrors the C# `git.*` DTOs; entries carry git's two status letters (index/worktree).

export interface GitFileEntry {
  path: string
  originalPath?: string | null
  index: string
  worktree: string
}

export interface GitSummary {
  available: boolean
  version: string
  repo: boolean
  root: string
  branch?: string | null
  detached: boolean
  upstream?: string | null
  ahead: number
  behind: number
  operation?: string | null
  staged: number
  unstaged: number
  untracked: number
  conflicted: number
}

export interface GitStatus {
  branch?: string | null
  detached: boolean
  upstream?: string | null
  ahead: number
  behind: number
  operation?: string | null
  staged: GitFileEntry[]
  unstaged: GitFileEntry[]
  untracked: GitFileEntry[]
  conflicted: GitFileEntry[]
}

export interface DiffLine {
  kind: 'context' | 'added' | 'removed'
  oldLine?: number | null
  newLine?: number | null
  text: string
}

export interface GitDiff {
  path: string
  commit?: string | null
  staged: boolean
  added: number
  removed: number
  truncated: boolean
  binary: boolean
  lines: DiffLine[]
  note?: string | null
}

export interface GitCommit {
  hash: string
  short: string
  author: string
  authorEmail: string
  date: string
  subject: string
  refs: string[]
  /** Parent hashes; feeds the commit graph lanes. */
  parents: string[]
}

export interface GitLog {
  commits: GitCommit[]
  more: boolean
}

export interface GitShowFile {
  path: string
  status: string
  originalPath?: string | null
}

export interface GitShow {
  commit: GitCommit
  body: string
  files: GitShowFile[]
}

export interface GitBranchInfo {
  name: string
  current: boolean
  upstream?: string | null
  date: string
  subject: string
}

export interface GitRemoteBranch {
  name: string
  date: string
  subject: string
}

export interface GitTagInfo {
  name: string
  date: string
}

export interface GitBranches {
  current?: string | null
  local: GitBranchInfo[]
  remote: GitRemoteBranch[]
  tags: GitTagInfo[]
}

export interface GitStash {
  index: number
  ref: string
  message: string
  date: string
}

export interface GitJob {
  id: string
  label: string
}

export interface GitRemoteInfo {
  name: string
  url: string
  pushUrl: string
}

export interface GitIdentity {
  name?: string | null
  email?: string | null
}

export interface GitInstallResult {
  ok: boolean
  message: string
}

// ── Integrated terminal ──────────────────────────────────────────────────────
// Host-side ConPTY sessions; output frames are base64 chunks with a per-session sequence.

export interface TerminalShell {
  id: string
  label: string
}

export interface TerminalSession {
  id: string
  title: string
  shellId: string
  shellLabel: string
  cwd: string
  cols: number
  rows: number
  alive: boolean
  exitCode?: number | null
  sequence: number
}

export interface TerminalAttach {
  session: TerminalSession
  replay: string
  sequence: number
}
