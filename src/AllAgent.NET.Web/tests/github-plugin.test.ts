import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const pluginRoot = resolve(__dirname, '../../../plugins/AllAgent.NET.GitHub/web')
const html = readFileSync(resolve(pluginRoot, 'index.html'), 'utf8')
const script = readFileSync(resolve(pluginRoot, 'app.js'), 'utf8')
const issue = { title: '<img src=x onerror=alert(1)>', number: 7, repository_url: 'https://api.github.com/repos/octo/demo', state: 'open', body: '待修复问题', user: { login: 'octo' }, updated_at: '2026-10-19T00:00:00Z', html_url: 'https://github.com/octo/demo/issues/7' }
const settle = async () => { for (let i = 0; i < 15; i++) await Promise.resolve() }
let invoke: ReturnType<typeof vi.fn>

beforeEach(async () => {
  document.body.innerHTML = html.replace(/<script[\s\S]*?<\/script>/g, '')
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value() { this.setAttribute('open', '') } })
  Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value() { this.removeAttribute('open') } })
  invoke = vi.fn(async (method: string, p?: any): Promise<any> => {
    if (method === 'status') return { loggedIn: true, account: 'octo' }
    if (method === 'operations') return []
    if (method === 'workspaces') return [{ path: 'C:\\Projects\\demo', name: 'demo' }]
    if (method === 'list') return { items: p.kind === 'repositories' ? [{ full_name: 'octo/demo' }] : [p.kind === 'pulls' ? { ...issue, pull_request: {} } : issue], page: 1, hasMore: false, total: 1 }
    if (method === 'detail') return { item: { ...issue, head: { label: 'octo:fix' }, base: { label: 'octo:main' } }, files: [{ filename: 'demo.cs', patch: '+ fix' }], comments: [], reviews: [], truncated: false }
    if (method === 'start') return { id: 'operation-1', state: 'done', result: { conversationId: 'conversation-1', workspace: 'C:\\Projects\\demo' } }
    return true
  })
  ;(window as any).AllAgentPlugin = { connect: () => ({ ready: Promise.resolve(), invoke, context: { workspace: 'C:\\Projects\\demo' }, openExternal: vi.fn() }) }
  // Execute the shipped, framework-free plugin page with a fake host SDK.
  new Function(script)()
  await settle()
})
afterEach(() => { document.body.replaceChildren(); vi.restoreAllMocks() })

describe('GitHub plugin GUI', () => {
  it('renders remote titles as text and opens the issue workflow with the current workspace', async () => {
    expect(document.querySelector('#list img')).toBeNull()
    expect(document.querySelector('#list')?.textContent).toContain('<img src=x onerror=alert(1)>')
    ;(document.querySelector('.list-item') as HTMLButtonElement).click()
    await settle()
    ;(document.querySelector('#detail .primary') as HTMLButtonElement).click()
    await settle()
    expect(document.querySelector('#task-dialog')?.hasAttribute('open')).toBe(true)
    expect(document.querySelector('#workflow')).toBeNull()
    expect(document.querySelector('#task-dialog')?.textContent).not.toContain('提示词')
    expect(invoke.mock.calls.some(([method]) => method === 'workflows')).toBe(false)
    expect((document.querySelector('#workspace-path') as HTMLInputElement).value).toBe('C:\\Projects\\demo')
  })

  it('starts a PR review with clone selection and user instructions, then opens the resulting workspace session', async () => {
    ;(document.querySelector('[data-kind=pulls]') as HTMLButtonElement).click()
    await settle()
    ;(document.querySelector('.list-item') as HTMLButtonElement).click()
    await settle()
    ;(document.querySelector('#detail .primary') as HTMLButtonElement).click()
    await settle()
    expect(document.querySelector('#workflow')).toBeNull()
    const clone = document.querySelector('[name=mode][value=clone]') as HTMLInputElement
    clone.checked = true; clone.dispatchEvent(new Event('change'))
    expect((document.querySelector('#workspace-path') as HTMLInputElement).value).toBe('')
    ;(document.querySelector('#workspace-path') as HTMLInputElement).value = 'C:\\Projects\\review'
    ;(document.querySelector('#instructions') as HTMLTextAreaElement).value = '检查并发'
    document.querySelector('#task-form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await settle()
    expect(invoke).toHaveBeenCalledWith('start', expect.objectContaining({ kind: 'review', cloneRepository: true, instructions: '检查并发', repository: 'octo/demo', number: 7, workspace: 'C:\\Projects\\review' }))
    ;(document.querySelector('.operation-link') as HTMLButtonElement).click()
    await settle()
    expect(invoke).toHaveBeenCalledWith('host.openConversation', { id: 'conversation-1', workspace: 'C:\\Projects\\demo' })
  })

  it('clears pasted credentials immediately and surfaces login failures', async () => {
    invoke.mockImplementation(async (method: string) => { if (method === 'login') throw new Error('凭据已过期'); return [] })
    ;(document.querySelector('#token') as HTMLInputElement).value = 'secret-token'
    document.querySelector('#login-form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await settle()
    expect((document.querySelector('#token') as HTMLInputElement).value).toBe('')
    expect(document.querySelector('#error')?.textContent).toBe('凭据已过期')
  })
})
