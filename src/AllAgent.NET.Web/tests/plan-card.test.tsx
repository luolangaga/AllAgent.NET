import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PlanDialog, PlanPanel } from '../src/components/PlanCard'
import type { PlanDocument, PlanProposalRequest } from '../src/types'

const plan: PlanDocument = {
  id: 'plan-1',
  title: 'Add plan mode',
  markdown: '# Plan\n\n1. Read the code\n2. Implement the change',
  status: 'approved',
  createdAt: '2026-10-19T00:00:00Z',
  decidedAt: '2026-10-19T00:01:00Z',
}

const request: PlanProposalRequest = {
  id: 'plan-1',
  conversationId: 'c1',
  title: 'Add plan mode',
  markdown: '# Plan\n\n1. Read the code',
  status: 'proposed',
  createdAt: '2026-10-19T00:00:00Z',
}

let root: Root | null = null
let element: HTMLDivElement | null = null

beforeEach(() => {
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  Object.assign(window, { matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }) })
  element = document.createElement('div')
  document.body.appendChild(element)
  root = createRoot(element)
})

afterEach(async () => {
  if (root) await act(async () => { root?.unmount() })
  root = null
  element?.remove()
  element = null
  vi.unstubAllGlobals()
})

describe('plan card', () => {
  it('renders the persisted plan as sanitized HTML and expands the Markdown', async () => {
    await act(async () => { root?.render(<PlanPanel plan={plan} />) })
    expect(element?.textContent).toContain('Add plan mode')
    expect(element?.textContent).toContain('已批准')
    const header = element?.querySelector('button.plan-panel-head')
    await act(async () => { header?.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
    expect(element?.querySelector('.plan-panel-body h1')?.textContent).toBe('Plan')
    expect(element?.querySelector('.plan-panel-body')?.textContent).toContain('Implement the change')
  })

  it('strips scripts from Markdown content', async () => {
    await act(async () => {
      root?.render(<PlanPanel plan={{ ...plan, markdown: '# Plan\n\n<script>window.__planXss = true</script>\n\nSafe text' }} />)
    })
    const header = element?.querySelector('button.plan-panel-head')
    await act(async () => { header?.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
    expect(element?.querySelector('script')).toBeNull()
    expect(element?.textContent).toContain('Safe text')
  })

  it('sends the approval decision from the blocking dialog', async () => {
    const respond = vi.fn(async () => {})
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
    await act(async () => {
      root?.render(<PlanDialog onRespond={respond} request={request} />)
      await new Promise(resolve => setTimeout(resolve, 30))
    })
    expect(document.body.textContent).toContain('计划审批 · Add plan mode')
    const approve = Array.from(document.querySelectorAll('button')).find(button => button.textContent?.includes('批准并自动执行'))
    expect(approve).toBeTruthy()
    await act(async () => {
      approve?.dispatchEvent(new MouseEvent('click', { bubbles: true }))
      await new Promise(resolve => setTimeout(resolve, 10))
    })
    expect(respond).toHaveBeenCalledWith('plan-1', true)
  })
})
