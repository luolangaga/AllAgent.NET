import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { PluginWebInfo } from '../src/types'

type Request = { id: string; method: string; params: any }
type HostWindow = Window & { chrome?: { webview: { postMessage: (json: string) => void } }; __bridgeCallback: (json: string) => void }
const host = window as unknown as HostWindow
let handle: (request: Request) => void
const reply = (request: Request, result: unknown, error?: string) => host.__bridgeCallback(JSON.stringify({ id: request.id, result, error }))

const ORIGIN = 'http://127.0.0.1:51234'

const plugin = (): PluginWebInfo => ({
  packageId: 'AllAgent.NET.SamplePlugin', pluginId: 'sample', name: '示例插件', version: '1.0.0',
  enabled: true, webEnabled: true, active: true,
  capabilities: ['notifications'], contributes: null, devUrl: null, error: null,
})

function createFrame() {
  const posted: any[] = []
  const frameWindow = { postMessage: (message: unknown) => { posted.push(message) } }
  const element = { contentWindow: frameWindow } as unknown as HTMLIFrameElement
  return { element, posted, frameWindow }
}

function deliver(frameWindow: unknown, type: string, payload: unknown, origin = ORIGIN, id?: string) {
  window.dispatchEvent(new MessageEvent('message', {
    data: { allagent: 1, type, id, payload },
    origin,
    source: frameWindow as MessageEventSource,
  }))
}

const settle = () => new Promise(resolve => setTimeout(resolve, 10))

beforeEach(() => {
  vi.resetModules()
  handle = request => reply(request, true)
  host.chrome = { webview: { postMessage: (json: string) => handle(JSON.parse(json)) } }
})

describe('plugin frame runtime', () => {
  it('completes the handshake only for the frame token from the expected origin', async () => {
    const runtime = await import('../src/lib/pluginRuntime')
    const frame = createFrame()
    const ready = vi.fn()
    runtime.registerFrame(frame.element, {
      plugin: plugin(),
      view: { kind: 'page', id: 'dashboard' },
      entry: 'web/dashboard.html',
      onReady: ready,
    }, { src: 'about:blank', token: 'tok-1', origin: ORIGIN })

    deliver(frame.frameWindow, 'hello', { token: 'other' })
    deliver(frame.frameWindow, 'hello', { token: 'tok-1' }, 'http://evil.example')
    expect(frame.posted).toHaveLength(0)

    deliver(frame.frameWindow, 'hello', { token: 'tok-1', sdkVersion: 1 })
    expect(ready).toHaveBeenCalledTimes(1)
    expect(frame.posted).toHaveLength(1)
    expect(frame.posted[0]).toMatchObject({ allagent: 1, type: 'init' })
    expect(frame.posted[0].payload).toMatchObject({
      sdkVersion: 1,
      plugin: { packageId: 'AllAgent.NET.SamplePlugin' },
      view: { kind: 'page', id: 'dashboard' },
      capabilities: ['notifications'],
    })
  })

  it('hands the frame an init payload that structured clone accepts', async () => {
    const runtime = await import('../src/lib/pluginRuntime')
    const frame = createFrame()
    // The contributions store wraps arrays in Proxies; postMessage rejects them with a
    // DataCloneError that the sender used to swallow, leaving the frame stuck in handshake.
    const capabilities = new Proxy(['notifications'], {}) as unknown as string[]
    runtime.registerFrame(frame.element, {
      plugin: { ...plugin(), capabilities },
      view: { kind: 'settings', id: 'codegraph' },
    }, { src: 'about:blank', token: 'tok', origin: ORIGIN })

    deliver(frame.frameWindow, 'hello', { token: 'tok' })
    const init = frame.posted[0]
    expect(() => structuredClone(init)).not.toThrow()
    expect(init.payload.capabilities).toEqual(['notifications'])
  })

  it('routes own-namespace calls to the bridge and gates host.* behind capabilities', async () => {
    const runtime = await import('../src/lib/pluginRuntime')
    const frame = createFrame()
    runtime.registerFrame(frame.element, { plugin: plugin(), view: { kind: 'page', id: 'x' } }, { src: 'about:blank', token: 'tok', origin: ORIGIN })
    deliver(frame.frameWindow, 'hello', { token: 'tok' })

    const methods: string[] = []
    handle = request => { methods.push(request.method); return reply(request, { echo: request.params?.value ?? null }) }

    deliver(frame.frameWindow, 'invoke', { method: 'plugin.AllAgent.NET.SamplePlugin.history', params: { value: 7 } }, ORIGIN, '1')
    deliver(frame.frameWindow, 'invoke', { method: 'host.notify', params: { title: '来自插件' } }, ORIGIN, '2')
    deliver(frame.frameWindow, 'invoke', { method: 'host.storage.set', params: { key: 'k', value: 'v' } }, ORIGIN, '3')
    deliver(frame.frameWindow, 'invoke', { method: 'plugin.other.package.method', params: {} }, ORIGIN, '4')
    deliver(frame.frameWindow, 'invoke', { method: 'conversations.delete', params: {} }, ORIGIN, '5')
    await settle()

    expect(methods).toEqual(['plugin.AllAgent.NET.SamplePlugin.history'])
    const result = frame.posted.find(message => message.type === 'invokeResult' && message.payload.id === '1')
    expect(result.payload.result).toEqual({ echo: 7 })
    const notify = frame.posted.find(message => message.type === 'invokeResult' && message.payload.id === '2')
    expect(notify.payload.result).toBe(true)
    const results = (id: string) => frame.posted.find(message => message.type === 'invokeResult' && message.payload.id === id)
    expect(results('3').payload.error.code).toBe('capability_denied')
    expect(results('4').payload.error.code).toBe('denied')
    expect(results('5').payload.error.code).toBe('denied')
  })

  it('fans plugin backend pushes out to subscribed frames only', async () => {
    const runtime = await import('../src/lib/pluginRuntime')
    const { bridgeEvents } = await import('../src/bridge')
    const frame = createFrame()
    runtime.registerFrame(frame.element, { plugin: plugin(), view: { kind: 'page', id: 'x' } }, { src: 'about:blank', token: 'tok', origin: ORIGIN })
    deliver(frame.frameWindow, 'hello', { token: 'tok' })
    const base = frame.posted.length

    bridgeEvents.emit('plugin.AllAgent.NET.SamplePlugin.tick' as never, { utc: 'now' } as never)
    expect(frame.posted).toHaveLength(base)

    deliver(frame.frameWindow, 'subscribe', { names: ['tick'] })
    bridgeEvents.emit('plugin.AllAgent.NET.SamplePlugin.tick' as never, { utc: 'now' } as never)
    const event = frame.posted.at(-1)
    expect(event.type).toBe('event')
    expect(event.payload).toMatchObject({ name: 'tick', data: { utc: 'now' } })

    bridgeEvents.emit('plugin.other.plugin.tick' as never, { utc: 'now' } as never)
    expect(frame.posted.at(-1)).toBe(event)
  })

  it('reports resize requests and ignores messages from foreign sources', async () => {
    const runtime = await import('../src/lib/pluginRuntime')
    const frame = createFrame()
    const resized = vi.fn()
    runtime.registerFrame(frame.element, {
      plugin: plugin(),
      view: { kind: 'toolcard', id: 'call-1' },
      onResize: resized,
    }, { src: 'about:blank', token: 'tok', origin: ORIGIN })
    deliver(frame.frameWindow, 'hello', { token: 'tok' })
    deliver(frame.frameWindow, 'resize', { height: 333 })
    expect(resized).toHaveBeenCalledWith(333)

    const before = frame.posted.length
    window.dispatchEvent(new MessageEvent('message', {
      data: { allagent: 1, type: 'invoke', payload: { id: '9', method: 'plugin.AllAgent.NET.SamplePlugin.history' } },
      origin: ORIGIN,
      source: { postMessage: () => {} } as unknown as MessageEventSource,
    }))
    await settle()
    expect(frame.posted).toHaveLength(before)
  })
})
