import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { TodoPanel } from '../src/components/TodoPanel'
import { MessageBubble } from '../src/components/MessageBubble'
import { ToolTrace } from '../src/components/ToolTrace'
import type { Message } from '../src/types'

describe('TODO panel', () => {
  const todos = [
    { id: '1', content: '<script>unsafe</script>', status: 'pending' as const },
    { id: '2', content: '实现', status: 'in_progress' as const },
    { id: '3', content: '验证', status: 'completed' as const },
  ]

  it('shows a collapsed progress bar above the transcript by default', () => {
    const html = renderToStaticMarkup(<TodoPanel disabled onChange={async () => {}} report={() => {}} todos={todos} />)
    expect(html).toContain('TODO')
    expect(html).toContain('1/3')
    expect(html).toContain('实现')
    // The body stays mounted so the height animation can run, but the collapsed
    // panel is marked closed and its controls are inert.
    expect(html).toContain('aria-expanded="false"')
    expect(html).toContain('aria-hidden="true"')
    expect(html).toContain('inert')
  })

  it('expands into the editable checklist with escaped task text', () => {
    const html = renderToStaticMarkup(<TodoPanel defaultOpen disabled onChange={async () => {}} report={() => {}} todos={todos} />)
    expect(html).toContain('1/3')
    expect(html).toContain('进行中')
    expect(html).toContain('todo-done')
    expect(html).toContain('&lt;script&gt;')
    expect(html).not.toContain('<script>unsafe')
    expect(html).toContain('disabled')
  })

  it('shows parallel work items as active instead of demoting them', () => {
    const parallel = [...todos, { id: '4', content: '并行验证', status: 'in_progress' as const }]
    const html = renderToStaticMarkup(<TodoPanel disabled onChange={async () => {}} report={() => {}} todos={parallel} />)
    expect(html).toContain('实现')
    expect(html).toContain('+1')
  })
})

describe('message rendering', () => {
  const base: Message = { id: 'm1', role: 'assistant', content: '', createdAt: new Date().toISOString(), status: 'done' }

  it('renders reasoning, tool triggers and rejected calls', () => {
    const html = renderToStaticMarkup(<MessageBubble message={{
      ...base,
      content: '**完成**',
      reasoningContent: '先读取文件，再验证结果。',
      toolCalls: [
        { id: 't1', name: 'file_read', args: '{"path":"a.txt"}', status: 'done', output: '{"ok":true,"data":{"content":"…"}}' },
        { id: 't2', name: 'file_edit', args: '{"path":"a.txt"}', status: 'error', rejected: true, note: '未执行 file_edit: 只读规划模式' },
      ],
    }} />)
    expect(html).toContain('思考过程')
    expect(html).toContain('file_read')
    expect(html).toContain('已拒绝')
    expect(html).toContain('<strong>完成</strong>')
    // Collapsed traces stay unmounted; heavy bodies only exist once a trace is opened.
    expect(html).not.toContain('未执行 file_edit: 只读规划模式')
  })

  it('renders streamed commentary before and after its tool activity', () => {
    const html = renderToStaticMarkup(<MessageBubble message={{
      ...base, content: '准备读取。读取完成。', status: 'streaming',
      toolCalls: [{ id: 't', name: 'file_read', args: '{}', status: 'running' }],
      parts: [{ type: 'text', text: '准备读取。' }, { type: 'tool', id: 't' }, { type: 'text', text: '读取完成。' }],
    }} />)
    const before = html.indexOf('<p>准备读取。')
    const tool = html.indexOf('tool-trace')
    const after = html.indexOf('<p>读取完成。')
    expect(before).toBeGreaterThan(0)
    expect(tool).toBeGreaterThan(before)
    expect(after).toBeGreaterThan(tool)
    expect(html).toContain('正在执行 file_read')
  })

  it('does not show an unlimited iteration budget as a division by zero', () => {
    const html = renderToStaticMarkup(<MessageBubble message={{ ...base, iteration: { current: 3, maximum: 0, toolCalls: 2 } }} />)
    expect(html).toContain('3')
    expect(html).not.toContain('/0')
  })

  it('distinguishes a protocol rejection from a user declining approval', () => {
    const html = renderToStaticMarkup(<ToolTrace calls={[{ id: 'bad', name: 'ask_user', args: '{}',
      status: 'error', rejected: true, rejectionKind: 'protocol' }]} />)
    expect(html).toContain('调用格式错误 · 未执行')
    expect(html).not.toContain('已拒绝')
  })

  it('renders trace bodies when opened, including rejection notes', () => {
    const html = renderToStaticMarkup(<ToolTrace defaultOpen={['t2']} calls={[
      { id: 't1', name: 'file_read', args: '{"path":"a.txt"}', status: 'done', output: '{"ok":true,"data":{"content":"…"}}' },
      { id: 't2', name: 'file_edit', args: '{"path":"a.txt"}', status: 'error', rejected: true, note: '未执行 file_edit: 只读规划模式' },
    ]} />)
    expect(html).toContain('未执行 file_edit: 只读规划模式')
    expect(html).toContain('已拒绝')
  })

  it('renders the learning card with escaped learned facts', () => {
    const html = renderToStaticMarkup(<ToolTrace defaultOpen={['learn']} calls={[{
      id: 'learn', name: 'memory_update', args: '{}', status: 'done',
      output: JSON.stringify({ ok: true, data: { summary: '已更新', learned: ['喜欢 .NET', '<script>unsafe</script>'] } }),
    }]} />)
    expect(html).toContain('学习本轮经验')
    expect(html).toContain('喜欢 .NET')
    expect(html).toContain('已更新')
    expect(html).toContain('&lt;script&gt;')
    expect(html).not.toContain('<script>unsafe')
  })

  it('renders fatal errors inline without killing the transcript', () => {
    const html = renderToStaticMarkup(<MessageBubble message={{ ...base, status: 'error', error: '模型连接失败' }} />)
    expect(html).toContain('执行中断')
    expect(html).toContain('模型连接失败')
    expect(html).toContain('执行失败')
  })
})
