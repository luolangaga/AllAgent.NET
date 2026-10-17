import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

type Request = { id: string; method: string }
const host = window as unknown as { chrome: { webview: { postMessage(json: string): void } }; __bridgeCallback(json: string): void }

beforeEach(() => {
  vi.resetModules()
  vi.stubGlobal('IS_REACT_ACT_ENVIRONMENT', true)
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  vi.stubGlobal('scrollTo', vi.fn())
  host.chrome = { webview: { postMessage(json) {
    const request = JSON.parse(json) as Request
    const data = request.method === 'conversations.create'
      ? { id: 'c', title: 'test', createdAt: '', updatedAt: '' }
      : request.method === 'chat.startStream' ? 's' : true
    host.__bridgeCallback(JSON.stringify({ id: request.id, data }))
  } } }
})
afterEach(() => { vi.unstubAllGlobals() })

describe('live React updates', () => {
  it('automatically opens each reasoning round, closes on output and tools, and allows reopening completed reasoning', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const { useStore } = await import('../src/lib/store')
    const { MessageBubble } = await import('../src/components/MessageBubble')
    function Transcript() { const store = useStore(useConversationsStore); return <>{store.messages.map(message => <MessageBubble key={message.id} message={message} />)}</> }
    const element = document.createElement('div')
    document.body.appendChild(element)
    const root = createRoot(element)
    const store = useConversationsStore()
    const emit = async (eventName: string, data: unknown) => {
      await act(async () => {
        host.__bridgeCallback(JSON.stringify({ type: 'event', streamId: 's', eventName, data }))
        await new Promise(resolve => setTimeout(resolve, 25))
      })
    }
    const trigger = (iteration: number) => element.querySelector(`[data-reasoning-iteration="${iteration}"] button`)!
    try {
      await act(async () => { await store.sendMessage('task', 'config'); root.render(<Transcript />) })
      await emit('agent.iteration', { current: 1, maximum: 0, toolCalls: 0 })
      await emit('agent.thinking', { text: '先确认问题' })
      expect(trigger(1).getAttribute('aria-expanded')).toBe('true')
      await emit('agent.token', { text: '我先读取文件。' })
      expect(trigger(1).getAttribute('aria-expanded')).toBe('false')
      await act(async () => { trigger(1).dispatchEvent(new MouseEvent('click', { bubbles: true })) })
      expect(trigger(1).getAttribute('aria-expanded')).toBe('true')
      await emit('agent.iteration', { current: 2, maximum: 0, toolCalls: 1 })
      await emit('agent.thinking', { text: '再检查结果' })
      expect(trigger(2).getAttribute('aria-expanded')).toBe('true')
      await emit('agent.toolCallStarted', { id: 't', name: 'file_read', args: '{}' })
      expect(trigger(2).getAttribute('aria-expanded')).toBe('false')
      await emit('agent.done', { reason: 'stop' })
      expect(element.querySelectorAll('[data-reasoning-iteration]')).toHaveLength(2)
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  })
  it('paints tokens and tool completion in a mounted memoized message without rerendering unrelated stores', async () => {
    const { useConversationsStore } = await import('../src/stores/conversations')
    const { defineStore, ref, useStore } = await import('../src/lib/store')
    const { MessageBubble } = await import('../src/components/MessageBubble')
    const other = defineStore('unrelated', () => ({ count: ref(0) }))
    let unrelatedRenders = 0
    function Unrelated() { const state = useStore(other); unrelatedRenders++; return <span>{state.count}</span> }
    function Transcript() {
      const store = useStore(useConversationsStore)
      return <>{store.messages.map(message => <MessageBubble key={message.id} message={message} />)}</>
    }
    const element = document.createElement('div')
    document.body.appendChild(element)
    const root = createRoot(element)
    const store = useConversationsStore()
    const emit = (eventName: string, data: unknown) => host.__bridgeCallback(JSON.stringify({ type: 'event', streamId: 's', eventName, data }))
    try {
      await act(async () => { await store.sendMessage('read', 'config') })
      await act(async () => { root.render(<><Unrelated /><Transcript /></>) })
      const before = unrelatedRenders
      await act(async () => {
        for (let i = 0; i < 100; i++) emit('agent.token', { text: '字' })
        emit('agent.toolCallStarted', { id: 't', name: 'file_read', args: '{}' })
        await new Promise(resolve => setTimeout(resolve, 25))
      })
      expect(element.textContent).toContain('字'.repeat(100))
      expect(element.textContent).toContain('执行中')
      await act(async () => {
        emit('agent.toolCallCompleted', { id: 't', name: 'file_read', result: '{"ok":true}' })
        emit('agent.done', { reason: 'stop' })
        await new Promise(resolve => setTimeout(resolve, 25))
      })
      expect(element.textContent).toContain('已完成')
      expect(element.textContent).not.toContain('执行中')
      expect(unrelatedRenders).toBe(before)
    } finally {
      await act(async () => { root.unmount() })
      element.remove()
    }
  })
})
