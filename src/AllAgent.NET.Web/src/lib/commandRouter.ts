import { invoke } from '@/bridge'
import { permissionOptions } from '@/lib/permissions'
import { useApiConfigsStore } from '@/stores/apiConfigs'
import { useAuthStore } from '@/stores/auth'
import { useCommandsStore } from '@/stores/commands'
import { useConversationsStore } from '@/stores/conversations'
import { usePanelsStore } from '@/stores/panels'
import { usePluginsStore } from '@/stores/plugins'
import { useSettingsStore } from '@/stores/settings'
import { useNoticesStore } from '@/stores/ui'
import { useWorkspaceStore } from '@/stores/workspace'
import type { CommandInfo } from '@/types'

export interface CommandHost {
  go: (page: string) => void
  openPalette: () => void
}

/** Browser-only bubbles surfaced in the command palette next to the host's catalog. */
export const WEB_COMMANDS: CommandInfo[] = [
  { text: '/files', description: '打开工作区文件面板 · 只读预览与引用到对话', aliases: 'Ctrl+Shift+E' },
  { text: '/git', description: '打开 Git 面板 · 更改、历史、分支与贮藏', aliases: 'Ctrl+Shift+G' },
  { text: '/terminal', description: '打开集成终端 · ConPTY 多标签', aliases: 'Ctrl+`' },
]

/**
 * Composer state a command may need. /compact has to run on the model the composer
 * currently targets, not just the default provider.
 */
export interface CommandContext {
  apiConfigId?: string | null
}

// Commands that need terminal-only state or UI (message timeline, checkpoints, diff viewer,
// inbox steering…). They stay visible in the palette but are labelled.
const TERMINAL_ONLY = new Set([
  '/timeline', '/fork', '/branch', '/history', '/copy', '/undo', '/redo', '/checkpoints', '/diff',
  '/context', '/stats', '/memory', '/init', '/doctor', '/computer',
  '/background', '/subagents', '/agents', '/queue', '/steer', '/parent', '/children', '/exit',
  '/quit', '/questions', '/cache',
])

export function isTerminalOnly(text: string): boolean {
  return TERMINAL_ONLY.has(text.trim().split(/\s+/)[0]?.toLowerCase() ?? '')
}

export interface CommandGroup {
  id: 'command' | 'plugin' | 'skill'
  title: string
  items: CommandInfo[]
}

/** Splits an already-filtered catalog into the terminal hint panel's sections:
 * built-in commands, plugin commands and user-invocable skills (empty sections drop out). */
export function groupCommands(commands: CommandInfo[]): CommandGroup[] {
  const groups: CommandGroup[] = [
    { id: 'command', title: '命令', items: [] },
    { id: 'plugin', title: '插件', items: [] },
    { id: 'skill', title: '技能', items: [] },
  ]
  for (const item of commands) {
    const group = item.isSkill ? groups[2] : item.text.startsWith('/ext ') ? groups[1] : groups[0]
    group.items.push(item)
  }
  return groups.filter(group => group.items.length > 0)
}

/** Commands matching a partial slash input: exact first, then text prefix, then aliases. */
export function matchCommands(commands: CommandInfo[], input: string): CommandInfo[] {
  const needle = input.trim().toLowerCase()
  if (!needle) return commands
  const matches: CommandInfo[] = []
  const seen = new Set<string>()
  const push = (item: CommandInfo) => { if (!seen.has(item.text)) { seen.add(item.text); matches.push(item) } }
  commands.filter(item => item.text.toLowerCase() === needle).forEach(push)
  commands.filter(item => item.text.toLowerCase().startsWith(needle)).forEach(push)
  commands.filter(item => (item.aliases ?? '').split(/\s+/).some(alias => alias.toLowerCase().startsWith(needle))).forEach(push)
  return matches
}

/** Runs a slash command entered in the composer or the palette. Returns true when the input
 * was consumed; false lets it fall through as a normal message (user-invocable skills). */
export async function runCommand(raw: string, host: CommandHost, context: CommandContext = {}): Promise<boolean> {
  const notices = useNoticesStore()
  const words = splitWords(raw)
  const name = (words[0] ?? '').toLowerCase()
  const rest = words.slice(1)
  try {
    switch (name) {
      case '/help': host.openPalette(); return true
      case '/new': case '/clear': {
        const conversations = useConversationsStore()
        await conversations.createConversation()
        host.go('chat')
        notices.notify('已创建新对话', undefined, 'success')
        return true
      }
      case '/session': case '/sessions': case '/resume':
        host.go('chat')
        notices.notify('会话列表在左侧栏', '点击历史对话即可恢复；Ctrl K 可搜索会话。')
        return true
      case '/rename': {
        const conversations = useConversationsStore()
        if (!conversations.activeId) throw new Error('当前没有对话。')
        if (!rest.length) throw new Error('用法：/rename 新标题')
        await conversations.renameConversation(conversations.activeId, rest.join(' '))
        notices.notify('对话已重命名', undefined, 'success')
        return true
      }
      case '/workspace':
        useWorkspaceStore().openSwitcher()
        return true
      case '/export':
        notices.notify('已导出 Markdown', await exportConversation(), 'success')
        return true
      case '/todo':
        host.go('chat')
        notices.notify('TODO 清单', 'TODO 由模型在任务中维护，可在对话页查看进度。')
        return true
      case '/compact': {
        // Same model-side compaction as the terminal's /compact: older history is
        // summarised into a continuation summary, the visible transcript is untouched.
        const conversations = useConversationsStore()
        if (!conversations.activeId) throw new Error('当前没有对话，无法整理上下文。')
        const enabled = useApiConfigsStore().configs.filter(item => item.isEnabled)
        // The composer's selection wins even before the catalog finished loading.
        const config = (context.apiConfigId ? { id: context.apiConfigId } : undefined)
          ?? enabled.find(item => item.id === useSettingsStore().defaultProviderId)
          ?? enabled[0]
        if (!config) throw new Error('先连接一个可用的模型配置，再整理上下文。')
        await conversations.compact(config.id, rest.join(' '))
        notices.notify('正在整理上下文', '模型会总结较早的历史；完整消息仍保留在对话中。')
        return true
      }
      case '/skills': host.go('skills'); return true
      case '/plugins': host.go('plugins'); return true
      // The three bubbles are browser-only; the terminal's own catalog has no such commands.
      case '/files': case '/file':
        usePanelsStore().openPanel('files')
        return true
      case '/git':
        usePanelsStore().openPanel('git')
        return true
      case '/terminal': case '/term':
        usePanelsStore().openPanel('terminal')
        return true
      case '/powershell': {
        // The store, search and install live on the PowerShell page; the page and the terminal's
        // /powershell panel share one module directory.
        host.go('powershell')
        if (words.length > 1) notices.notify('PowerShell 组件', '在组件页搜索或按名称安装模块；安装由宿主完成导入验证。')
        return true
      }
      case '/mcp': host.go('mcp'); return true
      case '/tasks': host.go('tasks'); return true
      case '/tools': host.go('tools'); return true
      case '/models': case '/providers': case '/config': host.go('models'); return true
      case '/settings': case '/spinner': host.go('settings'); return true
      case '/themes': {
        const settings = useSettingsStore()
        const next = settings.theme === 'light' ? 'dark' : settings.theme === 'dark' ? 'system' : 'light'
        settings.theme = next
        settings.applyTheme(next)
        await settings.save()
        notices.notify('主题已切换', next, 'success')
        return true
      }
      case '/thinking':
        notices.notify('思考等级', '在输入框的「思考等级」中选择标准回答、深度思考或具体等级；按消息生效。')
        return true
      case '/details':
        notices.notify('工具详情', '点击对话中的工具卡片即可展开参数与输出。')
        return true
      case '/model':
        notices.notify('切换模型', '使用输入框的「模型」菜单切换下一个任务使用的模型；配置管理在「模型与 API」。')
        return true
      case '/effort':
        notices.notify('思考强度', '在输入框的「思考等级」中选择等级；预算由模型配置决定。')
        return true
      case '/permissions': {
        const workspace = useWorkspaceStore()
        if (!workspace.available) throw new Error('此宿主不支持权限切换。')
        const mode = rest[0] ?? ''
        if (!mode) { notices.notify('当前权限', workspace.controls?.permissionLabel ?? '未知'); return true }
        if (!permissionOptions().some(option => option.value === mode)) throw new Error(`权限模式：${permissionOptions().map(option => option.value).join(' / ')}。`)
        await workspace.setPermission(mode)
        notices.notify('权限已切换', workspace.controls?.permissionLabel ?? mode, 'success')
        return true
      }
      case '/plan': {
        const workspace = useWorkspaceStore()
        const next = workspace.controls?.permissionMode === 'plan' ? 'restricted' : 'plan'
        await workspace.setPermission(next)
        notices.notify('权限已切换', workspace.controls?.permissionLabel ?? next, 'success')
        return true
      }
      case '/simple': {
        const workspace = useWorkspaceStore()
        const next = workspace.controls?.permissionMode === 'simple' ? 'restricted' : 'simple'
        await workspace.setPermission(next)
        notices.notify('权限已切换', workspace.controls?.permissionLabel ?? next, 'success')
        return true
      }
      case '/sandbox': {
        const workspace = useWorkspaceStore()
        const mode = rest[0] ?? ''
        if (!mode) { notices.notify('沙箱状态', workspace.controls?.sandboxEffective ?? '未知'); return true }
        if (!['auto', 'hyperlight', 'monty'].includes(mode)) throw new Error('沙箱模式：auto / hyperlight / monty。')
        await workspace.setSandbox(mode)
        notices.notify('沙箱已切换', workspace.controls?.sandboxEffective ?? mode, 'success')
        return true
      }
      case '/hooks': {
        const action = rest[0] ?? ''
        if (!action || action === 'list') { host.go('plugins'); return true }
        if (action !== 'enable' && action !== 'disable') throw new Error('用法：/hooks enable|disable 名称')
        if (!rest[1]) throw new Error('请提供 Hook 名称。')
        await usePluginsStore().setHook(rest[1], action === 'enable')
        notices.notify('Hook 已更新', `${rest[1]} · ${action === 'enable' ? '启用' : '禁用'}`, 'success')
        return true
      }
      case '/ext': {
        if (!rest.length) { host.go('plugins'); notices.notify('插件命令', '用法：/ext 命令名 [参数]；已启用插件的命令会出现在命令面板中。'); return true }
        const output = await invoke<string>('plugins.runCommand', { name: rest[0], args: rest.slice(1) })
        notices.notify(`插件命令 ${rest[0]}`, output.length > 400 ? `${output.slice(0, 400)}…` : output, 'success')
        return true
      }
      case '/login': {
        const auth = useAuthStore()
        if (rest.length) {
          await auth.login(rest[0])
          notices.notify('已开始登录', '如已打开浏览器请完成授权；完成后状态会自动刷新。')
        } else host.go('models')
        return true
      }
      case '/logout': {
        const auth = useAuthStore()
        if (rest.length) {
          await auth.logout(rest[0])
          notices.notify('已退出登录', undefined, 'success')
        } else host.go('models')
        return true
      }
      case '/web-ui':
        notices.notify('Web 工作台', '你正在 Web 工作台中；地址与连接数可在终端用 /web-ui status 查看。')
        return true
      case '/browser': {
        if (rest[0] !== 'on' && rest[0] !== 'off') { host.go('settings'); return true }
        const settings = useSettingsStore()
        settings.browserUseEnabled = rest[0] === 'on'
        await settings.save()
        notices.notify(`浏览器操作已${rest[0] === 'on' ? '开启' : '关闭'}`, undefined, 'success')
        return true
      }
      default:
        break
    }
    if (isTerminalOnly(name)) {
      notices.notify('该命令目前仅在终端可用', `${name} · 命令面板中已标注「终端专属」。`)
      return true
    }
    // Slash text that matches a user-invocable skill becomes a task, like the terminal's default branch.
    const commands = useCommandsStore()
    if (commands.commands.some(item => item.isSkill && item.text.toLowerCase() === name)) return false
    notices.notify('未知命令', `没有找到 ${name}；输入 /help 打开命令面板。`)
    return true
  } catch (error) {
    notices.report(error)
    return true
  }
}

async function exportConversation(): Promise<string> {
  const conversations = useConversationsStore()
  if (!conversations.activeId) throw new Error('当前没有可导出的对话。')
  const markdown = await invoke<string>('conversations.export', { id: conversations.activeId })
  const title = (conversations.active?.title ?? 'conversation').replace(/[\\/:*?"<>|]/g, '_').trim() || 'conversation'
  const blob = new Blob([markdown], { type: 'text/markdown;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = `${title}.md`
  anchor.click()
  URL.revokeObjectURL(url)
  return `${title}.md`
}

/** Quote-aware word splitting, mirroring the terminal's CommandLineWords parser. */
function splitWords(text: string): string[] {
  const words: string[] = []
  const pattern = /"([^"]*)"|'([^']*)'|(\S+)/g
  for (const match of text.matchAll(pattern)) words.push(match[1] ?? match[2] ?? match[3] ?? '')
  return words
}
