<template>
  <div class="message-bubble" :class="`role-${message.role}`">
    <div class="bubble-avatar">
      <span v-if="message.role === 'user'">👤</span>
      <span v-else>🤖</span>
    </div>
    <div class="bubble-body">
      <details v-if="message.reasoningContent" class="reasoning"><summary>思考过程</summary><MarkdownRenderer :content="message.reasoningContent" /></details>
      <ExecutionSteps v-if="message.toolCalls?.length" :calls="message.toolCalls" />
      <MarkdownRenderer v-if="message.role === 'assistant'" :content="message.content" />
      <span v-else>{{ message.content }}</span>
      <span v-if="streaming" class="cursor-blink" />
    </div>
  </div>
</template>

<script setup lang="ts">
import type { Message } from '@/types'
import ExecutionSteps from './ExecutionSteps.vue'
import MarkdownRenderer from './MarkdownRenderer.vue'

defineProps<{ message: Message; streaming?: boolean }>()
</script>
