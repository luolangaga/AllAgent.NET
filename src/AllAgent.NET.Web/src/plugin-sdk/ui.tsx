// Public, frame-local React runtime and curated Iconiq components. No host stores or bridge internals.
import * as React from 'react'
export { React }
export { createRoot } from 'react-dom/client'
export { Button } from '@/components/ui/b-button'
export { Input } from '@/components/ui/input'
export { Badge } from '@/components/ui/badge'
export * from '@/components/ui/b-dialog'
export * from '@/components/ui/b-tabs'
export * from '@/components/ui/b-select'

import { createRoot, type Root } from 'react-dom/client'
import { Button } from '@/components/ui/b-button'
import css from './ui.css?inline'

export const UI_SDK_VERSION = 1

let sheet: CSSStyleSheet | undefined
/** Opt-in document styles for React plugins. HTML adapters use isolated shadow styles instead. */
export function installStyles() {
  if (!document.getElementById('allagent-plugin-ui-styles')) {
    const style = document.createElement('style')
    style.id = 'allagent-plugin-ui-styles'
    style.textContent = css
    document.head.append(style)
  }
  const apply = () => document.documentElement.classList.toggle('dark', document.documentElement.dataset.theme === 'dark')
  apply()
  const observer = new MutationObserver(apply)
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })
  return () => observer.disconnect()
}
function attachStyles(shadow: ShadowRoot) {
  if ('adoptedStyleSheets' in shadow && typeof CSSStyleSheet.prototype.replaceSync === 'function') {
    sheet ??= new CSSStyleSheet()
    if (!sheet.cssRules.length) sheet.replaceSync(css)
    shadow.adoptedStyleSheets = [sheet]
  } else {
    const style = document.createElement('style')
    style.textContent = css
    shadow.append(style)
  }
}

/** Registers <ag-button>, a real Iconiq React Button for framework-free HTML plugins.
 * Text is light-DOM slot content; arbitrary HTML never crosses into React props. */
export function registerElements() {
  if (customElements.get('ag-button')) return
  class PluginButton extends HTMLElement {
    static observedAttributes = ['disabled', 'variant', 'size', 'type', 'aria-label', 'class']
    private root: Root | undefined
    private container: HTMLDivElement | undefined
    private observer: MutationObserver | undefined
    private epoch = 0

    get disabled() { return this.hasAttribute('disabled') }
    set disabled(value: boolean) { this.toggleAttribute('disabled', value) }
    get type() { return this.getAttribute('type') ?? 'button' }
    set type(value: string) { this.setAttribute('type', value) }

    connectedCallback() {
      this.epoch++
      if (!this.root) {
        const shadow = this.shadowRoot ?? this.attachShadow({ mode: 'open', delegatesFocus: true })
        attachStyles(shadow)
        this.container = document.createElement('div')
        shadow.append(this.container)
        this.root = createRoot(this.container)
      }
      this.observer?.disconnect()
      this.observer = new MutationObserver(() => this.renderButton())
      this.observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'class'] })
      this.renderButton()
    }
    disconnectedCallback() {
      this.observer?.disconnect()
      const epoch = ++this.epoch
      // DOM moves can disconnect/reconnect in the same task; keep the React tree in that case.
      queueMicrotask(() => {
        if (this.epoch !== epoch || this.isConnected) return
        this.root?.unmount(); this.root = undefined
        this.container?.remove(); this.container = undefined
      })
    }
    attributeChangedCallback() { if (this.isConnected) this.renderButton() }
    override click() {
      if (!this.disabled) this.shadowRoot?.querySelector('button')?.click()
    }
    override focus(options?: FocusOptions) { this.shadowRoot?.querySelector('button')?.focus(options) }
    private renderButton() {
      if (!this.container || !this.root) return
      this.container.classList.toggle('dark', document.documentElement.dataset.theme === 'dark' || document.documentElement.classList.contains('dark'))
      const variants = ['default', 'outline', 'ghost', 'secondary', 'destructive', 'link'] as const
      const requested = this.getAttribute('variant') ?? (this.classList.contains('primary') ? 'default' : 'outline')
      const variant = variants.find(value => value === requested) ?? 'outline'
      this.root.render(<Button
        type="button"
        size={this.getAttribute('size') === 'default' ? 'default' : 'sm'}
        variant={variant}
        disabled={this.disabled}
        aria-label={this.getAttribute('aria-label') ?? undefined}
        onClick={event => {
          const native = event.nativeEvent
          // Native buttons inside a shadow tree cannot submit a light-DOM form themselves.
          if (this.type === 'submit') queueMicrotask(() => { if (!native.defaultPrevented && !this.disabled) this.closest('form')?.requestSubmit() })
          if (this.type === 'reset') queueMicrotask(() => { if (!native.defaultPrevented && !this.disabled) this.closest('form')?.reset() })
        }}
      ><slot /></Button>)
    }
  }
  customElements.define('ag-button', PluginButton)
}
