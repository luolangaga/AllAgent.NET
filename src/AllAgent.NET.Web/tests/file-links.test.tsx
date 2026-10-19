import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string; params?: unknown }
const host = window as unknown as { chrome: { webview: { postMessage(json: string): void } }; __bridgeCallback(json: string): void }
const requests: Request[] = []
// How the stubbed host answers `workspace.readFile`: an error (directory / outside path / missing)
// keeps the external-open fallback, a payload routes the click into the file bubble.
let readFile: 'error' | 'file' = 'error'

beforeEach(() => {
  vi.resetModules()
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  requests.length = 0
  readFile = 'error'
  host.chrome = { webview: { postMessage(json) {
    const request = JSON.parse(json) as Request
    requests.push(request)
    if (request.method === 'workspace.readFile' && readFile === 'error') {
      host.__bridgeCallback(JSON.stringify({ id: request.id, error: '这是一个目录，无法预览文件内容。' }))
      return
    }
    if (request.method === 'workspace.readFile') {
      host.__bridgeCallback(JSON.stringify({ id: request.id, data: { path: 'src/App.tsx', kind: 'text', size: 12, modified: new Date().toISOString(), text: 'hello world', base64: null, truncated: false } }))
      return
    }
    host.__bridgeCallback(JSON.stringify({ id: request.id, data: true }))
  } } }
})
afterEach(() => { vi.unstubAllGlobals() })

async function renderMessage(content: string) {
  const { Markdown } = await import('../src/components/Markdown')
  const element = document.createElement('div')
  document.body.appendChild(element)
  const root = createRoot(element)
  await act(async () => { root.render(<Markdown content={content} />) })
  return element
}

async function click(element: Element) {
  const event = new MouseEvent('click', { bubbles: true, cancelable: true })
  await act(async () => { element.dispatchEvent(event) })
  return event
}

describe('reference routing', () => {
  it('reads paths as host references and web addresses as browser links', async () => {
    const { isFilePath, referencePath } = await import('../src/lib/fileLinks')
    expect(isFilePath('pelican-bike.html')).toBe(true)
    expect(isFilePath('src/App.tsx')).toBe(true)
    expect(isFilePath('C:\\Users\\me\\pelican-bike.html')).toBe(true)
    expect(isFilePath('print(1)')).toBe(false)
    expect(isFilePath('https://example.com/a.html')).toBe(false)
    expect(referencePath('file:///C:/tmp/a.html')).toBe('C:/tmp/a.html')
    expect(referencePath('docs/%E6%8A%A5%E5%91%8A.html')).toBe('docs/报告.html')
    expect(referencePath('#section')).toBeNull()
    expect(referencePath('https://example.com')).toBeNull()
  })
})

describe('file references in messages', () => {
  it('previews a workspace file in the file bubble instead of opening it externally', async () => {
    readFile = 'file'
    const element = await renderMessage('看一下 `src/App.tsx`。')
    await click(element.querySelector('code.file-ref')!)

    expect(requests.filter(request => request.method === 'shell.openPath')).toEqual([])
    const panels = (await import('../src/stores/panels')).usePanelsStore()
    const browser = (await import('../src/stores/workspaceBrowser')).useWorkspaceBrowserStore()
    expect(panels.open.files).toBe(true)
    expect(browser.file?.path).toBe('src/App.tsx')
  })

  it('hands a path the host cannot read to the host opener', async () => {
    const element = await renderMessage('已生成 HTML5 页面：`pelican-bike.html`。')
    const code = element.querySelector('code.file-ref')
    expect(code?.textContent).toBe('pelican-bike.html')

    await click(code!)
    expect(requests.filter(request => request.method === 'workspace.readFile')).toEqual([
      expect.objectContaining({ params: { path: 'pelican-bike.html' } }),
    ])
    expect(requests.filter(request => request.method === 'shell.openPath')).toEqual([
      expect.objectContaining({ params: { path: 'pelican-bike.html' } }),
    ])
  })

  it('intercepts a markdown link to a local path instead of navigating the page', async () => {
    const element = await renderMessage('文件位置：[张明轩](C:\\Users\\张明轩\\pelican-bike.html)')
    const event = await click(element.querySelector('a')!)
    expect(event.defaultPrevented).toBe(true)
    expect(requests.filter(request => request.method === 'shell.openPath')).toEqual([
      expect.objectContaining({ params: { path: 'C:\\Users\\张明轩\\pelican-bike.html' } }),
    ])
  })

  it('leaves plain code and web links with the browser', async () => {
    const element = await renderMessage('运行 `print(1)`，参考 [文档](https://example.com/a.html)。')
    expect(element.querySelector('code.file-ref')).toBeNull()
    expect(requests.filter(request => request.method === 'shell.openPath')).toEqual([])
  })
})
