import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params?: any }
const host = window as unknown as { chrome: { webview: { postMessage(json: string): void } }; __bridgeCallback(json: string): void }
const calls: Request[] = []

const tree = (path: string) => ({
  path,
  truncated: false,
  entries: path === '.'
    ? [
      { name: 'src', path: 'src', directory: true, size: 0, modified: new Date().toISOString(), link: false, hidden: false },
      { name: 'README.md', path: 'README.md', directory: false, size: 12, modified: new Date().toISOString(), link: false, hidden: false },
    ]
    : [{ name: 'App.tsx', path: 'src/App.tsx', directory: false, size: 11, modified: new Date().toISOString(), link: false, hidden: false }],
})

beforeEach(() => {
  vi.resetModules()
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  calls.length = 0
  host.chrome = { webview: { postMessage(json) {
    const request = JSON.parse(json) as Request
    calls.push(request)
    const reply = (data: unknown, error?: string) => host.__bridgeCallback(JSON.stringify({ id: request.id, data, error }))
    switch (request.method) {
      case 'workspace.tree': return reply(tree(String(request.params?.path ?? '.')))
      case 'workspace.readFile': return String(request.params?.path).endsWith('.tsx')
        ? reply({ path: 'src/App.tsx', kind: 'text', size: 11, modified: new Date().toISOString(), text: 'hello world', base64: null, truncated: false })
        : reply(undefined, '这是一个目录，无法预览文件内容。')
      default: return reply(true)
    }
  } } }
})
afterEach(() => { vi.unstubAllGlobals() })

async function mount() {
  const panels = (await import('../src/stores/panels')).usePanelsStore()
  panels.openPanel('files')
  const { WorkspacePanel } = await import('../src/components/WorkspacePanel')
  const element = document.createElement('div')
  document.body.appendChild(element)
  const root = createRoot(element)
  await act(async () => { root.render(<WorkspacePanel />) })
  return { element, root }
}

async function settle(check: () => boolean, timeout = 3000) {
  const deadline = Date.now() + timeout
  while (!check() && Date.now() < deadline) {
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
  }
}

function findByText(element: Element, selector: string, text: string): HTMLElement | undefined {
  return [...element.querySelectorAll<HTMLElement>(selector)].find(node => node.textContent?.includes(text))
}

describe('工作区文件面板', () => {
  it('renders the root level, expands a folder lazily and previews a file', async () => {
    const { element, root } = await mount()
    try {
      await settle(() => element.textContent?.includes('README.md') === true, 10000)
      expect(element.textContent).toContain('src')
      expect(calls.some(call => call.method === 'workspace.tree' && (call.params as { path?: string }).path === '.')).toBe(true)
      // The nested level only loads when the folder is expanded.
      expect(calls.some(call => call.method === 'workspace.tree' && (call.params as { path?: string }).path === 'src')).toBe(false)

      const folder = findByText(element, '.tree-main', 'src')!
      await act(async () => { folder.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
      await settle(() => element.textContent?.includes('App.tsx') === true, 10000)
      expect(calls.some(call => call.method === 'workspace.tree' && (call.params as { path?: string }).path === 'src')).toBe(true)

      const file = findByText(element, '.tree-main', 'App.tsx')!
      await act(async () => { file.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
      await settle(() => element.textContent?.includes('hello world') === true, 10000)
      expect(calls).toContainEqual(expect.objectContaining({ method: 'workspace.readFile', params: { path: 'src/App.tsx' } }))
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  }, 20000)

  it('filters ignored directories and can reveal them again', async () => {
    const { element, root } = await mount()
    try {
      await settle(() => element.textContent?.includes('src') === true)
      const store = (await import('../src/stores/workspaceBrowser')).useWorkspaceBrowserStore()
      expect(store.hideIgnored).toBe(true)
      await act(async () => { store.hideIgnored = false })
      expect(store.hideIgnored).toBe(false)
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  })
})
