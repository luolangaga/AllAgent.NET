import type { Message, ToolCall } from '@/types'

export function parseObject(text?: string): Record<string, any> {
  try {
    const value: unknown = JSON.parse(text ?? '{}')
    return value && typeof value === 'object' ? (value as Record<string, any>) : {}
  } catch {
    return {}
  }
}

export function toolFailed(result?: string): boolean {
  return parseObject(result).ok === false
}

export function parseArguments(args?: string): Record<string, any> {
  return parseObject(args)
}

/**
 * Rebuilds the conversation view from persisted messages:
 * assistant rows get their planned tool calls, tool rows attach results (and
 * nested host traces) to the originating call, and raw protocol rows are
 * folded away so the transcript stays readable.
 */
export function restoreMessages(messages: Message[]): Message[] {
  const result: Message[] = []
  const calls = new Map<string, ToolCall>()
  const owners = new Map<string, Message>()
  let iteration = 0
  function traces(text: string | undefined, parentId?: string): ToolCall[] {
    const saved = parseObject(text)
    if (!Array.isArray(saved)) return []
    return saved.filter(item => item && typeof item.id === 'string' && typeof item.toolName === 'string').map(item => ({
      id: item.id, parentId, name: item.toolName, args: typeof item.argsJson === 'string' ? item.argsJson : '{}',
      output: item.resultJson, status: toolFailed(item.resultJson) ? 'error' : 'done',
    }))
  }
  for (const original of messages) {
    // Restoration is a projection: never modify cached/mock/backend records.
    const message: Message = { ...original, parts: original.parts?.map(part => ({ ...part })) }
    if (message.role === 'user') iteration = 0
    if (message.isInterrupted) {
      message.status = 'error'
      message.error = '上次响应已中断，可输入“继续”。'
    }
    if (message.isHostActivity) {
      message.toolCalls = traces(message.hostToolTraceJson)
      result.push(message)
      continue
    }
    if (message.role === 'tool') {
      let call = calls.get(message.toolCallId ?? '')
      // Imported/truncated histories can contain a result without its planned
      // call. Keep the evidence visible instead of silently discarding it.
      if (!call) {
        call = { id: message.toolCallId || message.id, name: message.toolName || 'tool', args: '{}', status: 'done' }
        const owner: Message = { ...message, role: 'assistant', content: '', toolCalls: [call] }
        result.push(owner)
        calls.set(call.id, call)
        owners.set(call.id, owner)
      }
      if (call) {
        call.output = message.content
        call.status = toolFailed(message.content) ? 'error' : 'done'
        call.note = undefined
        const error = parseObject(message.content).error
        if (typeof error === 'string' && error.startsWith('Not executed:')) { call.rejected = true; call.rejectionKind = 'protocol'; call.note = error }
        const owner = owners.get(call.id)
        let offset = 1
        for (const child of traces(message.hostToolTraceJson, call.id)) {
          if (owner?.toolCalls?.some(existing => existing.id === child.id)) continue
          owner?.toolCalls?.push(child)
          const index = owner?.parts?.findIndex(part => part.type === 'tool' && part.id === call!.id) ?? -1
          if (index >= 0) owner?.parts?.splice(index + offset++, 0, { type: 'tool', id: child.id })
        }
      }
      continue
    }
    if (message.role === 'assistant') {
      iteration++
      message.toolCalls = original.toolCalls?.map(call => ({ ...call, outputLines: call.outputLines?.slice() })) ?? []
      for (const call of message.toolCalls) { calls.set(call.id, call); owners.set(call.id, message) }
      const saved = parseObject(message.toolCallsJson)
      if (Array.isArray(saved)) {
        for (const item of saved as Record<string, any>[]) {
          if (!item || typeof item.id !== 'string' || message.toolCalls.some(call => call.id === item.id)) continue
          const name = item.function?.name ?? item.name
          if (typeof name !== 'string') continue
          const call: ToolCall = {
            id: item.id,
            name,
            args: typeof (item.function?.arguments ?? item.arguments) === 'string' ? (item.function?.arguments ?? item.arguments) : '{}',
            status: 'error',
            output: '执行记录不完整，可能已中断',
            note: '可能已被取消'
          }
          calls.set(call.id, call)
          owners.set(call.id, message)
          message.toolCalls.push(call)
        }
      }
      if (!message.parts && message.reasoningContent?.trim()) {
        message.parts = [{ type: 'reasoning', text: message.reasoningContent,
          iteration: message.iteration?.current ?? iteration, status: message.isInterrupted ? 'interrupted' : 'done' },
          ...(message.content ? [{ type: 'text' as const, text: message.content }] : []),
          ...message.toolCalls.map(call => ({ type: 'tool' as const, id: call.id }))]
      }
    }
    result.push(message)
  }
  return result
}
