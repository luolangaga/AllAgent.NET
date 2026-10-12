<template>
  <article class="operation" :class="{ delegated: isAgent, failed }">
    <button class="operation-header" :aria-expanded="open" @click="open = !open">
      <span class="icon">{{ isAgent ? '↗' : isShell ? '>_' : '→' }}</span>
      <span class="title"><strong>{{ title }}</strong><span class="subtitle">{{ subtitle }}</span></span>
      <span v-if="diff" class="counts"><span>+{{ diff.added }}</span> <span>−{{ diff.removed }}</span></span>
      <span class="state">{{ statusLabel }}</span><span>{{ open ? '⌄' : '›' }}</span>
    </button>
    <div v-if="isAgent && task" class="progress">{{ task.currentStep ?? task.title }}</div>
    <div v-if="open" class="body">
      <template v-if="taskId">
        <p v-if="taskError" class="error" role="alert">{{ taskError }}</p>
        <p v-if="task">{{ task.title }}<br><small>任务 {{ task.id }} · {{ statusLabel }}</small></p>
        <ol v-if="task?.steps?.length" class="steps"><li v-for="(step, i) in task.steps" :key="i">{{ step }}</li></ol>
        <div v-if="task?.output" class="output"><pre v-if="isShell">{{ task.output }}</pre><MarkdownRenderer v-else :content="task.output" /></div>
        <p v-if="task?.error" class="error">{{ task.error }}</p>
        <div class="actions"><button @click="refreshTask">刷新详情</button><button v-if="running" @click="cancelTask">停止任务</button></div>
      </template>
      <div v-if="diff" class="diff" aria-label="文件变更">
        <div v-for="(line, i) in diff.lines" :key="i" class="diff-line" :class="line.kind"><span>{{ line.oldLine ?? '' }}</span><span>{{ line.newLine ?? '' }}</span><code>{{ line.kind === 'added' ? '+' : line.kind === 'removed' ? '−' : ' ' }} {{ line.text }}</code></div>
      </div>
      <p v-if="diff?.truncated || result.truncated" class="hint">内容较长，预览已截断。</p>
      <p v-if="diff?.replacement" class="hint">变更范围较大，展示整个替换区域。</p>
      <pre v-if="isShell && !taskId" class="output">{{ shellOutput }}</pre>
      <pre v-if="toolCall.name === 'execute_code'" class="output">{{ args.code }}</pre>
      <pre v-if="toolCall.outputLines?.length" class="output">{{ toolCall.outputLines.join('\n') }}</pre>
      <p v-if="result.error" class="error">{{ result.error }}</p>
      <pre v-if="!diff && !isShell && !taskId && toolCall.output" class="output">{{ readableOutput }}</pre>
      <details v-if="toolCall.name !== 'execute_code'" class="raw"><summary>原始参数</summary><pre class="output">{{ JSON.stringify(args, null, 2) }}</pre></details>
    </div>
  </article>
</template>
<script setup lang="ts">
import { computed, ref, watch, onBeforeUnmount } from 'vue'
import type { ToolCall } from '@/types'
import { invoke } from '@/bridge'
import { parseObject } from '@/utils/execution'
import MarkdownRenderer from './MarkdownRenderer.vue'
interface TaskInfo { id: string; title: string; state: number | string; currentStep?: string; steps?: string[]; output: string; error?: string }
const props = defineProps<{ toolCall: ToolCall; expanded?: boolean }>()
const open = ref(props.expanded ?? false)
const args = computed(() => parseObject(props.toolCall.args))
const result = computed(() => parseObject(props.toolCall.output))
const data = computed(() => result.value.data ?? {})
const diff = computed(() => data.value.diff)
const isAgent = computed(() => ['start_agent', 'agent_status', 'wait_agent', 'cancel_agent'].includes(props.toolCall.name))
const isShell = computed(() => ['run_shell', 'shell_status', 'cancel_shell'].includes(props.toolCall.name))
const taskId = computed<string | undefined>(() => data.value.taskId ?? ((isAgent.value || ['shell_status', 'cancel_shell'].includes(props.toolCall.name)) ? args.value.id : undefined))
const task = ref<TaskInfo>()
const taskError = ref('')
const states = ['Queued', 'Running', 'AwaitingApproval', 'AwaitingAnswer', 'Completed', 'Failed', 'Cancelled', 'Interrupted']
const state = computed(() => typeof task.value?.state === 'number' ? states[task.value.state] : task.value?.state)
const running = computed(() => task.value ? ['Queued', 'Running', 'AwaitingApproval', 'AwaitingAnswer'].includes(state.value ?? '') : props.toolCall.status === 'running' || !!taskId.value)
const failed = computed(() => props.toolCall.status === 'error' || ['Failed', 'Interrupted'].includes(state.value ?? ''))
const labels: Record<string, string> = { Queued: '排队中', Running: '运行中', AwaitingApproval: '等待授权', AwaitingAnswer: '等待回答', Completed: '已完成', Failed: '失败', Cancelled: '已停止', Interrupted: '已中断' }
const statusLabel = computed(() => labels[state.value ?? ''] ?? (failed.value ? '失败' : running.value ? '执行中' : '已完成'))
const titles: Record<string, string> = { file_read: '读取文件', file_write: '写入文件', file_edit: '修改文件', list_directory: '浏览目录', run_shell: '运行命令', start_agent: '子代理任务', agent_status: '子代理进度', wait_agent: '等待子代理', cancel_agent: '停止子代理', execute_code: '执行详情', shell_status: '后台命令进度', cancel_shell: '停止后台命令' }
const title = computed(() => props.toolCall.name === 'run_shell' && args.value.background ? '后台命令' : titles[props.toolCall.name] ?? props.toolCall.name)
const command = computed(() => [args.value.exe, ...(args.value.args ?? [])].filter(Boolean).join(' '))
const subtitle = computed(() => args.value.path ?? (isAgent.value ? args.value.task ?? task.value?.title ?? args.value.id : isShell.value ? command.value || args.value.id : ''))
const shellOutput = computed(() => [command.value, data.value.stdout, data.value.stderr ? '[stderr] ' + data.value.stderr : '', data.value.exitCode != null ? '退出码 ' + data.value.exitCode : ''].filter(Boolean).join('\n'))
const readableOutput = computed(() => props.toolCall.output && Object.keys(result.value).length ? JSON.stringify(data.value, null, 2) : props.toolCall.output)
let timer: ReturnType<typeof setTimeout> | undefined
let disposed = false
async function refreshTask() {
  const id = taskId.value
  if (!id || disposed) return
  try { const info = await invoke<TaskInfo>('tasks.get', { id }); if (!disposed && taskId.value === id) { task.value = info; taskError.value = '' } }
  catch (error) { if (!disposed) taskError.value = `无法获取任务详情：${String(error)}` }
}
async function poll() {
  await refreshTask()
  if (!disposed && !taskError.value && taskId.value && (!task.value || running.value)) timer = setTimeout(poll, 2000)
}
watch(taskId, () => { clearTimeout(timer); task.value = data.value.id ? data.value as TaskInfo : undefined; if (taskId.value) void poll() }, { immediate: true })
async function cancelTask() { try { await invoke('tasks.cancel', { id: taskId.value }); await refreshTask() } catch (error) { taskError.value = String(error) } }
onBeforeUnmount(() => { disposed = true; clearTimeout(timer) })
</script>
<style scoped>
.operation { border: 1px solid var(--color-stroke-default); border-radius: 8px; margin: 6px 0; overflow: hidden; background: var(--color-bg-card); }
.delegated { border-left: 3px solid var(--color-accent); background: var(--color-bg-selected); }
.failed { border-color: var(--color-error); }
.operation-header { display: flex; align-items: center; gap: 10px; width: 100%; text-align: left; padding: 10px 12px; border: 0; background: transparent; color: inherit; cursor: pointer; }
.operation-header:hover { background: var(--color-bg-hover); }
button:focus-visible { outline: 2px solid var(--color-stroke-focus); outline-offset: -2px; }
.icon { color: var(--color-accent); font: 14px var(--font-mono); flex-shrink: 0; }
.title { display: flex; flex: 1; min-width: 0; gap: 8px; align-items: baseline; }
strong { font-size: 13px; flex-shrink: 0; font-weight: 600; }
.subtitle { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; color: var(--color-text-secondary); }
.state { font-size: 11px; color: var(--color-text-secondary); flex-shrink: 0; }
.body { padding: 0 12px 12px; font-size: 13px; }
.progress, .hint { color: var(--color-text-secondary); font-size: 12px; }
.progress { padding: 0 12px 10px 36px; }
.output, .diff, .steps { max-height: 300px; overflow: auto; border-radius: 4px; background: var(--color-bg-layer1); }
.output { padding: 10px; white-space: pre-wrap; overflow-wrap: anywhere; font: 12px/1.65 var(--font-mono); }
.output pre { white-space: pre-wrap; margin: 0; }
.diff { font: 12px/1.7 var(--font-mono); }
.diff-line { display: flex; min-width: max-content; }
.diff-line > span { width: 38px; flex-shrink: 0; color: var(--color-text-secondary); text-align: right; padding-right: 8px; user-select: none; }
.diff-line code { white-space: pre; padding-right: 12px; }
.added { background: color-mix(in srgb, var(--color-success) 12%, transparent); }
.removed { background: color-mix(in srgb, var(--color-error) 12%, transparent); }
.counts { font-size: 12px; white-space: nowrap; }
.counts span:first-child { color: var(--color-success); }
.counts span:last-child, .error { color: var(--color-error); }
.raw { margin-top: 10px; color: var(--color-text-secondary); font-size: 12px; }
summary { cursor: pointer; }
.actions { display: flex; gap: 8px; }
.actions button { padding: 4px 10px; border: 1px solid var(--color-stroke-default); border-radius: 4px; color: inherit; background: var(--color-bg-card); cursor: pointer; }
@media (max-width: 600px) { .title { flex-direction: column; gap: 2px; } .subtitle { max-width: 100%; } }
</style>
