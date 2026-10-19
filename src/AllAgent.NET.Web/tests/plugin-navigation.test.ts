import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { PluginWebInfo } from '../src/types'

const mocks = vi.hoisted(() => ({ switchTo: vi.fn(), loadAll: vi.fn(), select: vi.fn() }))
vi.mock('../src/stores/workspace', () => ({ useWorkspaceStore: () => ({ workspace: 'C:\\one', switchTo: mocks.switchTo }) }))
vi.mock('../src/stores/conversations', () => ({ useConversationsStore: () => ({ loadAll: mocks.loadAll, selectConversation: mocks.select }) }))
import { invokeHostApi } from '../src/lib/hostApi'
const plugin = (capabilities: string[]) => ({ packageId: 'AllAgent.NET.GitHub', capabilities }) as PluginWebInfo
beforeEach(() => { vi.clearAllMocks() })
describe('plugin cross-workspace navigation', () => {
  it('requires the workspace.switch capability before switching', async () => {
    await expect(invokeHostApi(plugin(['navigation']), 'host.openConversation', { id: 'id', workspace: 'C:\\two' })).rejects.toMatchObject({ code: 'capability_denied' })
    expect(mocks.switchTo).not.toHaveBeenCalled()
    expect(mocks.select).not.toHaveBeenCalled()
  })
  it('switches workspace before loading and opening the new session', async () => {
    const order: string[] = []
    mocks.switchTo.mockImplementation(async () => { order.push('switch') })
    mocks.loadAll.mockImplementation(async () => { order.push('load') })
    mocks.select.mockImplementation(async () => { order.push('select') })
    await invokeHostApi(plugin(['navigation', 'workspace.switch']), 'host.openConversation', { id: 'id', workspace: 'C:\\two' })
    expect(order).toEqual(['switch', 'load', 'select'])
    expect(mocks.select).toHaveBeenCalledWith('id')
  })
})
