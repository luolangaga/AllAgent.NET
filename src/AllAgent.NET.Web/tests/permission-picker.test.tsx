import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params?: any }
const host = window as unknown as { chrome: { webview: { postMessage(json: string): void } }; __bridgeCallback(json: string): void }

let permissionMode = 'restricted'
let running = true
const switches: string[] = []

const state = () => ({
  permissionMode,
  permissionLabel: permissionMode === 'plan' ? '只读规划' : '限制模式',
  sandboxMode: 'auto',
  sandboxEffective: '首次任务时验证',
  running,
})

beforeEach(() => {
  vi.resetModules()
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  permissionMode = 'restricted'
  running = true
  switches.length = 0
  host.chrome = { webview: { postMessage(json) {
    const request = JSON.parse(json) as Request
    let result: unknown = true
    switch (request.method) {
      case 'workspace.get': result = { workspace: 'C:\\a', dataDirectory: 'C:\\a\\.allagent\\data', sharedDirectory: 'C:\\shared' }; break
      case 'workspaces.list': result = []; break
      case 'session.controls': result = state(); break
      case 'session.setPermission': {
        switches.push(request.params.mode)
        permissionMode = request.params.mode
        result = state()
        break
      }
      default: break
    }
    host.__bridgeCallback(JSON.stringify({ id: request.id, result }))
  } } }
})
afterEach(() => { vi.unstubAllGlobals() })

async function mount() {
  const { PermissionPicker } = await import('../src/components/PermissionPicker')
  const { useWorkspaceStore } = await import('../src/stores/workspace')
  const store = useWorkspaceStore()
  const element = document.createElement('div')
  document.body.appendChild(element)
  const root = createRoot(element)
  await act(async () => {
    await store.load()
    root.render(<PermissionPicker />)
    await new Promise(resolve => setTimeout(resolve, 30))
  })
  return { element, root, store }
}

function click(element: Element | null) {
  element?.dispatchEvent(new MouseEvent('click', { bubbles: true }))
}

describe('permission chip', () => {
  it('switches modes while a task is running instead of locking itself', async () => {
    const { element, root, store } = await mount()
    try {
      expect(store.controls?.running).toBe(true)
      const chip = element.querySelector('button.permission-chip')
      expect(chip).not.toBeNull()
      expect(chip?.hasAttribute('disabled')).toBe(false)

      await act(async () => { click(chip); await new Promise(resolve => setTimeout(resolve, 30)) })
      const options = Array.from(element.querySelectorAll('[role="menuitemradio"]'))
      expect(options.length).toBeGreaterThan(0)
      const plan = options.find(option => option.textContent?.includes('只读规划'))
      await act(async () => { click(plan ?? null); await new Promise(resolve => setTimeout(resolve, 30)) })

      // The switch reached the host and the chip mirrors the reply, still during the run.
      expect(switches).toEqual(['plan'])
      expect(store.controls?.permissionMode).toBe('plan')
      expect(element.querySelector('button.permission-chip')?.textContent).toContain('只读规划')
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  })

  it('renders nothing when the host serves no workspace controls', async () => {
    host.chrome = { webview: { postMessage(json) {
      const request = JSON.parse(json) as Request
      host.__bridgeCallback(JSON.stringify({ id: request.id, result: null, error: 'Unknown method: workspace.get' }))
    } } }
    const { element, root } = await mount()
    try {
      expect(element.querySelector('button.permission-chip')).toBeNull()
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  })
})
