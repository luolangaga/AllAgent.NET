// Shared TypeScript types for the frontend

export interface Conversation {
  id: string
  title: string
  modelId: string
  providerId: string
  createdAt: string
  updatedAt: string
  messages: Message[]
}

export interface Message {
  id: string
  role: 'user' | 'assistant' | 'tool' | 'system'
  content: string
  toolCallsJson?: string
  hostToolTraceJson?: string
  isHostActivity?: boolean
  toolCallId?: string
  toolName?: string
  reasoningContent?: string
  createdAt: string
  status?: 'streaming' | 'done' | 'error'
  toolCalls?: ToolCall[]
}

export interface ToolCall {
  id: string
  parentId?: string
  name: string
  args: string
  output?: string
  status: 'pending' | 'running' | 'done' | 'error'
  outputLines?: string[]
}

export interface Skill {
  id: string
  name: string
  description: string
  parametersJson: string
  commandTemplate?: string
  type: 'Builtin' | 'Shell' | 'Custom'
  isEnabled: boolean
  createdAt: string
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
  extraHeadersJson?: string
  thinkingEnabled: boolean
  thinkingBudget: number
}

export interface AppSettings {
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
  createdAt: string
}

export interface StreamChunk {
  type: 'text' | 'tool_call' | 'done' | 'error'
  delta?: string
  toolCall?: ToolCall
  error?: string
}
