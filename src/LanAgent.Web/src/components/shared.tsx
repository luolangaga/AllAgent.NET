import { type ReactNode, useState } from 'react'
import { Button } from './ui/b-button'
import { Input } from './ui/input'
import { Select, SelectTrigger, SelectValue, SelectContent, SelectItem } from './ui/b-select'
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from './ui/b-dialog'

export function Picker({ value, onChange, options, label, disabled = false }: { value: string; onChange: (value: string) => void; options: { value: string; label: string }[]; label: string; disabled?: boolean }) {
  return <Select value={value} onValueChange={v => onChange(v ?? '')} items={options} disabled={disabled}><SelectTrigger aria-label={label} className="w-full"><SelectValue placeholder={label}>{v => options.find(o => o.value === v)?.label ?? v}</SelectValue></SelectTrigger><SelectContent>{options.map(o => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent></Select>
}
export function Field({ label, value, onChange, type = 'text', required = false }: { label: string; value: string | number; onChange: (value: string) => void; type?: string; required?: boolean }) {
  return <label className="field"><span>{label}</span><Input type={type} value={String(value)} onValueChange={onChange} required={required} /></label>
}
export function Modal({ title, description, children, onClose, footer }: { title: string; description?: string; children: ReactNode; onClose: () => void; footer?: ReactNode }) {
  return <Dialog open onOpenChange={open => { if (!open) onClose() }}><DialogContent className="max-h-[90dvh] overflow-y-auto"><DialogHeader><DialogTitle>{title}</DialogTitle><DialogDescription>{description ?? '修改后保存，立即应用到 LanAgent。'}</DialogDescription></DialogHeader><div className="modal-fields">{children}</div>{footer && <DialogFooter>{footer}</DialogFooter>}</DialogContent></Dialog>
}
export function Confirm({ title, onClose, onConfirm }: { title: string; onClose: () => void; onConfirm: () => Promise<unknown> }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  return <Modal title={title} description="删除后无法恢复，请确认。" onClose={() => { if (!busy) onClose() }} footer={<><Button variant="outline" onClick={onClose} disabled={busy}>取消</Button><Button variant="destructive" loading={busy} onClick={async () => { setBusy(true); try { await onConfirm(); onClose() } catch (e) { setError(e instanceof Error ? e.message : String(e)) } finally { setBusy(false) } }}>删除</Button></>}><p>相关数据将从当前配置中移除。</p>{error && <p role="alert">{error}</p>}</Modal>
}
export function Empty({ title, children }: { title: string; children: ReactNode }) { return <div className="empty-panel"><h3>{title}</h3><p>{children}</p></div> }

