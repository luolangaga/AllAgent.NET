import { act } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { registerElements } from '../src/plugin-sdk/ui'

beforeEach(() => {
  ;(globalThis as any).IS_REACT_ACT_ENVIRONMENT = true
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} })))
  registerElements()
})
afterEach(async () => { await act(async () => { document.body.replaceChildren() }); vi.unstubAllGlobals() })

describe('plugin Iconiq UI adapter', () => {
  it('renders an actual Iconiq button, preserves labels, disabled state and light DOM clicks', async () => {
    const control = document.createElement('ag-button') as HTMLElement & { disabled: boolean }
    control.textContent = '启动 Agent'
    const click = vi.fn()
    control.addEventListener('click', click)
    await act(async () => { document.body.append(control) })
    expect(control.shadowRoot?.querySelector('button')).not.toBeNull()
    expect(control.shadowRoot?.querySelector('slot')).not.toBeNull()
    await act(async () => { control.click() })
    expect(click).toHaveBeenCalledTimes(1)
    await act(async () => { control.disabled = true })
    expect(control.shadowRoot?.querySelector('button')?.disabled).toBe(true)
    control.click()
    expect(click).toHaveBeenCalledTimes(1)
  })

  it('submits a light DOM form and follows the frame theme', async () => {
    const form = document.createElement('form')
    const submit = vi.fn(event => event.preventDefault())
    form.addEventListener('submit', submit)
    const control = document.createElement('ag-button')
    control.setAttribute('type', 'submit'); control.textContent = '连接 GitHub'
    await act(async () => { form.append(control); document.body.append(form) })
    await act(async () => { control.click(); await Promise.resolve() })
    expect(submit).toHaveBeenCalledTimes(1)
    await act(async () => { document.documentElement.dataset.theme = 'dark'; await Promise.resolve() })
    expect(control.shadowRoot?.querySelector('.dark')).not.toBeNull()
    await act(async () => { delete document.documentElement.dataset.theme; await Promise.resolve() })
  })
})
