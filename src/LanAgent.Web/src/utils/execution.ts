import type { Message, ToolCall } from '@/types'

export function parseObject(text?: string): Record<string, any> {
  try { const value = JSON.parse(text ?? '{}'); return value && typeof value === 'object' ? value : {} }
  catch { return {} }
}

export function restoreMessages(messages: Message[]): Message[] {
  const result: Message[] = []
  const calls = new Map<string, ToolCall>()
  for (const message of messages) {
    if (message.isHostActivity) {
      const trace = parseObject(message.hostToolTraceJson)
      message.toolCalls = Array.isArray(trace) ? trace.map(item => ({ id: item.id, name: item.toolName, args: item.argsJson, output: item.resultJson, status: parseObject(item.resultJson).ok === false ? 'error' as const : 'done' as const })) : []
      result.push(message)
      continue
    }
    if (message.role === 'tool') {
      const call = calls.get(message.toolCallId ?? '')
      if (call) {
        call.output = message.content
        call.status = parseObject(message.content).ok === false ? 'error' : 'done'
        const trace = parseObject(message.hostToolTraceJson)
        if (Array.isArray(trace)) {
          const owner = result.find(m => m.toolCalls?.includes(call))
          for (const item of trace) owner?.toolCalls?.push({ id: item.id, parentId: call.id, name: item.toolName, args: item.argsJson, output: item.resultJson, status: parseObject(item.resultJson).ok === false ? 'error' : 'done' })
        }
      }
      continue
    }
    if (message.role === 'assistant') {
      message.toolCalls = []
      const saved = parseObject(message.toolCallsJson)
      if (Array.isArray(saved)) for (const item of saved) {
        const call: ToolCall = { id: item.id, name: item.function?.name ?? item.name, args: item.function?.arguments ?? item.arguments ?? '{}', status: 'error', output: '执行记录不完整，可能已中断' }
        calls.set(call.id, call); message.toolCalls.push(call)
      }
    }
    result.push(message)
  }
  return result
}
