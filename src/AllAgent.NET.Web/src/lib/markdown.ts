import { marked } from 'marked'
import hljs from 'highlight.js/lib/common'
import DOMPurify from 'dompurify'
import { isFilePath } from '@/lib/fileLinks'

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

/** Sanitized GitHub-flavoured Markdown → HTML. Shared by chat and the plugin host API. */
export function renderMarkdown(content: string): string {
  return DOMPurify.sanitize(marked.parse(content ?? '') as string)
}
