import { useSyncExternalStore } from 'react'
import type { Language, LanguageSetting } from '@/types'

const CACHE_KEY = 'allagent.language'
const listeners = new Set<() => void>()

function readCached(): Language {
  try {
    const cached = localStorage.getItem(CACHE_KEY)
    if (cached === 'zh' || cached === 'en') return cached
  } catch {
    // Storage can be unavailable in sandboxed previews; system language still works.
  }
  return resolveLanguage('auto')
}

let current: Language = readCached()

export function resolveLanguage(setting: LanguageSetting, languages?: readonly string[]): Language {
  if (setting === 'zh' || setting === 'en') return setting
  const preferred = languages ?? (typeof navigator === 'undefined'
    ? []
    : navigator.languages?.length ? navigator.languages : [navigator.language])
  return preferred.some(item => item.toLowerCase().startsWith('zh')) ? 'zh' : 'en'
}

function applyDocument(language: Language): void {
  if (typeof document !== 'undefined') {
    document.documentElement.lang = language === 'zh' ? 'zh-CN' : 'en'
    document.documentElement.dataset.language = language
  }
  try {
    localStorage.setItem(CACHE_KEY, language)
  } catch {
    // Ignore storage failures; the backend remains the source of truth.
  }
}

export function applyResolvedLanguage(language: Language): void {
  const next: Language = language === 'zh' ? 'zh' : 'en'
  const changed = next !== current
  current = next
  applyDocument(next)
  if (changed) listeners.forEach(listener => listener())
}

export function setLanguage(setting: LanguageSetting, options?: { resolved?: Language }): void {
  applyResolvedLanguage(options?.resolved ?? resolveLanguage(setting))
}

export function currentLanguage(): Language {
  return current
}

export function languageTag(): 'zh-CN' | 'en-US' {
  return current === 'zh' ? 'zh-CN' : 'en-US'
}

export function t(zh: string, en: string): string {
  return current === 'zh' ? zh : en
}

export function subscribeLanguage(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function useT(): typeof t {
  useSyncExternalStore(subscribeLanguage, currentLanguage, currentLanguage)
  return t
}
