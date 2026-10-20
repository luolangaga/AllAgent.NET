import { invoke } from '@/bridge'
import { renderMarkdown } from '@/lib/markdown'
import { useCommandsStore } from '@/stores/commands'
import { useConversationsStore } from '@/stores/conversations'
import { useNoticesStore } from '@/stores/ui'
import { useWorkspaceStore } from '@/stores/workspace'
import type { NoticeAppearance } from '@/stores/ui'
import type { PluginWebInfo } from '@/types'

// Curated host APIs for plugin frames. Everything not listed here is unreachable — the frame
// protocol only exposes this half of the allowlist, the bridge namespace covers the other half
// (plugin.<packageId>.* registered by the plugin's own backend).
const CAPABILITIES: Record<string, string> = {
  'host.notify': 'notifications',
  'host.storage.get': 'storage',
  'host.storage.set': 'storage',
  'host.storage.remove': 'storage',
  'host.storage.keys': 'storage',
  'host.clipboard.writeText': 'clipboard.write',
  'host.conversation.active': 'conversation.read',
  'host.conversation.messages': 'conversation.read',
  'host.workspace.info': 'workspace.read',
  'host.workspace.files': 'workspace.read',
  'host.pickFolder': 'workspace.pick',
  'host.markdown.render': 'markdown',
  'host.navigate': 'navigation',
  'host.openConversation': 'navigation',
  'host.composer.insertText': 'composer.write',
}

const APPEARANCES = new Set<NoticeAppearance>(['default', 'success', 'info', 'warning', 'destructive'])
const STORAGE_LIMIT = 256 * 1024
const TEXT_LIMIT = 20_000

export type HostApiError = Error & { code: string }

function fail(code: string, message: string): HostApiError {
  return Object.assign(new Error(message), { code })
}

function clip(value: unknown, limit = TEXT_LIMIT): string {
  const text = typeof value === 'string' ? value : value == null ? '' : String(value)
  return text.length > limit ? text.slice(0, limit) : text
}

function storagePrefix(plugin: PluginWebInfo): string {
  return `allagent.plugin.${plugin.packageId.toLowerCase()}.`
}

export async function invokeHostApi(plugin: PluginWebInfo, method: string, params: unknown): Promise<unknown> {
  const capability = CAPABILITIES[method]
  if (!capability) throw fail('unknown_method', '宿主不支持该方法：' + method)
  if (!(plugin.capabilities ?? []).includes(capability)) throw fail('capability_denied', `插件未声明「${capability}」能力，无法调用 ${method}。`)
  const p = (params ?? {}) as Record<string, unknown>
  switch (method) {
    case 'host.notify': {
      const title = clip(p.title, 200).trim()
      if (!title) throw fail('invalid_params', '通知缺少标题。')
      const message = clip(p.message, 4000)
      const appearance = APPEARANCES.has(p.appearance as NoticeAppearance) ? (p.appearance as NoticeAppearance) : 'info'
      useNoticesStore().notify(title, message || undefined, appearance)
      return true
    }
    case 'host.storage.get': {
      return localStorage.getItem(storagePrefix(plugin) + clip(p.key, 200))
    }
    case 'host.storage.set': {
      const key = clip(p.key, 200)
      if (!key) throw fail('invalid_params', '存储键为空。')
      const value = typeof p.value === 'string' ? p.value : JSON.stringify(p.value ?? null)
      if (value.length > STORAGE_LIMIT) throw fail('invalid_params', '存储值超过 256 KB。')
      localStorage.setItem(storagePrefix(plugin) + key, value)
      return true
    }
    case 'host.storage.remove': {
      localStorage.removeItem(storagePrefix(plugin) + clip(p.key, 200))
      return true
    }
    case 'host.storage.keys': {
      const prefix = storagePrefix(plugin)
      const keys: string[] = []
      for (let index = 0; index < localStorage.length; index++) {
        const item = localStorage.key(index)
        if (item?.startsWith(prefix)) keys.push(item.slice(prefix.length))
      }
      return keys
    }
    case 'host.clipboard.writeText': {
      const text = clip(p.text)
      if (!text) throw fail('invalid_params', '剪贴板内容为空。')
      await navigator.clipboard.writeText(text)
      return true
    }
    case 'host.conversation.active': {
      const conversations = useConversationsStore()
      const active = conversations.active
      if (!active) return null
      return { id: active.id, title: active.title, modelId: active.modelId, providerId: active.providerId, messageCount: conversations.messages.length }
    }
    case 'host.conversation.messages': {
      const conversations = useConversationsStore()
      const limit = Math.max(1, Math.min(Number(p.limit ?? 50) || 50, 200))
      return conversations.messages.slice(-limit).map(message => ({
        id: message.id,
        role: message.role,
        content: clip(message.content, 8000),
        createdAt: message.createdAt,
      }))
    }
    case 'host.workspace.info': {
      const workspace = useWorkspaceStore()
      return { workspace: workspace.workspace, dataDirectory: workspace.dataDirectory, sharedDirectory: workspace.sharedDirectory }
    }
    case 'host.workspace.files': {
      const query = clip(p.query, 400)
      const limit = Math.max(1, Math.min(Number(p.limit ?? 40) || 40, 100))
      return invoke('workspace.files', { query, limit })
    }
    case 'host.pickFolder': {
      // Non-blocking: the native dialog's result arrives as a workspace.folderPicked event,
      // which the frame runtime forwards to subscribers. The SDK invoke timeout therefore
      // never cuts a slow browse short.
      await invoke('workspace.pickFolder')
      return true
    }
    case 'host.markdown.render': {
      const text = clip(p.text, 200_000)
      return text.trim() ? renderMarkdown(text) : ''
    }
    case 'host.navigate': {
      const page = clip(p.page, 200).trim()
      if (!/^[A-Za-z0-9:._-]{1,200}$/.test(page)) throw fail('invalid_params', '页面 ID 无效。')
      if (location.hash !== `#/${page}`) location.hash = `/${page}`
      return true
    }
    case 'host.openConversation': {
      const id = clip(p.id, 80).trim()
      if (!id) throw fail('invalid_params', '缺少会话 ID。')
      const workspace = clip(p.workspace, 2000).trim()
      if (workspace && workspace !== useWorkspaceStore().workspace) {
        if (!plugin.capabilities.includes('workspace.switch')) throw fail('capability_denied', '插件未声明 workspace.switch 能力。')
        await useWorkspaceStore().switchTo(workspace)
      }
      await useConversationsStore().loadAll()
      await useConversationsStore().selectConversation(id)
      if (location.hash !== '#/chat') location.hash = '/chat'
      return true
    }
    case 'host.composer.insertText': {
      const text = clip(p.text)
      if (!text.trim()) throw fail('invalid_params', '缺少文本。')
      useCommandsStore().queueDraft(text)
      if (location.hash !== '#/chat') location.hash = '/chat'
      return true
    }
    default:
      throw fail('unknown_method', '宿主不支持该方法：' + method)
  }
}
