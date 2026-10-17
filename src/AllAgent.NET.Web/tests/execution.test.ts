import { describe, expect, it } from 'vitest'
import { parseObject, restoreMessages, toolFailed } from '../src/utils/execution'

describe('history restoration', () => {
  it('places each historical iteration reasoning before its text and nested tool activity', () => {
    const restored = restoreMessages([
      { id: 'u', role: 'user', content: 'go', createdAt: '' },
      { id: 'a', role: 'assistant', content: 'first', reasoningContent: 'thought 1', createdAt: '', toolCallsJson: '[{"id":"p","function":{"name":"execute_code","arguments":"{}"}}]' },
      { id: 't', role: 'tool', toolCallId: 'p', content: '{"ok":true}', createdAt: '', hostToolTraceJson: '[{"id":"c","toolName":"file_read","argsJson":"{}","resultJson":"{}"}]' },
      { id: 'b', role: 'assistant', content: 'second', reasoningContent: 'thought 2', createdAt: '' },
    ])
    expect(restored[1].parts?.[0]).toMatchObject({ type: 'reasoning', iteration: 1, status: 'done' })
    expect(restored[1].parts?.map(part => part.type)).toEqual(['reasoning', 'text', 'tool', 'tool'])
    expect(restored[2].parts?.[0]).toMatchObject({ type: 'reasoning', iteration: 2, status: 'done' })
  })
  it('does not mutate cached records, clears interruption hints on success and tolerates malformed trace items', () => {
    const history = [
      { id: 'a', role: 'assistant' as const, content: '', createdAt: '', toolCallsJson: '[null,{"id":"p","function":{"name":"execute_code","arguments":"{}"}}]' },
      { id: 'b', role: 'tool' as const, toolCallId: 'p', content: '{"ok":true}', createdAt: '', hostToolTraceJson: '[null,{}, {"id":"c","toolName":"file_read","argsJson":"{}","resultJson":"{\\"ok\\":true}"}]' },
    ]
    const before = JSON.stringify(history)
    const restored = restoreMessages(history)
    expect(JSON.stringify(history)).toBe(before)
    expect(restored[0].toolCalls).toHaveLength(2)
    expect(restored[0].toolCalls?.[0].note).toBeUndefined()
    expect(restored[0].toolCalls?.[0].status).toBe('done')
  })

  it('keeps orphan results and precomputed preview traces visible', () => {
    const restored = restoreMessages([
      { id: 't', role: 'tool', toolCallId: 'orphan', toolName: 'file_read', content: '{"ok":false}', createdAt: '' },
      { id: 'a', role: 'assistant', content: '', createdAt: '', toolCalls: [{ id: 'preview', name: 'file_edit', args: '{}', status: 'done' }] },
      { id: 'b', role: 'assistant', content: 'partial', createdAt: '', isInterrupted: true },
    ])
    expect(restored[0].toolCalls?.[0]).toMatchObject({ id: 'orphan', status: 'error' })
    expect(restored[1].toolCalls?.[0].id).toBe('preview')
    expect(restored[2]).toMatchObject({ status: 'error', content: 'partial' })
  })
  it('folds tool rows into their originating assistant calls with traces', () => {
    const messages = restoreMessages([
      { id: 'a', role: 'assistant', content: '', createdAt: '', toolCallsJson: '[{"id":"parent","function":{"name":"execute_code","arguments":"{}"}}]' },
      { id: 'b', role: 'tool', toolCallId: 'parent', content: '{"ok":true}', createdAt: '', hostToolTraceJson: JSON.stringify([{ id: 'child', toolName: 'file_read', argsJson: '{}', resultJson: '{"ok":true}' }]) },
    ])
    expect(messages).toHaveLength(1)
    expect(messages[0].toolCalls?.[0].status).toBe('done')
    expect(messages[0].toolCalls?.[1].parentId).toBe('parent')
    expect(messages[0].toolCalls?.[1].name).toBe('file_read')
  })

  it('restores failure states and keeps malformed history readable', () => {
    const messages = restoreMessages([
      { id: 'a', role: 'assistant', content: '', createdAt: '', toolCallsJson: JSON.stringify([{ id: 'code', function: { name: 'execute_code', arguments: '{"code":"print(1)"}' } }]) },
      { id: 't', role: 'tool', toolCallId: 'code', content: '{"ok":false,"error":"failed"}', createdAt: '', hostToolTraceJson: JSON.stringify([{ id: 'file', toolName: 'file_edit', argsJson: '{"path":"a.txt"}', resultJson: '{"ok":true,"data":{"diff":{"added":1,"removed":1}}}' }]) },
    ])
    expect(messages).toHaveLength(1)
    expect(messages[0].toolCalls?.[0].status).toBe('error')
    expect(messages[0].toolCalls?.[1].output).toMatch(/diff/)
    expect(restoreMessages([{ id: 'a', role: 'assistant', content: 'answer', createdAt: '', toolCallsJson: 'broken' }])[0].toolCalls).toEqual([])
    const interrupted = restoreMessages([{ id: 'a', role: 'assistant', content: '', createdAt: '', toolCallsJson: '[{"id":"code","function":{"name":"execute_code","arguments":"{}"}}]' }])
    expect(interrupted[0].toolCalls?.[0].status).toBe('error')
  })

  it('restores host-only activity rows without protocol tool messages', () => {
    const messages = restoreMessages([{
      id: 'a', role: 'assistant', content: '', createdAt: '', isHostActivity: true,
      hostToolTraceJson: JSON.stringify([{ id: 'learn', toolName: 'memory_update', argsJson: '{}', resultJson: '{"ok":true,"data":{"summary":"已更新","learned":["GitHub: fixture-user"]}}' }]),
    }])
    expect(messages).toHaveLength(1)
    expect(messages[0].toolCalls?.[0]).toMatchObject({ name: 'memory_update', status: 'done' })
  })

  it('classifies tool payloads safely', () => {
    expect(parseObject('{"ok":true}').ok).toBe(true)
    expect(parseObject('[1,2]')).toEqual([1, 2])
    expect(parseObject('broken')).toEqual({})
    expect(toolFailed('{"ok":false}')).toBe(true)
    expect(toolFailed('{"ok":true}')).toBe(false)
    expect(toolFailed(undefined)).toBe(false)
  })
})
