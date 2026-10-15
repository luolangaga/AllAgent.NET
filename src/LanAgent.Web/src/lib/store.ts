import { useSyncExternalStore } from 'react'
let version = 0
const listeners = new Set<() => void>()
function changed() { version++; listeners.forEach(listener => listener()) }
const proxies = new WeakMap<object, object>()
function observe<T>(value: T): T {
  if (!value || typeof value !== 'object' || (!Array.isArray(value) && Object.getPrototypeOf(value) !== Object.prototype)) return value
  if (proxies.has(value)) return proxies.get(value) as T
  const proxy = new Proxy(value, {
    get(target, key, receiver) { return observe(Reflect.get(target, key, receiver)) },
    set(target, key, next, receiver) { const previous = Reflect.get(target, key); const ok = Reflect.set(target, key, next, receiver); if (previous !== next) changed(); return ok }
  })
  proxies.set(value, proxy); proxies.set(proxy, proxy)
  return proxy
}
interface Ref<T> { readonly __ref: true; value: T }
export function ref<T>(initial: T): Ref<T> {
  let current = observe(initial)
  return { __ref: true, get value() { return current }, set value(next) { current = observe(next); changed() } }
}
export function computed<T>(read: () => T): Ref<T> { return { __ref: true, get value() { return read() } } }
type Unwrap<T> = { [K in keyof T]: T[K] extends Ref<infer V> ? V : T[K] }
export function defineStore<T extends object>(_name: string, setup: () => T): () => Unwrap<T> {
  let store: Unwrap<T> | undefined
  return () => {
    if (!store) {
      const state = setup()
      store = new Proxy(state, {
        get(target, key) { const value: any = Reflect.get(target, key); return value?.__ref ? value.value : value },
        set(target, key, next) { const value: any = Reflect.get(target, key); if (value?.__ref) value.value = next; else { Reflect.set(target, key, next); changed() } return true }
      }) as Unwrap<T>
    }
    return store
  }
}
export function useStore<T>(getStore: () => T): T {
  const store = getStore()
  useSyncExternalStore(listener => { listeners.add(listener); return () => { listeners.delete(listener) } }, () => version)
  return store
}
