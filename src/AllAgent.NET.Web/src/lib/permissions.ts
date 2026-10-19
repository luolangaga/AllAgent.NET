/** The host's permission modes, in the order every picker presents them. */
export interface PermissionOption {
  value: string
  label: string
  hint: string
}

export const PERMISSION_OPTIONS: PermissionOption[] = [
  { value: 'restricted', label: '限制模式', hint: '工作区内读写免审，工作区外需授权' },
  { value: 'ask', label: '逐次授权', hint: '写文件与执行前逐次询问' },
  { value: 'full', label: '完全访问', hint: '不逐次审核，可访问工作区外' },
  { value: 'plan', label: '只读规划', hint: '只读，不写入不执行' },
  { value: 'simple', label: '简单模式', hint: '仅保留 PowerShell、读取与编辑文件三个工具' },
]

/** Label for a mode key; unknown keys keep the host's own label. */
export function permissionLabel(mode: string | undefined, fallback?: string): string {
  return PERMISSION_OPTIONS.find(option => option.value === mode)?.label || fallback || mode || '权限模式'
}

export function permissionHint(mode: string | undefined): string {
  return PERMISSION_OPTIONS.find(option => option.value === mode)?.hint ?? ''
}
