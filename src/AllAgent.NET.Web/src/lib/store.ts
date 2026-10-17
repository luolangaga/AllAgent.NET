import { useSyncExternalStore } from 'react'
interface Scope { version: number; scheduled: boolean; listeners: Set<() => void>; proxies: WeakMap<object, object> }
const createScope = (): Scope => ({ version: 0, scheduled: false, listeners: new Set(), proxies: new WeakMap() })
const fallback = createScope()
let activeScope = fallback
const storeScopes = new WeakMap<object, Scope>()
function changed(scope: Scope) {
  scope.version++
  if (scope.scheduled) return
  scope.scheduled = true
  // Values remain synchronously readable; React receives one notification per
  // frame instead of one for every field of every streamed event.
  window.setTimeout(() => {
    scope.scheduled = false
    scope.listeners.forEach(listener => listener())
  }, 16)
}
function observe<T>(value: T, scope: Scope): T {
  if (!value || typeof value !== 'object' || (!Array.isArray(value) && Object.getPrototypeOf(value) !== Object.prototype)) return value
  const proxies = scope.proxies
  if (proxies.has(value)) return proxies.get(value) as T
  const proxy = new Proxy(value, {
    get(target, key, receiver) { return observe(Reflect.get(target, key, receiver), scope) },
    set(target, key, next, receiver) { const previous = Reflect.get(target, key); const ok = Reflect.set(target, key, next, receiver); if (ok && previous !== next) changed(scope); return ok },
    deleteProperty(target, key) { const existed = Reflect.has(target, key); const ok = Reflect.deleteProperty(target, key); if (ok && existed) changed(scope); return ok },
  })
  proxies.set(value, proxy); proxies.set(proxy, proxy)
  return proxy
}
interface Ref<T> { readonly __ref: true; value: T }
export function ref<T>(initial: T): Ref<T> {
  const scope = activeScope
  let current = observe(initial, scope)
  return { __ref: true, get value() { return current }, set value(next) { const observed = observe(next, scope); if (current !== observed) { current = observed; changed(scope) } } }
}
export function computed<T>(read: () => T): Ref<T> { return { __ref: true, get value() { return read() } } }
type Unwrap<T> = { [K in keyof T]: T[K] extends Ref<infer V> ? V : T[K] }
export function defineStore<T extends object>(_name: string, setup: () => T): () => Unwrap<T> {
  let store: Unwrap<T> | undefined
  return () => {
    if (!store) {
      const scope = createScope()
      const previous = activeScope
      activeScope = scope
      let state: T
      try { state = setup() } finally { activeScope = previous }
      store = new Proxy(state, {
        get(target, key) { const value: any = Reflect.get(target, key); return value?.__ref ? value.value : value },
        set(target, key, next) { const value: any = Reflect.get(target, key); if (value?.__ref) value.value = next; else if (Reflect.get(target, key) !== next) { Reflect.set(target, key, next); changed(scope) } return true }
      }) as Unwrap<T>
      storeScopes.set(store, scope)
    }
    return store
  }
}
export function useStore<T extends object>(getStore: () => T): T {
  const store = getStore()
  const scope = storeScopes.get(store) ?? fallback
  useSyncExternalStore(listener => { scope.listeners.add(listener); return () => { scope.listeners.delete(listener) } }, () => scope.version, () => scope.version)
  return store
}
