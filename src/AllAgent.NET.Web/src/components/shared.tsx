import { useState, type ReactNode } from 'react'
import { Button } from './ui/b-button'
import { Input } from './ui/input'
import { Select, SelectTrigger, SelectValue, SelectContent, SelectItem } from './ui/b-select'
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from './ui/b-dialog'
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent,
  AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from './ui/b-alert-dialog'

export interface SelectOption { value: string; label: string }

export function Picker({ value, onChange, options, label, disabled = false }: {
  value: string
  onChange: (value: string) => void
  options: SelectOption[]
  label: string
  disabled?: boolean
}) {
  return <Select value={value} onValueChange={v => onChange(v ?? '')} items={options} disabled={disabled}>
    <SelectTrigger aria-label={label} className="w-full">
      <SelectValue placeholder={label}>{v => options.find(option => option.value === v)?.label ?? v}</SelectValue>
    </SelectTrigger>
    <SelectContent>{options.map(option => <SelectItem key={option.value} value={option.value}>{option.label}</SelectItem>)}</SelectContent>
  </Select>
}

export function Field({ label, value, onChange, type = 'text', required = false, placeholder, description }: {
  label: string
  value: string | number
  onChange: (value: string) => void
  type?: string
  required?: boolean
  placeholder?: string
  description?: string
}) {
  return <Input
    label={label}
    type={type}
    value={String(value)}
    onValueChange={onChange}
    required={required}
    placeholder={placeholder}
    description={description}
    wrapperClassName="w-full"
  />
}

export function Modal({ title, description, children, onClose, footer, size = 'default' }: {
  title: string
  description?: string
  children: ReactNode
  onClose: () => void
  footer?: ReactNode
  size?: 'sm' | 'default' | 'lg' | 'full'
}) {
  return <Dialog open onOpenChange={open => { if (!open) onClose() }}>
    <DialogContent size={size} className="gap-4">
      <DialogHeader>
        <DialogTitle>{title}</DialogTitle>
        <DialogDescription>{description ?? '修改后保存，立即生效。'}</DialogDescription>
      </DialogHeader>
      <div className="modal-fields">{children}</div>
      {footer ? <DialogFooter>{footer}</DialogFooter> : null}
    </DialogContent>
  </Dialog>
}

export function Confirm({ title, description = '删除后无法恢复，请确认。', confirmLabel = '删除', onClose, onConfirm }: {
  title: string
  description?: string
  confirmLabel?: string
  onClose: () => void
  onConfirm: () => Promise<unknown>
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  return <AlertDialog open onOpenChange={open => { if (!open && !busy) onClose() }}>
    <AlertDialogContent>
      <AlertDialogHeader>
        <AlertDialogTitle>{title}</AlertDialogTitle>
        <AlertDialogDescription>{description}</AlertDialogDescription>
      </AlertDialogHeader>
      {error ? <p className="text-destructive text-sm" role="alert">{error}</p> : null}
      <AlertDialogFooter>
        <AlertDialogCancel disabled={busy}>取消</AlertDialogCancel>
        <AlertDialogAction
          closeOnClick={false}
          disabled={busy}
          onClick={async () => {
            setBusy(true)
            setError('')
            try {
              await onConfirm()
              onClose()
            } catch (cause) {
              setError(cause instanceof Error ? cause.message : String(cause))
            } finally {
              setBusy(false)
            }
          }}
        >
          {busy ? '处理中…' : confirmLabel}
        </AlertDialogAction>
      </AlertDialogFooter>
    </AlertDialogContent>
  </AlertDialog>
}

export function TextArea({ label, value, onChange, rows = 6, placeholder, description }: {
  label: string
  value: string
  onChange: (value: string) => void
  rows?: number
  placeholder?: string
  description?: string
}) {
  return <label className="field">
    <span>{label}</span>
    <textarea className="text-area" rows={rows} value={value} placeholder={placeholder} spellCheck={false} onChange={event => onChange(event.target.value)} />
    {description ? <small className="field-hint">{description}</small> : null}
  </label>
}

export function ToolbarButton({ children, ...props }: React.ComponentProps<typeof Button>) {
  return <Button {...props}>{children}</Button>
}
