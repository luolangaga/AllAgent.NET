import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { TodoPanel } from '../src/components/TodoPanel'

describe('TODO panel', () => {
  it('shows all statuses and escapes task text', () => {
    const html = renderToStaticMarkup(<TodoPanel todos={[
      { id: '1', content: '<script>unsafe</script>', status: 'pending' },
      { id: '2', content: '实现', status: 'in_progress' },
      { id: '3', content: '验证', status: 'completed' }
    ]} disabled onChange={async () => {}} report={() => {}} />)
    expect(html).toContain('1/3'); expect(html).toContain('进行中'); expect(html).toContain('todo-completed')
    expect(html).toContain('&lt;script&gt;'); expect(html).not.toContain('<script>unsafe')
    expect(html).toContain('disabled=""')
  })
})
