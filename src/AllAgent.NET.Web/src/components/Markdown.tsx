import { memo, type MouseEvent } from 'react'
import { cn } from '@/lib/utils'
import { isFilePath, openReference } from '@/lib/fileLinks'
import { renderMarkdown } from '@/lib/markdown'

// A path has no route on the host's loopback server, so a click hands it to Explorer
// instead of navigating the page away to a 404.
function openClicked(event: MouseEvent<HTMLDivElement>) {
  const element = event.target as HTMLElement
  const link = element.closest('a')
  if (link) {
    const target = (link.getAttribute('data-ref') ?? link.getAttribute('href') ?? '').trim()
    if (target && openReference(target)) event.preventDefault()
    return
  }
  const code = element.closest('code')
  const text = code?.textContent?.trim() ?? ''
  if (code && isFilePath(text)) {
    event.preventDefault()
    openReference(text)
  }
}

export const Markdown = memo(function Markdown({ content, className }: { content: string; className?: string }) {
  return <div className={cn('markdown', className)} dangerouslySetInnerHTML={{ __html: renderMarkdown(content) }} onClick={openClicked} />
})
