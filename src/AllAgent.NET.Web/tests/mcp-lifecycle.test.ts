import { beforeEach, expect, it, vi } from 'vitest'

const host = window as unknown as { chrome: { webview: { postMessage(json: string): void } }; __bridgeCallback(json: string): void }
beforeEach(() => { vi.resetModules() })

it('reads actual connection state and starts or stops services only through explicit actions', async () => {
  let enabled = false
  let connected = false
  const calls: string[] = []
  host.chrome = { webview: { postMessage(json) {
    const request = JSON.parse(json)
    calls.push(request.method)
    let data: unknown = true
    if (request.method === 'mcpServers.save') { enabled = request.params.isEnabled; connected = false; data = { id: 'm' } }
    if (request.method === 'mcpServers.list') data = [{ id: 'm', name: 'fixture', command: 'fixture', arguments: '', isEnabled: enabled, isConnected: connected, createdAt: '' }]
    if (request.method === 'mcpServers.connect') connected = true
    if (request.method === 'mcpServers.disconnect') connected = false
    host.__bridgeCallback(JSON.stringify({ id: request.id, data }))
  } } }
  const { useMcpServersStore } = await import('../src/stores/mcpServers')
  const store = useMcpServersStore()
  await store.loadAll()
  await store.save({ name: 'fixture', command: 'fixture' })
  expect(enabled).toBe(false)
  expect(calls).not.toContain('mcpServers.connect')
  await store.save({ id: 'm', name: 'fixture', command: 'fixture', isEnabled: true })
  await store.connect('m')
  await store.loadAll()
  expect(store.connectedIds).toEqual(['m'])
  await store.disconnect('m')
  expect(store.connectedIds).toEqual([])
})
