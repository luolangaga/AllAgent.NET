import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params?: any }
const host = window as unknown as { chrome: { webview: { postMessage(json: string): void } }; __bridgeCallback(json: string): void }

function reply(request: Request, data: unknown, error?: string) {
  host.__bridgeCallback(JSON.stringify({ id: request.id, data, error }))
}

const status = (workerAvailable: boolean, modules: unknown[]) => ({
  workerAvailable,
  workerPath: 'C:\\app\\AllAgent.NET.PowerShell.exe',
  root: 'C:\\Users\\demo\\AppData\\Roaming\\AllAgent.NET\\powershell\\Modules',
  modules,
})

const catalog = [{ name: 'Pester', version: '5.7.1', commandCount: 12, error: null }]

beforeEach(() => {
  vi.resetModules()
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  host.chrome = { webview: { postMessage(json) {
    const request = JSON.parse(json) as Request
    switch (request.method) {
      case 'powershell.status': return reply(request, status(true, catalog))
      case 'powershell.commands': return reply(request, { name: 'Pester', version: '5.7.1', total: 12, commands: ['Invoke-Pester', 'New-PesterConfiguration'], error: null })
      default: return reply(request, true)
    }
  } } }
})
afterEach(() => { vi.unstubAllGlobals() })

async function mount() {
  const { default: PowerShell } = await import('../src/views/PowerShell')
  const element = document.createElement('div')
  document.body.appendChild(element)
  const root = createRoot(element)
  await act(async () => { root.render(<PowerShell />) })
  return { element, root }
}

/** The bridge settles asynchronously and the store notifies one frame later: poll inside act. */
async function settle(check: () => boolean, timeout = 3000) {
  const deadline = Date.now() + timeout
  while (!check() && Date.now() < deadline) {
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)) })
  }
}

describe('PowerShell 组件页', () => {
  it('renders the verified catalog with per-module status and the shared directory', async () => {
    const { element, root } = await mount()
    try {
      await settle(() => element.textContent?.includes('Pester') === true)
      expect(element.textContent).toContain('POWERSHELL 能力商店')
      expect(element.textContent).toContain('Pester')
      expect(element.textContent).toContain('5.7.1')
      expect(element.textContent).toContain('12 个命令')
      expect(element.textContent).toContain('可用')
      expect(element.textContent).toContain('powershell\\Modules')
      // A present worker is not warned about.
      expect(element.textContent).not.toContain('PowerShell 组件缺失')
      expect(element.textContent).toContain('按名称安装')
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  })

  it('opens the exported commands of a module from the list', async () => {
    const { element, root } = await mount()
    try {
      await settle(() => element.textContent?.includes('Pester') === true)
      const button = [...element.querySelectorAll('button')].find(item => item.textContent === '查看命令')!
      expect(button).toBeTruthy()
      await act(async () => { button.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
      await settle(() => document.body.textContent?.includes('Invoke-Pester') === true)
      expect(document.body.textContent).toContain('Invoke-Pester')
      expect(document.body.textContent).toContain('New-PesterConfiguration')
      expect(document.body.textContent).toContain('共 12 个命令')
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  })

  it('warns instead of offering installs when the embedded SDK is missing', async () => {
    host.chrome = { webview: { postMessage(json) { reply(JSON.parse(json) as Request, status(false, [])) } } }
    const { element, root } = await mount()
    try {
      await settle(() => element.textContent?.includes('PowerShell 组件缺失') === true)
      expect(element.textContent).toContain('PowerShell 组件缺失')
      expect(element.textContent).toContain('重新安装 AllAgent')
      expect(element.textContent).toContain('还没有安装模块')
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  })
})
