<template>
  <section class="execution-steps" aria-label="执行步骤">
    <div class="execution-heading">执行步骤 · {{ operations.length }} 次操作 <span v-if="running">进行中</span></div>
    <ToolCallCard v-for="call in operations" :key="call.id" :tool-call="call" />
    <details v-for="call in codeCalls" :key="call.id" class="code-details">
      <summary>{{ call.status === 'error' ? '执行出错 · 查看原因' : operations.some(t => t.parentId === call.id) ? '查看调度代码与输出' : '计算与处理 · 查看详情' }}</summary>
      <ToolCallCard :tool-call="call" :expanded="true" />
    </details>
  </section>
</template>
<script setup lang="ts">
import { computed } from 'vue'
import type { ToolCall } from '@/types'
import ToolCallCard from './ToolCallCard.vue'
const props = defineProps<{ calls: ToolCall[] }>()
const operations = computed(() => props.calls.filter(c => c.name !== 'execute_code'))
const codeCalls = computed(() => props.calls.filter(c => c.name === 'execute_code'))
const running = computed(() => props.calls.some(c => c.status === 'running'))
</script>
<style scoped>
.execution-steps { margin-bottom: 16px; min-width: 0; }
.execution-heading { color: var(--color-text-secondary); font-size: 12px; margin-bottom: 8px; display: flex; justify-content: space-between; }
.code-details { margin-top: 8px; font-size: 12px; color: var(--color-text-secondary); }
summary { cursor: pointer; padding: 6px 0; }
</style>
