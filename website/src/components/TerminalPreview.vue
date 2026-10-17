<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'

/**
 * TerminalPreview —— 按 AllAgent.NET 终端（Spectre.Console）真实布局还原的会话预览。
 *
 * 配色与结构取自 src/AllAgent.NET.Terminal：
 *   TerminalTheme "night"：背景 #11131A、面板 #1A1D27、强调 #7DCFFF、前景 #DCE1EB
 *   FullScreenTerminal：消息区 + 右侧栏（会话信息 / 扩展 / 操作）+ 底部三行 footer，
 *   输入区是 Heavy 圆角面板、标题“输入任务”，审批列表用 “❯ 1. 允许这一次”。
 * 为了让预览贴近真实终端，这里的颜色直接使用终端主题色，不跟随站点调色板。
 */
type LineKind = 'user' | 'text' | 'tool' | 'ok' | 'dim' | 'choice'

type Line = {
  kind: LineKind
  /** tool 行：面板标题（工具名 · 参数）；其余行：正文 */
  text: string
  /** tool 行的面板正文 */
  body?: string
  /** choice 行：是否被选中（❯） */
  selected?: boolean
}

type Scene = {
  id: string
  label: string
  lines: Line[]
}

const SCENES: Scene[] = [
  {
    id: 'build',
    label: '修构建',
    lines: [
      { kind: 'user', text: '检查这个 .NET 项目为什么无法构建，修复后运行相关测试。' },
      { kind: 'dim', text: 'execute_code · python · Hyperlight microVM' },
      { kind: 'tool', text: 'file_search · {"pattern":"CS0246","path":"src"}', body: '3 matches in 2 files' },
      {
        kind: 'tool',
        text: 'run_shell · {"exe":"dotnet","args":["build","src/App/App.csproj"]}',
        body: 'error CS0246: 未能找到类型或命名空间名“PathGate”',
      },
      {
        kind: 'tool',
        text: 'file_edit · {"path":"src/AllAgent.NET.Core/Tools/SystemTools.cs"}',
        body: '+3 / -1 · 已写入并校验校验和',
      },
      {
        kind: 'tool',
        text: 'run_shell · {"exe":"dotnet","args":["test","tests/AllAgent.NET.Tests"]}',
        body: 'Passed!  Failed: 0, Passed: 214, Skipped: 3, Total: 217',
      },
      { kind: 'ok', text: '已修正 3 处引用并跑完相关测试。工具卡片保留了实际参数与工作目录。' },
    ],
  },
  {
    id: 'approval',
    label: '授权',
    lines: [
      { kind: 'user', text: '把 C:\\Projects\\OtherRepo 里的失败用例单独跑一遍' },
      { kind: 'dim', text: 'run_shell · 工作区外路径，需要审批' },
      { kind: 'text', text: '需要授权' },
      { kind: 'choice', text: '1. 允许这一次', selected: true },
      { kind: 'choice', text: '2. 拒绝' },
      { kind: 'choice', text: '3. 此会话记住目录' },
      { kind: 'tool', text: 'run_shell · {"exe":"dotnet","args":["test","--filter","FailedOnly"]}', body: '3 passed · 0 failed' },
      { kind: 'ok', text: '审批默认选中“允许这一次”，回车确认；N / Esc 拒绝。' },
    ],
  },
  {
    id: 'browser',
    label: '浏览器',
    lines: [
      { kind: 'user', text: '打开文档站，看看有没有 0.2.1 的 API 变更' },
      { kind: 'dim', text: 'browser use · 内置 WebView2 会话' },
      { kind: 'tool', text: 'browser · navigate', body: '/zh/guide/changelog · loaded 1.2s' },
      { kind: 'tool', text: 'browser · eval', body: '"更新日志 | Pixelium Design"' },
      { kind: 'tool', text: 'browser · screenshot', body: '1440×900 · 已附加到对话' },
      { kind: 'ok', text: '已整理 3 条与当前项目相关的变更。默认开启，无需额外安装浏览器组件。' },
    ],
  },
]

const LINE_MS = 300
const HOLD_MS = 3200
const SPINNER = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧']

const host = ref<HTMLElement | null>(null)
const sceneIndex = ref(0)
const visibleCount = ref(0)
const playing = ref(true)
const spinnerIndex = ref(0)

const scene = computed<Scene>(() => SCENES[sceneIndex.value])
const lines = computed<Line[]>(() => scene.value.lines)
const streaming = computed(() => visibleCount.value < lines.value.length)
const spinner = computed(() => SPINNER[spinnerIndex.value % SPINNER.length])

let lineTimer: ReturnType<typeof setInterval> | null = null
let holdTimer: ReturnType<typeof setTimeout> | null = null
let spinTimer: ReturnType<typeof setInterval> | null = null
let io: IntersectionObserver | null = null
let inView = true
let reduced = false

function clearTimers(): void {
  if (lineTimer !== null) {
    clearInterval(lineTimer)
    lineTimer = null
  }
  if (holdTimer !== null) {
    clearTimeout(holdTimer)
    holdTimer = null
  }
}

function tick(): void {
  if (!inView || !playing.value || reduced) {
    return
  }
  if (visibleCount.value < lines.value.length) {
    visibleCount.value += 1
    return
  }
  if (holdTimer === null) {
    holdTimer = setTimeout(() => {
      holdTimer = null
      sceneIndex.value = (sceneIndex.value + 1) % SCENES.length
    }, HOLD_MS)
  }
}

function start(): void {
  clearTimers()
  if (reduced) {
    visibleCount.value = lines.value.length
    return
  }
  lineTimer = setInterval(tick, LINE_MS)
}

function selectScene(index: number): void {
  sceneIndex.value = index
}

function toggle(): void {
  playing.value = !playing.value
}

watch(sceneIndex, () => {
  visibleCount.value = 0
  start()
})

watch(playing, (value) => {
  if (value) {
    start()
  } else {
    clearTimers()
  }
})

onMounted(() => {
  reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
  start()
  spinTimer = setInterval(() => {
    spinnerIndex.value += 1
  }, 120)

  const el = host.value
  if (el !== null && typeof IntersectionObserver !== 'undefined') {
    io = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          inView = entry.isIntersecting
        }
      },
      { threshold: 0.15 },
    )
    io.observe(el)
  }
})

onBeforeUnmount(() => {
  clearTimers()
  if (spinTimer !== null) {
    clearInterval(spinTimer)
  }
  io?.disconnect()
  io = null
})
</script>

<template>
  <section id="demo" class="section demo">
    <div class="shell">
      <header class="head">
        <p class="eyebrow">Terminal</p>
        <h2>这就是那个终端</h2>
        <p class="muted intro">
          下面按终端里的真实排版回放：消息区、右栏会话信息、底部三行状态，输入区是“输入任务”面板。
          配色取自终端主题 <code>night</code>（#11131A / #1A1D27 / #7DCFFF）。
        </p>
      </header>

      <div ref="host" class="term panel">
        <div class="term-bar">
          <span class="term-title tiny">allagent · terminal</span>
          <div class="term-tabs">
            <button
              v-for="(item, i) in SCENES"
              :key="item.id"
              class="tab"
              type="button"
              :class="{ 'tab--active': i === sceneIndex }"
              @click="selectScene(i)"
            >
              {{ item.label }}
            </button>
          </div>
          <button class="term-toggle tiny" type="button" @click="toggle">{{ playing ? '暂停' : '播放' }}</button>
        </div>

        <div class="term-body">
          <div class="term-main">
            <ol class="transcript">
              <template v-for="(line, i) in lines.slice(0, visibleCount)" :key="`${scene.id}-${i}`">
                <li v-if="line.kind === 'user'" class="row row--user">
                  <span class="prompt">❯</span>
                  <span>{{ line.text }}</span>
                </li>

                <li v-else-if="line.kind === 'dim'" class="row row--dim">
                  <span class="gutter" />
                  <span>{{ line.text }}</span>
                </li>

                <li v-else-if="line.kind === 'tool'" class="row row--tool">
                  <span class="gutter" />
                  <div class="tool">
                    <p class="tool-head">{{ line.text }}</p>
                    <p v-if="line.body" class="tool-body">{{ line.body }}</p>
                  </div>
                </li>

                <li v-else-if="line.kind === 'choice'" class="row row--choice" :class="{ 'is-selected': line.selected }">
                  <span class="gutter" />
                  <span>{{ line.selected ? '❯' : ' ' }} {{ line.text }}</span>
                </li>

                <li v-else-if="line.kind === 'ok'" class="row row--ok">
                  <span class="gutter" />
                  <span>{{ line.text }}</span>
                </li>

                <li v-else class="row row--text">
                  <span class="gutter" />
                  <span class="label">{{ line.text }}</span>
                </li>
              </template>

              <li v-if="streaming" class="row row--dim">
                <span class="gutter" />
                <span class="activity">{{ spinner }} 正在执行…</span>
              </li>
            </ol>

            <div class="input-panel">
              <span class="input-label">输入任务</span>
              <p class="input-line">
                <span class="caret">/diff 看看刚才的改动</span>
              </p>
            </div>
          </div>

          <aside class="sidebar">
            <p class="side-title">会话信息</p>
            <p class="side-model">gpt-5-codex · openai</p>
            <p class="side-dim">模型缓存命中率 · 92%</p>
            <p class="side-rule" />
            <p class="side-title">扩展</p>
            <p class="side-dim">技能 5 · MCP 2 · 插件 1</p>
            <p class="side-rule" />
            <p class="side-title">操作</p>
            <p class="side-dim">F2 选择模型 · Ctrl+E 执行详情</p>
          </aside>
        </div>

        <footer class="term-footer">
          <p class="foot-line">turn 12 · 沙箱 hyperlight │ 权限 full</p>
          <p class="foot-line">上下文 38k / 200k │ 已就绪</p>
          <p class="foot-line">F1 菜单  F2 模型  Ctrl+E 执行详情  消息区拖选文字 · Ctrl+C 复制</p>
        </footer>
      </div>
    </div>
  </section>
</template>

<style scoped>
.head {
  max-width: 760px;
  margin-bottom: var(--grid-3);
}

.head h2 {
  font-size: var(--fs-h2);
  margin: var(--grid) 0;
}

.intro {
  max-width: 64ch;
}

.intro code {
  color: var(--c-mint);
}

/* ---------- 终端外壳：沿用站点的硬边像素面板 ---------- */
.term {
  padding: 0;
  overflow: hidden;
}

.term-bar {
  display: flex;
  align-items: center;
  gap: var(--grid-2);
  padding: 8px 12px;
  border-bottom: 2px solid var(--c-line);
  background: var(--c-surface-2);
}

.term-title {
  color: var(--c-text-mute);
}

.term-tabs {
  display: flex;
  gap: 6px;
  margin-left: auto;
}

.tab {
  padding: 3px 10px;
  border: 2px solid var(--c-line);
  background: transparent;
  color: var(--c-text-dim);
  font-family: var(--font-pixel);
  font-size: var(--fs-micro);
  cursor: pointer;
  transition: color var(--t-fast) var(--ease-step), border-color var(--t-fast) var(--ease-step);
}

.tab:hover {
  color: var(--c-text);
}

.tab--active {
  border-color: var(--term-accent);
  color: var(--c-void);
  background: var(--term-accent);
}

.term-toggle {
  padding: 3px 8px;
  border: 2px solid var(--c-line);
  background: transparent;
  color: var(--c-text-dim);
  font-family: var(--font-pixel);
  cursor: pointer;
}

/* ---------- 终端本体：主题色来自 AllAgent.NET.Terminal 的 night 主题 ---------- */
.term {
  --term-bg: #11131a;
  --term-surface: #1a1d27;
  --term-accent: #7dcfff;
  --term-fg: #dce1eb;
  --term-dim: #8b93a7;
  --term-green: #94e2a9;
}

.term-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 220px;
  background: var(--term-bg);
  color: var(--term-fg);
  font-family: var(--font-mono);
  font-size: 0.82rem;
  line-height: 1.7;
}

.term-main {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 16px 16px 18px;
  min-height: 380px;
}

.transcript {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 10px;
  flex: 1;
}

.row {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr);
  gap: 8px;
  animation: term-line 200ms steps(3, end);
}

@keyframes term-line {
  from {
    opacity: 0;
    transform: translateY(2px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

.gutter {
  display: block;
}

.row--user {
  color: var(--term-fg);
}

.prompt {
  color: var(--term-accent);
}

.row--dim {
  color: var(--term-dim);
}

.row--dim .gutter {
  color: var(--term-dim);
}

.row--text {
  color: var(--term-accent);
}

.label::after {
  content: '';
}

.row--ok {
  color: var(--term-green);
}

.row--choice {
  color: var(--term-dim);
}

.row--choice.is-selected {
  color: var(--term-green);
  font-weight: 700;
}

.activity {
  color: var(--term-accent);
}

/* 工具卡片：真实终端里是带标题的圆角面板 */
.tool {
  border: 2px solid var(--term-accent);
  border-radius: 6px;
  background: var(--term-surface);
  padding: 6px 10px 8px;
}

.tool-head {
  color: var(--term-accent);
  overflow-wrap: anywhere;
}

.tool-body {
  color: var(--term-fg);
  overflow-wrap: anywhere;
}

/* 输入区：Heavy 边框 + “输入任务”标题 */
.input-panel {
  position: relative;
  border: 3px solid var(--term-accent);
  border-radius: 6px;
  padding: 10px 12px;
}

.input-label {
  position: absolute;
  top: -12px;
  left: 10px;
  padding: 0 6px;
  background: var(--term-bg);
  color: var(--term-accent);
  font-size: 0.72rem;
}

.input-line {
  color: var(--term-fg);
}

.caret::after {
  content: '';
  display: inline-block;
  width: 0.55em;
  height: 1em;
  margin-left: 2px;
  background: var(--term-accent);
  vertical-align: -0.15em;
  animation: caret-blink 1s steps(1, end) infinite;
}

/* 右栏 */
.sidebar {
  border-left: 2px solid var(--c-line);
  background: var(--term-surface);
  padding: 16px 14px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.side-title {
  color: var(--term-fg);
  font-weight: 700;
}

.side-model {
  color: var(--term-accent);
}

.side-dim {
  color: var(--term-dim);
  font-size: 0.76rem;
}

.side-rule {
  height: 1px;
  margin: 6px 0;
  background: rgba(139, 147, 167, 0.3);
}

/* 底部三行状态 */
.term-footer {
  border-top: 2px solid var(--c-line);
  background: var(--c-surface-2);
  padding: 8px 14px;
}

.foot-line {
  color: var(--c-text-mute);
  font-family: var(--font-mono);
  font-size: var(--fs-micro);
  letter-spacing: 0.04em;
}

@media (max-width: 992px) {
  .term-body {
    grid-template-columns: minmax(0, 1fr);
  }

  .sidebar {
    border-left: 0;
    border-top: 2px solid var(--c-line);
  }
}

@media (max-width: 560px) {
  .term-tabs {
    display: none;
  }

  .term-main {
    padding: 12px;
  }
}
</style>
