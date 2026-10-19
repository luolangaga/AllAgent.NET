import { memo, useMemo, type MouseEvent } from 'react'
import { marked } from 'marked'
import hljs from 'highlight.js/lib/common'
import DOMPurify from 'dompurify'
import { cn } from '@/lib/utils'
import { isFilePath, openReference } from '@/lib/fileLinks'

marked.use({
  gfm: true,
  breaks: true,
  renderer: {
    code(code, info) {
      const language = (info ?? '').split(' ')[0]
      const safeLanguage = /^[\w-]+$/.test(language) ? language : 'plaintext'
      const highlighted = hljs.highlight(code, { language: hljs.getLanguage(safeLanguage) ? safeLanguage : 'plaintext' }).value
      return `<pre><code class="hljs language-${safeLanguage}">${highlighted}</code></pre>`
    },
    // Answers name files as inline code far more often than as links; marking the ones the
    // host can open keeps the affordance visible before anything is clicked.
    codespan(text) {
      return isFilePath(text) ? `<code class="file-ref">${text}</code>` : `<code>${text}</code>`
    },
    // A path is not a URL, and the sanitiser drops href values that look like one. The raw
    // target therefore rides along in a data attribute the click handler reads.
    link(href, title, text) {
      const target = attribute(href)
      return `<a href="${target}"${title ? ` title="${attribute(title)}"` : ''} data-ref="${target}">${text}</a>`
    },
  },
})

function attribute(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

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
  const html = useMemo(() => DOMPurify.sanitize(marked.parse(content) as string), [content])
  return <div className={cn('markdown', className)} dangerouslySetInnerHTML={{ __html: html }} onClick={openClicked} />
})
