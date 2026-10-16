import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { Accordion } from '../src/components/ui/b-accordion'
import { Execution } from '../src/views/Chat'
import { restoreMessages } from '../src/utils/execution'
import type { ToolCall } from '../src/types'

const card = (call: ToolCall) => renderToStaticMarkup(<Accordion defaultValue={[call.id]}><Execution call={call} /></Accordion>)
describe('learning tool cards', () => {
  it('shows learned facts with a readable label and escaped content', () => {
    const html = card({ id: 'learn', name: 'memory_update', args: '{}', status: 'done', output: JSON.stringify({ ok: true, data: { summary: '已更新', learned: ['喜欢 .NET', '<script>unsafe</script>'] } }) })
    expect(html).toContain('学习本轮经验'); expect(html).toContain('喜欢 .NET'); expect(html).toContain('已更新')
    expect(html).toContain('&lt;script&gt;'); expect(html).not.toContain('<script>unsafe')
  })
  it('shows no-change, running and error states', () => {
    expect(card({ id: 'a', name: 'memory_update', args: '{}', status: 'done', output: '{"ok":true,"data":{"summary":"本轮无新增","learned":[]}}' })).toContain('本轮无新增')
    expect(card({ id: 'a', name: 'memory_update', args: '{}', status: 'running' })).toContain('整理中')
    expect(card({ id: 'a', name: 'memory_update', args: '{}', status: 'error', output: '{"ok":false,"error":"原文件保留"}' })).toContain('原文件保留')
  })
  it('restores persisted host-only learning cards without protocol tool calls', () => {
    const messages = restoreMessages([{ id: 'a', role: 'assistant', content: '', createdAt: '', isHostActivity: true, hostToolTraceJson: JSON.stringify([{ id: 'learn', toolName: 'memory_update', argsJson: '{}', resultJson: '{"ok":true,"data":{"summary":"已更新","learned":["GitHub: fixture-user"]}}' }]) }])
    expect(messages).toHaveLength(1)
    expect(messages[0].toolCalls?.[0]).toMatchObject({ name: 'memory_update', status: 'done' })
    expect(card(messages[0].toolCalls![0])).toContain('GitHub: fixture-user')
  })
})
