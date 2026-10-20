<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import PixelIcon from '@/components/PixelIcon.vue'

/**
 * SandboxDiagram —— 执行链路图（区块 #sandbox）。
 *
 * 链路：模型 → execute_code → 沙箱 worker（Hyperlight 优先 / Monty 自动兜底）
 *      → C# 宿主工具（文件 / 命令 / 桌面 / 浏览器 / MCP）→ 本机 Windows。
 * 悬停或键盘聚焦任一节点时高亮，并在图下方的"节点说明"里显示它的一句话说明。
 */

/** 悬停说明的公共形状 */
type HoverInfo = {
  id: string
  label: string
  desc: string
}

/** 链路节点：tag 是序号，meta 是副标题 */
type NodeSpec = HoverInfo & {
  tag: string
  icon: string
  meta: string
}

/** 沙箱分支：在节点基础上带"优先 / 自动兜底"标签 */
type BranchSpec = NodeSpec & {
  badge: string
  badgeTone: 'primary' | 'alt'
}

const entryNodes: NodeSpec[] = [
  {
    id: 'model',
    tag: '01',
    icon: 'cpu',
    label: '模型',
    meta: 'OpenAI Chat / Responses · Anthropic · Gemini · 任何兼容 OpenAI 协议的服务（含 Ollama）',
    desc: '负责推理与编排：F2 或 /model 切换，选择会记住。',
  },
  {
    id: 'codeact',
    tag: '02',
    icon: 'terminal',
    label: 'execute_code',
    meta: '唯一暴露给模型的工具',
    desc: '模型只输出 Python：一次调用就是一段可复现的执行步骤，没有命令字符串或模板入口。',
  },
]

const branchNodes: BranchSpec[] = [
  {
    id: 'hyperlight',
    tag: '03A',
    icon: 'sandbox',
    label: 'Hyperlight microVM',
    meta: '需要硬件虚拟化的微虚拟机',
    badge: '优先',
    badgeTone: 'primary',
    desc: '默认优先尝试的隔离层，启动失败会立刻回退到 Monty，不需要你手动处理。',
  },
  {
    id: 'monty',
    tag: '03B',
    icon: 'shield',
    label: 'Monty 沙箱',
    meta: 'Rust 实现的 Python 子集 · 内存上限 128 MiB',
    badge: '自动兜底',
    badgeTone: 'alt',
    desc: '不需要虚拟化、Docker 或管理员权限的兜底后端，覆盖常见的脚本与数据处理。',
  },
]

const hostNode: NodeSpec = {
  id: 'host-tools',
  tag: '04',
  icon: 'layers',
  label: 'C# 宿主工具',
  meta: '35 个 [AgentTool]，其中 12 个 computer_* 桌面工具',
  desc: '真实副作用都发生在这里：沙箱里的 Python 只能通过宿主工具碰文件、进程与桌面。',
}

const surfaceNode: NodeSpec = {
  id: 'windows',
  tag: '05',
  icon: 'desktop',
  label: '本机 Windows',
  meta: '当前 Agent 执行仅支持 Windows x64',
  desc: '工作区、PowerShell 7.6、桌面与浏览器会话都留在你自己的机器上，不上传代码。',
}

const hostTools: HoverInfo[] = [
  {
    id: 'tool-file',
    label: '文件',
    desc: 'file_read / file_write / file_edit / list_directory / file_search（内容与 names_only 文件名搜索），带页级与字节预算、diff 预览和原子写。',
  },
  {
    id: 'tool-shell',
    label: '命令',
    desc: 'run_shell 只接受 exe + args，Base64 后交给固定脚本 run_shell.ps1，以 PowerShell 7.6 启动，参数不会被二次解析。',
  },
  {
    id: 'tool-computer',
    label: '桌面 Computer Use',
    desc: 'FlaUI / UIA3 的 12 个 computer_* 工具，只能由用户用 /computer on|off 打开，模式为 background / foreground / isolated 之一。',
  },
  {
    id: 'tool-browser',
    label: 'Browser Use',
    desc: '由 C# 实现：内置模式用 WebView2 独立窗口，外置模式自动连接已安装的 Chrome / Edge，无需额外浏览器库。',
  },
  {
    id: 'tool-mcp',
    label: 'MCP',
    desc: 'MCP 服务与内置 Skills、DLL 插件一起扩展可用的宿主工具集合。',
  },
]

const notes: HoverInfo[] = [
  {
    id: 'note-credential',
    label: 'worker 不继承模型服务凭据',
    desc: '沙箱 worker 只保留 Windows 系统环境变量，模型服务凭据不进 worker。',
  },
  {
    id: 'note-snapshot',
    label: '每次执行恢复干净快照',
    desc: '一次执行结束就丢弃状态，下一次从干净快照开始，不残留上一次的改动。',
  },
  {
    id: 'note-replay',
    label: '执行中失败不自动换后端重放',
    desc: '执行中失败不会自动切到另一个后端重跑代码，避免重复副作用。',
  },
]

const allInfo: HoverInfo[] = [
  ...entryNodes,
  ...branchNodes,
  hostNode,
  surfaceNode,
  ...hostTools,
  ...notes,
]

/** 当前悬停 / 聚焦的节点 id，空串表示没有 */
const active = ref('')

const activeInfo = computed<HoverInfo | null>(() => {
  if (active.value === '') {
    return null
  }
  return allInfo.find((item) => item.id === active.value) ?? null
})

/** 说明文本拆成两个 computed，模板里就不依赖可空收窄 */
const activeLabel = computed<string>(() => activeInfo.value?.label ?? '')
const activeDesc = computed<string>(() => activeInfo.value?.desc ?? '')

/**
 * 鼠标在相邻节点之间移动时，mouseleave 可能晚于下一个 mouseenter 触发，
 * 所以只在"离开的正是当前高亮项"时才清除，避免高亮闪烁。
 */
function hot(id: string): void {
  active.value = id
}

function cool(id: string): void {
  if (active.value === id) {
    active.value = ''
  }
}

/** 图进入视口后再让数据方块开始流动，省掉首屏之外的动画开销 */
const diagramEl = ref<HTMLElement | null>(null)
const live = ref(false)
let observer: IntersectionObserver | null = null

function startFlow(): void {
  live.value = true
  observer?.disconnect()
  observer = null
}

onMounted(() => {
  const host = diagramEl.value
  if (host === null || typeof IntersectionObserver === 'undefined') {
    live.value = true
    return
  }
  observer = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) {
          startFlow()
        }
      }
    },
    { rootMargin: '0px 0px -10% 0px', threshold: 0.15 },
  )
  observer.observe(host)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
})
</script>

<template>
  <section id="sandbox" class="section sandbox">
    <div class="shell">
      <header class="head">
        <p class="eyebrow">Execution Path</p>
        <h2 class="head__title">一次调用，经过五层</h2>
        <p class="head__lead muted">
          模型侧只有 <code>execute_code</code> 这一个入口：Python 在隔离沙箱里运行，文件、命令与桌面操作全部经由
          <strong class="text-accent">C# 宿主工具</strong>落到本机 Windows。
        </p>
      </header>

      <div class="grid">
        <div ref="diagramEl" class="diagram panel" :class="{ 'is-live': live }">
          <span class="diagram__tex dither" aria-hidden="true"></span>
          <span class="scan-sweep diagram__sweep" aria-hidden="true"></span>

          <div class="flow">
            <!-- 01 模型 -->
            <article
              class="node panel panel--flat pixel-cut"
              :class="{ 'is-hot': active === entryNodes[0].id }"
              tabindex="0"
              @mouseenter="hot(entryNodes[0].id)"
              @mouseleave="cool(entryNodes[0].id)"
              @focus="hot(entryNodes[0].id)"
              @blur="cool(entryNodes[0].id)"
            >
              <span class="node__tag tiny">{{ entryNodes[0].tag }}</span>
              <PixelIcon :name="entryNodes[0].icon" class="node__icon" />
              <div class="node__body">
                <h3 class="node__title">{{ entryNodes[0].label }}</h3>
                <p class="node__meta muted">{{ entryNodes[0].meta }}</p>
              </div>
            </article>

            <div class="link" aria-hidden="true">
              <div class="link__cell">
                <span class="link__rail"></span>
                <span class="link__bit"></span>
              </div>
            </div>

            <!-- 02 execute_code -->
            <article
              class="node panel panel--flat pixel-cut"
              :class="{ 'is-hot': active === entryNodes[1].id }"
              tabindex="0"
              @mouseenter="hot(entryNodes[1].id)"
              @mouseleave="cool(entryNodes[1].id)"
              @focus="hot(entryNodes[1].id)"
              @blur="cool(entryNodes[1].id)"
            >
              <span class="node__tag tiny">{{ entryNodes[1].tag }}</span>
              <PixelIcon :name="entryNodes[1].icon" class="node__icon" />
              <div class="node__body">
                <h3 class="node__title">{{ entryNodes[1].label }}</h3>
                <p class="node__meta muted">{{ entryNodes[1].meta }}</p>
              </div>
            </article>

            <!-- 分叉：两条并行路径 -->
            <div class="link link--split link--fork" aria-hidden="true">
              <div class="link__cell">
                <span class="link__rail"></span>
                <span class="link__bit"></span>
              </div>
              <div class="link__cell">
                <span class="link__rail"></span>
                <span class="link__bit"></span>
              </div>
            </div>

            <!-- 03 沙箱 worker -->
            <div class="stage">
              <p class="stage__caption tiny">03 · 沙箱 worker（同一次执行只走一条）</p>
              <div class="branch">
                <article
                  v-for="branch in branchNodes"
                  :key="branch.id"
                  class="node node--branch panel panel--flat pixel-cut"
                  :class="{ 'is-hot': active === branch.id }"
                  tabindex="0"
                  @mouseenter="hot(branch.id)"
                  @mouseleave="cool(branch.id)"
                  @focus="hot(branch.id)"
                  @blur="cool(branch.id)"
                >
                  <span class="node__tag tiny">{{ branch.tag }}</span>
                  <PixelIcon :name="branch.icon" class="node__icon" />
                  <div class="node__body">
                    <h3 class="node__title">{{ branch.label }}</h3>
                    <p class="node__meta muted">{{ branch.meta }}</p>
                  </div>
                  <span class="badge" :class="`badge--${branch.badgeTone}`">{{ branch.badge }}</span>
                </article>
              </div>
            </div>

            <!-- 汇合 -->
            <div class="link link--split link--merge" aria-hidden="true">
              <div class="link__cell">
                <span class="link__rail"></span>
                <span class="link__bit"></span>
              </div>
              <div class="link__cell">
                <span class="link__rail"></span>
                <span class="link__bit"></span>
              </div>
            </div>

            <!-- 04 C# 宿主工具 -->
            <article
              class="node node--host panel panel--flat pixel-cut"
              :class="{ 'is-hot': active === hostNode.id }"
              tabindex="0"
              @mouseenter="hot(hostNode.id)"
              @mouseleave="cool(hostNode.id)"
              @focus="hot(hostNode.id)"
              @blur="cool(hostNode.id)"
            >
              <div class="node__head">
                <span class="node__tag tiny">{{ hostNode.tag }}</span>
                <PixelIcon :name="hostNode.icon" class="node__icon" />
                <div class="node__body">
                  <h3 class="node__title">{{ hostNode.label }}</h3>
                  <p class="node__meta muted">{{ hostNode.meta }}</p>
                </div>
              </div>
              <ul class="chips">
                <li
                  v-for="tool in hostTools"
                  :key="tool.id"
                  class="chip"
                  :class="{ 'is-hot': active === tool.id }"
                  tabindex="0"
                  @mouseenter="hot(tool.id)"
                  @mouseleave="cool(tool.id)"
                  @focus="hot(tool.id)"
                  @blur="cool(tool.id)"
                >
                  {{ tool.label }}
                </li>
              </ul>
            </article>

            <div class="link" aria-hidden="true">
              <div class="link__cell">
                <span class="link__rail"></span>
                <span class="link__bit"></span>
              </div>
            </div>

            <!-- 05 本机 Windows -->
            <article
              class="node panel panel--flat pixel-cut"
              :class="{ 'is-hot': active === surfaceNode.id }"
              tabindex="0"
              @mouseenter="hot(surfaceNode.id)"
              @mouseleave="cool(surfaceNode.id)"
              @focus="hot(surfaceNode.id)"
              @blur="cool(surfaceNode.id)"
            >
              <span class="node__tag tiny">{{ surfaceNode.tag }}</span>
              <PixelIcon :name="surfaceNode.icon" class="node__icon" />
              <div class="node__body">
                <h3 class="node__title">{{ surfaceNode.label }}</h3>
                <p class="node__meta muted">{{ surfaceNode.meta }}</p>
              </div>
            </article>
          </div>

          <div class="inspector" aria-live="polite">
            <p class="tiny">节点说明</p>
            <p class="inspector__text">
              <template v-if="activeInfo">
                <strong class="text-accent">{{ activeLabel }}</strong>
                <span class="muted"> · {{ activeDesc }}</span>
              </template>
              <template v-else>
                <span class="muted">把指针移到链路中的任一节点（或 tab 聚焦），这里显示它的职责。</span>
              </template>
            </p>
          </div>
        </div>

        <aside class="notes">
          <h3 class="notes__title">沙箱的硬约束</h3>
          <ul class="notes__list">
            <li
              v-for="note in notes"
              :key="note.id"
              class="note panel panel--flat pixel-cut hover-lift"
              :class="{ 'is-hot': active === note.id }"
              tabindex="0"
              @mouseenter="hot(note.id)"
              @mouseleave="cool(note.id)"
              @focus="hot(note.id)"
              @blur="cool(note.id)"
            >
              <p class="note__title">{{ note.label }}</p>
              <p class="note__text muted">{{ note.desc }}</p>
            </li>
          </ul>
        </aside>
      </div>
    </div>
  </section>
</template>

<style scoped>
/* ---------- 区块头 ---------- */
.head {
  display: flex;
  flex-direction: column;
  gap: var(--grid);
  max-width: 62ch;
  margin-bottom: var(--grid-3);
}

.head__title {
  font-size: var(--fs-h2);
}

.head__lead code {
  color: var(--c-amber);
}

/* ---------- 两栏：链路图 + 要点 ---------- */
.grid {
  display: grid;
  grid-template-columns: minmax(0, 1.6fr) minmax(0, 1fr);
  gap: var(--grid-3);
  align-items: start;
}

/* ---------- 链路图外框 ---------- */
.diagram {
  position: relative;
  overflow: hidden;
  padding: var(--grid-2);
}

.diagram__tex {
  position: absolute;
  inset: 0;
  opacity: 0.55;
  pointer-events: none;
}

.diagram__sweep {
  opacity: 0.5;
}

.flow {
  position: relative;
  display: flex;
  flex-direction: column;
}

/* ---------- 节点盒 ---------- */
.node {
  position: relative;
  display: flex;
  align-items: center;
  gap: var(--grid-2);
  padding: var(--grid-2);
  background: linear-gradient(180deg, var(--c-surface-2), var(--c-surface));
  transition: border-color var(--t-fast) var(--ease-step), box-shadow var(--t-fast) var(--ease-step);
}

.node--host {
  flex-direction: column;
  align-items: stretch;
  gap: var(--grid-2);
}

.node__head {
  display: flex;
  align-items: center;
  gap: var(--grid-2);
}

.node__tag {
  flex: none;
  color: var(--c-text-mute);
}

.node__icon {
  flex: none;
  color: var(--c-mint);
}

.node__body {
  min-width: 0;
  flex: 1 1 auto;
}

.node__title {
  font-size: var(--fs-body);
  font-family: var(--font-mono);
  letter-spacing: 0;
}

.node__meta {
  font-size: var(--fs-small);
  line-height: 1.6;
  overflow-wrap: anywhere;
}

/* 高亮：硬边描边 + 克制的辉光 */
.node.is-hot,
.chip.is-hot,
.note.is-hot {
  border-color: var(--c-mint);
  box-shadow: inset 0 0 0 2px var(--c-line), var(--glow-mint);
}

/* ---------- 优先 / 自动兜底 标签 ---------- */
.badge {
  flex: none;
  margin-left: auto;
  padding: var(--bit) var(--grid);
  border: 2px solid var(--c-line-strong);
  background: var(--c-void);
  font-family: var(--font-pixel);
  font-size: var(--fs-micro);
  letter-spacing: 0.12em;
  line-height: 1.4;
  white-space: nowrap;
}

.badge--primary {
  color: var(--c-mint);
  border-color: var(--c-mint-deep);
}

.badge--alt {
  color: var(--c-amber);
  border-color: var(--c-amber-deep);
}

/* ---------- 宿主工具小标签 ---------- */
.chips {
  display: flex;
  flex-wrap: wrap;
  gap: var(--grid);
  margin: 0;
  padding: 0;
  list-style: none;
}

.chip {
  padding: var(--bit) var(--grid);
  border: 2px solid var(--c-line);
  background: var(--c-surface-3);
  font-family: var(--font-mono);
  font-size: var(--fs-small);
  line-height: 1.5;
  transition: border-color var(--t-fast) var(--ease-step), box-shadow var(--t-fast) var(--ease-step);
  cursor: help;
}

/* ---------- 沙箱分支 ---------- */
.stage {
  display: flex;
  flex-direction: column;
  gap: var(--grid);
}

.stage__caption {
  margin: 0;
}

.branch {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: var(--grid-2);
}

/* ---------- 连接线 ---------- */
.link {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--grid-2);
}

.link__cell {
  position: relative;
  display: flex;
  justify-content: center;
  height: calc(var(--grid) * 4);
}

/* 2px 是"一个像素点"的一半，属于像素补偿值 */
.link__rail {
  width: 2px;
  height: 100%;
  background-image: repeating-linear-gradient(
    to bottom,
    var(--c-line-strong) 0 var(--bit),
    transparent var(--bit) calc(var(--bit) * 2)
  );
}

/* 沿路径流动的 4px 数据方块 */
.link__bit {
  position: absolute;
  top: 0;
  left: 50%;
  width: var(--bit);
  height: var(--bit);
  margin-left: calc(var(--bit) / -2); /* 像素补偿：让方块正压在连接线上 */
  background: var(--c-mint);
  animation: bit-run 2.4s steps(9, end) infinite;
  animation-play-state: paused;
}

.diagram.is-live .link__bit {
  animation-play-state: running;
}

@keyframes bit-run {
  from {
    top: 0;
  }
  to {
    top: calc(100% - var(--bit));
  }
}

/* 两条并行路径 */
.link--split {
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
}

/* 优先路径用薄荷色，兜底路径用琥珀色，两条支路错开相位 */
.link--split .link__cell:first-child .link__bit {
  background: var(--c-mint);
}

.link--split .link__cell:last-child .link__bit {
  background: var(--c-amber);
  animation-delay: 1.2s;
}

.link--split .link__cell {
  height: calc(var(--grid) * 6);
}

/* 分叉 / 汇合处的横杠与中轴 */
.link--fork::before,
.link--merge::before,
.link--fork::after,
.link--merge::after {
  content: '';
  position: absolute;
  background: var(--c-line-strong);
  pointer-events: none;
}

.link--fork::before,
.link--merge::before {
  left: calc(25% - var(--bit)); /* 对齐两条支路的中心 */
  right: calc(25% - var(--bit));
  height: 2px;
}

.link--fork::after,
.link--merge::after {
  left: calc(50% - 1px); /* 像素补偿 */
  width: 2px;
  height: var(--bar-y);
}

.link--fork {
  --bar-y: calc(var(--grid) * 3);
}

.link--fork::before {
  top: var(--bar-y);
}

.link--fork::after {
  top: 0;
}

.link--merge {
  --bar-y: calc(var(--grid) * 3);
}

.link--merge::before {
  bottom: var(--bar-y);
}

.link--merge::after {
  bottom: 0;
}

/* ---------- 节点说明 ---------- */
.inspector {
  position: relative;
  margin-top: var(--grid-2);
  padding-top: var(--grid-2);
  border-top: 2px dashed rgba(34, 49, 60, 0.7);
  min-height: calc(var(--grid) * 9);
  display: flex;
  flex-direction: column;
  gap: var(--bit);
}

.inspector__text {
  font-size: var(--fs-small);
  line-height: 1.7;
  overflow-wrap: anywhere;
}

/* ---------- 要点 ---------- */
.notes {
  display: flex;
  flex-direction: column;
  gap: var(--grid-2);
}

.notes__title {
  font-size: var(--fs-h3);
}

.notes__list {
  display: flex;
  flex-direction: column;
  gap: var(--grid-2);
  margin: 0;
  padding: 0;
  list-style: none;
}

.note {
  display: flex;
  flex-direction: column;
  gap: var(--bit);
  padding: var(--grid-2);
  background: var(--c-bg-soft);
}

.note__title {
  font-family: var(--font-pixel);
  font-size: var(--fs-body);
  line-height: 1.5;
}

.note__title::before {
  content: '';
  display: inline-block;
  width: var(--bit);
  height: var(--bit);
  margin-right: var(--grid);
  background: var(--c-amber);
  vertical-align: 0.15em; /* 像素补偿：让方块与文字基线对齐 */
}

.note__text {
  font-size: var(--fs-small);
  line-height: 1.7;
  overflow-wrap: anywhere;
}

/* ---------- 响应式：窄屏纵向堆叠，不出现横向滚动 ---------- */
@media (min-width: 768px) {
  .diagram {
    padding: var(--grid-3);
  }
}

@media (max-width: 1023px) {
  .grid {
    grid-template-columns: minmax(0, 1fr);
  }
}

@media (max-width: 767px) {
  .branch,
  .link--split {
    grid-template-columns: minmax(0, 1fr);
  }

  .link--split {
    gap: 0;
  }

  /* 纵向堆叠后不再需要分叉横杠 */
  .link--split::before,
  .link--split::after {
    display: none;
  }

  .badge {
    margin-left: 0;
  }

  .node--branch {
    flex-wrap: wrap;
  }
}
</style>
