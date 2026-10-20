import { beforeEach, describe, expect, it, vi } from 'vitest'

// jsdom keeps window.parent === window, so the SDK talks to the test page itself; every
// message it sends is captured through a postMessage spy.
const parent = window

function initEvent(payload: unknown) {
  return new MessageEvent('message', {
    data: { allagent: 1, type: 'init', payload },
    origin: window.location.origin,
    source: parent as MessageEventSource,
  })
}

function eventEvent(name: string, data: unknown) {
  return new MessageEvent('message', {
    data: { allagent: 1, type: 'event', payload: { name, data } },
    origin: window.location.origin,
    source: parent as MessageEventSource,
  })
}

const initPayload = {
  sdkVersion: 1,
  plugin: { packageId: 'AllAgent.NET.SamplePlugin', name: '示例插件', version: '1.0.0' },
  view: { kind: 'page', id: 'dashboard' },
  theme: { mode: 'dark', vars: { '--background': '#131316', '--brand': '#38bdf8' } },
  context: { workspace: 'C:\\demo', conversationId: 'c1' },
  capabilities: ['notifications'],
}

beforeEach(() => { vi.resetModules() })

describe('plugin web SDK', () => {
  it('handshakes, applies the host theme and round-trips invoke', async () => {
    const post = vi.spyOn(window, 'postMessage')
    const { connect } = await import('../src/plugin-sdk/index')
    const client = connect()
    expect(connect()).toBe(client)

    const sent = () => (post.mock.calls.map(call => call[0]) as any[])
    const hello = sent().find(message => message?.type === 'hello')
    expect(hello).toMatchObject({ allagent: 1, type: 'hello' })
    expect(hello.payload.sdkVersion).toBe(1)

    window.dispatchEvent(initEvent(initPayload))
    await client.ready
    expect(client.plugin.packageId).toBe('AllAgent.NET.SamplePlugin')
    expect(client.view).toMatchObject({ kind: 'page', id: 'dashboard' })
    expect(client.capabilities).toEqual(['notifications'])
    expect(client.context.workspace).toBe('C:\\demo')
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(document.documentElement.style.getPropertyValue('--ag-background')).toBe('#131316')
    expect(document.documentElement.style.getPropertyValue('--ag-brand')).toBe('#38bdf8')

    const pending = client.invoke('history', { limit: 3 })
    const invoke = sent().find(message => message?.type === 'invoke' && message.payload.method === 'plugin.allagent.net.sampleplugin.history')
    expect(invoke.payload.params).toEqual({ limit: 3 })
    window.dispatchEvent(new MessageEvent('message', {
      data: { allagent: 1, type: 'invokeResult', payload: { id: invoke.id, result: { items: [], total: 0 } } },
      origin: window.location.origin,
      source: parent as MessageEventSource,
    }))
    await expect(pending).resolves.toEqual({ items: [], total: 0 })
    post.mockRestore()
  })

  it('subscribes with the host and delivers plugin events', async () => {
    const post = vi.spyOn(window, 'postMessage')
    const { connect } = await import('../src/plugin-sdk/index')
    const client = connect()
    window.dispatchEvent(initEvent(initPayload))
    await client.ready

    const received: unknown[] = []
    const off = client.on('tick', data => { received.push(data) })
    const subscribe = (post.mock.calls.map(call => call[0]) as any[]).find(message => message?.type === 'subscribe')
    expect(subscribe.payload.names).toEqual(['tick'])

    window.dispatchEvent(eventEvent('tick', { utc: 'now' }))
    expect(received).toEqual([{ utc: 'now' }])

    off()
    const unsubscribe = (post.mock.calls.map(call => call[0]) as any[]).find(message => message?.type === 'unsubscribe')
    expect(unsubscribe.payload.names).toEqual(['tick'])
    post.mockRestore()
  })

  it('surfaces host-side failures as coded errors', async () => {
    vi.spyOn(window, 'postMessage')
    const { connect } = await import('../src/plugin-sdk/index')
    const client = connect()
    window.dispatchEvent(initEvent(initPayload))
    await client.ready

    const pending = client.invoke('host.storage.set', { key: 'k', value: 'v' })
    const sent = (window.postMessage as any).mock.calls.map((call: any[]) => call[0])
    const invoke = sent.filter((message: any) => message?.type === 'invoke').at(-1)
    window.dispatchEvent(new MessageEvent('message', {
      data: { allagent: 1, type: 'invokeResult', payload: { id: invoke.id, error: { code: 'capability_denied', message: '插件未声明「storage」能力。' } } },
      origin: window.location.origin,
      source: parent as MessageEventSource,
    }))
    await expect(pending).rejects.toMatchObject({ code: 'capability_denied', message: '插件未声明「storage」能力。' })
  })
})
