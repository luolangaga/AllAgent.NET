import { t } from '@/i18n'

/** The host's permission modes, in the order every picker presents them. */
export interface PermissionOption {
  value: string
  label: string
  hint: string
}

export function permissionOptions(): PermissionOption[] {
  return [
    { value: 'restricted', label: t('限制模式', 'Restricted'), hint: t('工作区内读写免审，工作区外需授权', 'Workspace reads/writes run freely; outside paths need approval') },
    { value: 'ask', label: t('逐次授权', 'Ask each time'), hint: t('写文件与执行前逐次询问', 'Ask before writes and execution') },
    { value: 'full', label: t('完全访问', 'Full access'), hint: t('不逐次审核，可访问工作区外', 'No per-call approval; outside the workspace allowed') },
    { value: 'plan', label: t('计划模式', 'Plan mode'), hint: t('只读调研 → 生成计划 → 批准后自动执行', 'Read-only research → plan → execute after approval') },
    { value: 'simple', label: t('简单模式', 'Simple mode'), hint: t('仅保留 PowerShell、读取与编辑文件三个工具', 'Only PowerShell, read file and edit file') },
  ]
}

/** Label for a mode key; unknown keys keep the host's own label. */
export function permissionLabel(mode: string | undefined, fallback?: string): string {
  return permissionOptions().find(option => option.value === mode)?.label || fallback || mode || t('权限模式', 'Permission mode')
}

export function permissionHint(mode: string | undefined): string {
  return permissionOptions().find(option => option.value === mode)?.hint ?? ''
}
