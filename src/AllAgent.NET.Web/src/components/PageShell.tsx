import { type ReactNode } from 'react'

export function PageShell({ eyebrow, title, description, action, children }: {
  eyebrow: string
  title: string
  description: string
  action?: ReactNode
  children: ReactNode
}) {
  return <section className="page">
    <header className="page-header">
      <div className="page-heading">
        <span className="eyebrow">{eyebrow}</span>
        <h1>{title}</h1>
        <p>{description}</p>
      </div>
      {action ? <div className="page-action">{action}</div> : null}
    </header>
    <div className="page-body">{children}</div>
  </section>
}

export function EmptyState({ title, children }: { title: string; children: ReactNode }) {
  return <div className="empty-state">
    <h3>{title}</h3>
    <p>{children}</p>
  </div>
}
