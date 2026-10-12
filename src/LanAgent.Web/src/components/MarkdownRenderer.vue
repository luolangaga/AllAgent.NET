<template>
  <div class="markdown-body" ref="container" v-html="rendered" @click="handleClick" />
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { marked } from 'marked'
import { markedHighlight } from 'marked-highlight'
import hljs from 'highlight.js'
import DOMPurify from 'dompurify'

marked.use(markedHighlight({
  langPrefix: 'hljs language-',
  highlight(code, lang) {
    const language = hljs.getLanguage(lang) ? lang : 'plaintext'
    return hljs.highlight(code, { language }).value
  }
}))

marked.use({
  breaks: true,
  gfm: true,
  renderer: {
    code(code: string, infostring: string | undefined) {
      const lang = infostring || 'plaintext'
      const highlighted = hljs.getLanguage(lang)
        ? hljs.highlight(code, { language: lang }).value
        : code.replace(/&/g, '&amp;').replace(/</g, '&lt;')
      // Use data attributes instead of inline onclick for security
      const escapedCode = code.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
      return `<div class="code-block">
        <div class="code-block-header">
          <span class="code-lang">${lang}</span>
          <button class="copy-btn" data-copy="${escapedCode}">
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none"><rect x="4" y="4" width="7" height="7" rx="1" stroke="currentColor" stroke-width="1.2"/><path d="M1 8V1h7" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/></svg>
            复制
          </button>
        </div>
        <pre><code class="hljs language-${lang}">${highlighted}</code></pre>
      </div>`
    }
  }
})

const props = defineProps<{ content: string }>()
const container = ref<HTMLElement>()

const rendered = computed(() => {
  const html = marked.parse(props.content) as string
  return DOMPurify.sanitize(html, { ADD_ATTR: ['data-copy'] })
})

function handleClick(e: MouseEvent) {
  const btn = (e.target as HTMLElement).closest('.copy-btn') as HTMLElement | null
  if (btn?.dataset.copy) {
    navigator.clipboard.writeText(btn.dataset.copy)
    btn.textContent = '已复制'
    setTimeout(() => { btn.textContent = '复制' }, 1500)
  }
}
</script>
