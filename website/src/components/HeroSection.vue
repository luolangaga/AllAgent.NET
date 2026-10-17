<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { Button as PxButton } from '@pixelium/web-vue/es'
import PixelField from '@/components/PixelField.vue'
import PixelIcon from '@/components/PixelIcon.vue'

/** 首屏：位图背景 + 一句定位 + 安装命令 + 关键数字。 */
const REPO_URL = 'https://github.com/luolangaga/AllAgent.NET'
const INSTALL_CMD = 'dotnet tool install --global AllAgent.NET.Terminal'

const copied = ref(false)
let copiedTimer: ReturnType<typeof setTimeout> | null = null

type Stat = { to: number; suffix: string; label: string; note: string }

const STATS: Stat[] = [
  { to: 35, suffix: '', label: '宿主工具', note: '全部经 C# 宿主执行' },
  { to: 12, suffix: '', label: '桌面工具', note: 'computer_* UIA3' },
  { to: 2, suffix: '', label: '沙箱后端', note: 'Hyperlight / Monty' },
  { to: 3, suffix: '', label: '权限档位', note: 'full · ask · plan' },
]

const CHIPS: string[] = ['exe + args 字面量传递', '模型凭据不进 worker', 'Windows 路径闸门']

const BOOT: { text: string; tone: 'ok' | 'key' | 'dim' }[] = [
  { text: 'allagent doctor', tone: 'key' },
  { text: 'host python 3.13.7 · pwsh 7.6.x · ripgrep', tone: 'ok' },
  { text: 'sandbox backend: hyperlight (verified)', tone: 'ok' },
  { text: 'shared venv: python/venv · pip.md indexed', tone: 'dim' },
  { text: 'workspace: C:\\Projects\\MyProject', tone: 'dim' },
  { text: 'ready — describe the task', tone: 'ok' },
]

/** 数字滚动：自己实现，保证一定能停在目标值上（不依赖组件的触发时机） */
const statsHost = ref<HTMLElement | null>(null)
const shown = ref<number[]>(STATS.map(() => 0))
let countRaf = 0
let statsObserver: IntersectionObserver | null = null

function easeOut(p: number): number {
  return 1 - (1 - p) ** 3
}

function countUp(): void {
  const started = performance.now()
  const duration = 1200
  const frame = (now: number): void => {
    const p = Math.min(1, (now - started) / duration)
    shown.value = STATS.map((stat, i) => {
      // 每个数字错开 0.12 的进度，形成阶梯感
      const offset = i * 0.12
      const local = Math.max(0, Math.min(1, (p - offset) / (1 - offset)))
      return Math.round(stat.to * easeOut(local))
    })
    if (p < 1) {
      countRaf = window.requestAnimationFrame(frame)
    }
  }
  countRaf = window.requestAnimationFrame(frame)
}

function applyStaticCounts(): void {
  shown.value = STATS.map((stat) => stat.to)
}

async function copyInstall(): Promise<void> {
  try {
    await navigator.clipboard.writeText(INSTALL_CMD)
    copied.value = true
  } catch {
    copied.value = false
  }
  if (copiedTimer !== null) {
    clearTimeout(copiedTimer)
  }
  copiedTimer = setTimeout(() => {
    copied.value = false
    copiedTimer = null
  }, 1600)
}

function goQuickStart(): void {
  document.querySelector('#quickstart')?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

function openRepo(): void {
  window.open(REPO_URL, '_blank', 'noopener,noreferrer')
}

onMounted(() => {
  const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
  const host = statsHost.value
  if (reduced || host === null || typeof IntersectionObserver === 'undefined') {
    applyStaticCounts()
    return
  }
  statsObserver = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) {
          countUp()
          statsObserver?.disconnect()
          statsObserver = null
        }
      }
    },
    { threshold: 0.3 },
  )
  statsObserver.observe(host)
})

onBeforeUnmount(() => {
  if (copiedTimer !== null) {
    clearTimeout(copiedTimer)
  }
  if (countRaf !== 0) {
    window.cancelAnimationFrame(countRaf)
  }
  statsObserver?.disconnect()
  statsObserver = null
})
</script>

<template>
  <section id="top" class="hero scanlines">
    <PixelField class="hero-bg" />
    <div class="hero-grid grid-lines" aria-hidden="true" />
    <div class="scan-sweep" aria-hidden="true" />

    <div class="shell hero-inner">
      <div class="lead">
        <p class="eyebrow hero-eyebrow">
          Windows x64 · Local-first Agent
          <span class="blink" aria-hidden="true" />
        </p>

        <h1 class="title">
          <span class="title-line">在你的终端里</span>
          <span class="title-line title-line--accent glitch">让 AI 真正动手</span>
        </h1>

        <p class="lede muted">
          AllAgent.NET 把模型侧的入口收成一个 <code>execute_code</code>：Python 在隔离沙箱中组织步骤，
          文件、命令、桌面与浏览器动作交给
          <strong>35 个 C# 宿主工具</strong>执行。参数按字面量传递，路径按 Windows 语义校验，
          模型服务的凭据不会进沙箱。
        </p>

        <ul class="chips">
          <li v-for="chip in CHIPS" :key="chip">
            <PixelIcon name="check" :size="16" class="chip-icon" />
            {{ chip }}
          </li>
        </ul>

        <div class="install panel">
          <span class="install-prompt" aria-hidden="true">PS&gt;</span>
          <code class="install-cmd">{{ INSTALL_CMD }}</code>
          <button class="install-copy" type="button" :aria-label="'复制安装命令'" @click="copyInstall">
            <PixelIcon :name="copied ? 'check' : 'copy'" :size="18" />
            <span>{{ copied ? '已复制' : '复制' }}</span>
          </button>
        </div>

        <div class="cta row">
          <PxButton theme="primary" size="large" @click="goQuickStart">三步快速开始</PxButton>
          <PxButton theme="info" variant="outline" size="large" @click="openRepo">查看源码</PxButton>
          <span class="cta-note tiny">.NET Tool · Windows x64 · GPL-3.0-only</span>
        </div>
      </div>

      <aside class="status panel scanlines">
        <header class="status-bar">
          <span class="status-dot" />
          <span class="tiny">allagent · doctor</span>
          <span class="status-live tiny">live</span>
        </header>

        <ul class="boot">
          <li v-for="(line, i) in BOOT" :key="line.text" class="boot-line" :class="`boot-line--${line.tone}`">
            <span class="boot-marker" aria-hidden="true" />
            <span class="boot-text">{{ line.text }}</span>
            <span class="boot-index tiny" aria-hidden="true">{{ String(i + 1).padStart(2, '0') }}</span>
          </li>
        </ul>

        <div class="matrix" aria-hidden="true">
          <span v-for="n in 96" :key="n" class="matrix-dot" :style="{ animationDelay: `${(n % 12) * 90}ms` }" />
        </div>

        <footer class="status-foot">
          <span class="tiny">sandbox：优先 microVM，失败自动兜底</span>
        </footer>
      </aside>
    </div>

    <ul ref="statsHost" class="shell stats">
      <li v-for="(stat, i) in STATS" :key="stat.label" class="stat">
        <span class="stat-value">{{ shown[i] ?? 0 }}{{ stat.suffix }}</span>
        <span class="stat-label">{{ stat.label }}</span>
        <span class="stat-note tiny">{{ stat.note }}</span>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.hero {
  position: relative;
  overflow: hidden;
  padding-block: clamp(48px, 7vw, 96px) clamp(32px, 5vw, 56px);
  border-bottom: 2px solid var(--c-line);
  background:
    radial-gradient(120% 80% at 20% 0%, rgba(111, 242, 176, 0.07), transparent 60%),
    linear-gradient(180deg, #080c10, #0a0e12 60%, #080c10);
}

.hero-bg {
  opacity: 0.62;
}

.hero-grid {
  position: absolute;
  inset: 0;
  opacity: 0.16;
  pointer-events: none;
}

.hero-inner {
  position: relative;
  z-index: var(--z-content);
  display: grid;
  grid-template-columns: minmax(0, 1.24fr) minmax(300px, 0.76fr);
  gap: clamp(24px, 4vw, 56px);
  align-items: center;
}

/* ---------- 左侧文案 ---------- */
.lead {
  display: flex;
  flex-direction: column;
  gap: var(--grid-2);
}

.hero-eyebrow {
  font-size: var(--fs-micro);
  letter-spacing: 0.28em;
}

.blink {
  display: inline-block;
  width: 8px;
  height: 8px;
  background: var(--c-mint);
  animation: caret-blink 1.1s steps(1, end) infinite;
}

.title {
  /* 首屏标题单独收一档：中文字形宽，全站 --fs-hero 会在这里折行 */
  font-size: clamp(2.1rem, 4.4vw, 4.3rem);
  line-height: 1.06;
  letter-spacing: 0.01em;
  display: flex;
  flex-direction: column;
  gap: 6px;
  text-shadow: 3px 3px 0 rgba(0, 0, 0, 0.55);
}

.title-line--accent {
  color: var(--c-mint);
}

.lede {
  max-width: 60ch;
  font-size: 1.02rem;
  line-height: 1.85;
}

.lede strong {
  color: var(--c-text);
  font-weight: 400;
  border-bottom: 2px solid var(--c-amber);
}

.chips {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-wrap: wrap;
  gap: 8px 18px;
}

.chips li {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-size: var(--fs-small);
  color: var(--c-text-dim);
}

.chip-icon {
  color: var(--c-mint);
}

/* ---------- 安装命令 ---------- */
.install {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 14px;
  max-width: 640px;
}

.install-prompt {
  font-family: var(--font-mono);
  color: var(--c-amber);
  font-size: var(--fs-small);
}

.install-cmd {
  flex: 1;
  min-width: 0;
  overflow-x: auto;
  white-space: nowrap;
  color: var(--c-text);
  font-size: 0.9rem;
}

.install-copy {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  background: var(--c-surface-2);
  border: 2px solid var(--c-line-strong);
  color: var(--c-text-dim);
  font-family: inherit;
  font-size: var(--fs-small);
  cursor: pointer;
  transition: color var(--t-fast) var(--ease-step), border-color var(--t-fast) var(--ease-step);
}

.install-copy:hover {
  color: var(--c-mint);
  border-color: var(--c-mint);
}

/* ---------- 右侧状态面板 ---------- */
.status {
  padding: 0;
  overflow: hidden;
}

.status-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 12px;
  border-bottom: 2px solid var(--c-line);
  background: var(--c-surface-2);
}

.status-dot {
  width: 8px;
  height: 8px;
  background: var(--c-mint);
  animation: caret-blink 1.6s steps(1, end) infinite;
}

.status-live {
  margin-left: auto;
  color: var(--c-mint);
  letter-spacing: 0.24em;
}

.boot {
  list-style: none;
  margin: 0;
  padding: 14px 14px 10px;
  display: grid;
  gap: 6px;
  font-family: var(--font-mono);
  font-size: 0.82rem;
}

.boot-line {
  display: grid;
  grid-template-columns: 10px 1fr auto;
  align-items: center;
  gap: 8px;
  opacity: 0;
  animation: boot-line-in 420ms steps(4, end) forwards;
}

.boot-line:nth-child(1) {
  animation-delay: 120ms;
}
.boot-line:nth-child(2) {
  animation-delay: 280ms;
}
.boot-line:nth-child(3) {
  animation-delay: 440ms;
}
.boot-line:nth-child(4) {
  animation-delay: 600ms;
}
.boot-line:nth-child(5) {
  animation-delay: 760ms;
}
.boot-line:nth-child(6) {
  animation-delay: 920ms;
}

@keyframes boot-line-in {
  from {
    opacity: 0;
    transform: translateX(-6px);
  }
  to {
    opacity: 1;
    transform: translateX(0);
  }
}

.boot-marker {
  width: 8px;
  height: 8px;
  background: var(--c-line-strong);
}

.boot-line--ok .boot-marker {
  background: var(--c-mint);
}

.boot-line--key .boot-marker {
  background: var(--c-amber);
}

.boot-line--key .boot-text {
  color: var(--c-amber);
}

.boot-line--ok .boot-text {
  color: var(--c-text-dim);
}

.boot-line--dim .boot-text {
  color: var(--c-text-mute);
}

.boot-text {
  overflow-x: auto;
  white-space: nowrap;
}

.boot-index {
  color: var(--c-text-mute);
}

.matrix {
  display: grid;
  grid-template-columns: repeat(24, 1fr);
  gap: 4px;
  padding: 12px 14px;
  border-top: 2px dashed rgba(34, 49, 60, 0.7);
}

.matrix-dot {
  width: 100%;
  aspect-ratio: 1;
  background: var(--c-mint);
  opacity: 0.08;
}

.status-foot {
  padding: 8px 14px;
  border-top: 2px solid var(--c-line);
  background: var(--c-surface-2);
}

/* ---------- 数字条 ---------- */
.stats {
  position: relative;
  z-index: var(--z-content);
  list-style: none;
  margin: clamp(32px, 5vw, 64px) auto 0;
  padding: 0;
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  border: 2px solid var(--c-line);
  background: rgba(10, 14, 18, 0.72);
}

.stat {
  padding: 18px 20px;
  border-right: 2px solid var(--c-line);
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.stat:last-child {
  border-right: 0;
}

.stat-value {
  font-family: var(--font-pixel);
  font-size: clamp(1.8rem, 3vw, 2.6rem);
  color: var(--c-mint);
  line-height: 1.1;
}

.stat-label {
  font-family: var(--font-pixel);
  font-size: 0.95rem;
  color: var(--c-text);
}

.stat-note {
  color: var(--c-text-mute);
  letter-spacing: 0.08em;
}

@media (max-width: 992px) {
  .hero-inner {
    grid-template-columns: minmax(0, 1fr);
  }

  .stats {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .stat:nth-child(2) {
    border-right: 0;
  }

  .stat:nth-child(1),
  .stat:nth-child(2) {
    border-bottom: 2px solid var(--c-line);
  }
}

@media (max-width: 560px) {
  .stats {
    grid-template-columns: minmax(0, 1fr);
  }

  .stat {
    border-right: 0;
    border-bottom: 2px solid var(--c-line);
  }

  .stat:last-child {
    border-bottom: 0;
  }
}
</style>
