import { memo, useMemo } from 'react'
import { marked } from 'marked'
import hljs from 'highlight.js/lib/common'
import DOMPurify from 'dompurify'
import { cn } from '@/lib/utils'

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
  },
})

export const Markdown = memo(function Markdown({ content, className }: { content: string; className?: string }) {
  const html = useMemo(() => DOMPurify.sanitize(marked.parse(content) as string), [content])
  return <div className={cn('markdown', className)} dangerouslySetInnerHTML={{ __html: html }} />
})
