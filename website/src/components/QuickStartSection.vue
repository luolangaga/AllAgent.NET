<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'
import PixelIcon from '@/components/PixelIcon.vue'

/**
 * QuickStartSection —— 三步上手：① 安装 ② 检查环境 ③ 选择模型并描述任务。
 *
 * 命令文本逐字取自 website/SITE-CONTENT.md 的「安装」一节（含
 * C:\Projects\MyProject 这种写法）；源码里写成双反斜杠，复制出去的才是单反斜杠。
 * 不要"美化"这里的命令、路径和大小写。
 */

type CommandBlock = {
  /** 复制反馈的键：同一时刻只有一个块显示「已复制 / 手动复制」 */
  key: string
  /** 代码块标题栏里的用途标签 */
  label: string
  /** 一行一条命令，复制时用换行符拼接 */
  lines: string[]
}

type DoctorItem = {
  icon: string
  title: string
  detail: string
}

/** ① 安装（与 SITE-CONTENT.md 逐字一致） */
const INSTALL_BLOCKS: CommandBlock[] = [
  {
    key: 'install',
    label: '安装 · 验证',
    lines: [
      'dotnet tool install --global AllAgent.NET.Terminal',
      'allagent --help',
      'allagent doctor',
    ],
  },
  {
    key: 'install-chat',
    label: '在该工作区进入会话',
    lines: ['allagent chat --workspace C:\\Projects\\MyProject'],
  },
]

const MAINTAIN_BLOCK: CommandBlock = {
  key: 'maintain',
  label: '升级 / 卸载',
  lines: [
    'dotnet tool update --global AllAgent.NET.Terminal',
    'dotnet tool uninstall --global AllAgent.NET.Terminal',
  ],
}

/** ② 体检：doctor 本身只检查，加参数才经 winget 补装 */
const DOCTOR_BLOCKS: CommandBlock[] = [
  { key: 'doctor', label: '检查环境', lines: ['allagent doctor'] },
  { key: 'doctor-install', label: '补齐缺失依赖', lines: ['allagent doctor --install'] },
]

const DOCTOR_ITEMS: DoctorItem[] = [
  { icon: 'cpu', title: '宿主 Python 3.10–3.14', detail: '宿主解释器版本区间' },
  { icon: 'terminal', title: 'PowerShell 7.6.x', detail: '固定调度器，不回退 5.1' },
  { icon: 'search', title: 'ripgrep', detail: '文件搜索与检索是否可用' },
  { icon: 'layers', title: '共享库环境', detail: 'python/venv/ 与已装库版本' },
  { icon: 'book', title: 'pip.md', detail: '按实际元数据生成并注入模型上下文' },
  { icon: 'sandbox', title: '实际沙箱执行', detail: '真的跑一次沙箱，而不是只看版本号' },
]

/** ③ 环境变量方式（与 SITE-CONTENT.md 逐字一致） */
const ENV_BLOCK: CommandBlock = {
  key: 'env',
  label: '环境变量方式 · Key 不写进项目',
  lines: [
    "$env:ALLAGENT_PROVIDER = 'openai'",
    "$env:ALLAGENT_BASE_URL = 'https://api.openai.com/v1'",
    "$env:ALLAGENT_MODEL = '<模型 ID>'",
    "$env:ALLAGENT_API_KEY = '<你的 API Key>'",
    '.\\agent.ps1 chat --workspace C:\\Projects\\MyProject',
  ],
}

const COPY_TTL = 1500
const FAIL_TTL = 2600

type CopyState = 'idle' | 'done' | 'fail'

const activeKey = ref('')
const activeState = ref<CopyState>('idle')
let resetTimer: ReturnType<typeof setTimeout> | null = null

function clearResetTimer(): void {
  if (resetTimer !== null) {
    clearTimeout(resetTimer)
    resetTimer = null
  }
}

/** 组件卸载时清掉定时器，避免组件销毁后再去改它的 ref */
onBeforeUnmount(clearResetTimer)

function flash(key: string, state: CopyState, ttl: number): void {
  activeKey.value = key
  activeState.value = state
  clearResetTimer()
  resetTimer = setTimeout(() => {
    resetTimer = null
    activeKey.value = ''
    activeState.value = 'idle'
  }, ttl)
}

function stateOf(key: string): CopyState {
  return activeKey.value === key ? activeState.value : 'idle'
}

function buttonText(key: string): string {
  const state = stateOf(key)
  if (state === 'done') {
    return '已复制'
  }
  if (state === 'fail') {
    return '手动复制'
  }
  return '复制'
}

/** 降级方案一：隐藏 textarea + execCommand（老 WebView / 非安全上下文可用） */
function copyWithTextarea(text: string): boolean {
  try {
    const area = document.createElement('textarea')
    area.value = text
    area.setAttribute('readonly', 'readonly')
    area.style.position = 'fixed'
    area.style.top = '0'
    area.style.left = '-9999px'
    document.body.appendChild(area)
    area.select()
    const ok = typeof document.execCommand === 'function' && document.execCommand('copy')
    document.body.removeChild(area)
    return ok
  } catch {
    return false
  }
}

async function writeClipboard(text: string): Promise<boolean> {
  const clip = typeof navigator === 'undefined' ? undefined : navigator.clipboard
  if (clip !== undefined && typeof clip.writeText === 'function') {
    try {
      await clip.writeText(text)
      return true
    } catch {
      // 权限被拒：继续走降级方案
    }
  }
  return copyWithTextarea(text)
}

/** 两种方案都失败时按钮会变成「手动复制」，提示用户自己选中命令 */
async function copyBlock(block: CommandBlock): Promise<void> {
  const ok = await writeClipboard(block.lines.join('\n'))
  flash(block.key, ok ? 'done' : 'fail', ok ? COPY_TTL : FAIL_TTL)
}
</script>

<template>
  <section id="quickstart" class="section quickstart">
    <div class="shell">
      <header class="qs-head">
        <p v-reveal class="eyebrow">QUICK START</p>
        <h2 v-reveal="{ delay: 90 }" class="qs-title">三步把它接进你的工作区</h2>
        <p v-reveal="{ delay: 140 }" class="qs-lead muted">
          <code>allagent</code> 是 NuGet 上的 .NET 全局工具：装完先让 <code>doctor</code> 体检，
          再挑模型、描述任务。下面每个命令块都能一键复制。
        </p>
      </header>

      <div class="steps">
        <!-- ① 安装 -->
        <article v-reveal class="step step--install">
          <div class="step__head">
            <span class="step__num" aria-hidden="true">01</span>
            <h3 class="step__title">安装</h3>
          </div>
          <p class="step__desc muted">
            一条 <code>dotnet tool install</code> 装到全局，<code>--help</code> 确认命令可用。
          </p>

          <figure
            v-for="block in INSTALL_BLOCKS"
            :key="block.key"
            class="cmd panel panel--flat"
            :class="{ 'is-done': stateOf(block.key) === 'done' }"
          >
            <figcaption class="cmd__bar">
              <span class="cmd__label tiny">{{ block.label }}</span>
              <button
                type="button"
                class="copy"
                :class="{ 'is-done': stateOf(block.key) === 'done', 'is-fail': stateOf(block.key) === 'fail' }"
                :aria-label="`复制：${block.label}`"
                aria-live="polite"
                @click="copyBlock(block)"
              >
                <PixelIcon :name="stateOf(block.key) === 'done' ? 'check' : 'clipboard'" :size="14" />
                <span>{{ buttonText(block.key) }}</span>
              </button>
            </figcaption>
            <div class="cmd__body">
              <div v-for="(line, index) in block.lines" :key="`${block.key}-${index}`" class="cmd__line">
                <span class="cmd__prompt" aria-hidden="true">&gt;</span>
                <code class="cmd__text">{{ line }}</code>
              </div>
            </div>
          </figure>

          <figure
            class="cmd panel panel--flat"
            :class="{ 'is-done': stateOf(MAINTAIN_BLOCK.key) === 'done' }"
          >
            <figcaption class="cmd__bar">
              <span class="cmd__label tiny">{{ MAINTAIN_BLOCK.label }}</span>
              <button
                type="button"
                class="copy"
                :class="{
                  'is-done': stateOf(MAINTAIN_BLOCK.key) === 'done',
                  'is-fail': stateOf(MAINTAIN_BLOCK.key) === 'fail',
                }"
                :aria-label="`复制：${MAINTAIN_BLOCK.label}`"
                aria-live="polite"
                @click="copyBlock(MAINTAIN_BLOCK)"
              >
                <PixelIcon :name="stateOf(MAINTAIN_BLOCK.key) === 'done' ? 'check' : 'clipboard'" :size="14" />
                <span>{{ buttonText(MAINTAIN_BLOCK.key) }}</span>
              </button>
            </figcaption>
            <div class="cmd__body">
              <div v-for="(line, index) in MAINTAIN_BLOCK.lines" :key="`maintain-${index}`" class="cmd__line">
                <span class="cmd__prompt" aria-hidden="true">&gt;</span>
                <code class="cmd__text">{{ line }}</code>
              </div>
            </div>
          </figure>
        </article>

        <!-- ② 检查环境 -->
        <article v-reveal="{ delay: 90 }" class="step step--doctor">
          <div class="step__head">
            <span class="step__num" aria-hidden="true">02</span>
            <h3 class="step__title">检查环境</h3>
          </div>
          <p class="step__desc muted">
            <code>doctor</code> 逐项体检本机，只报告不安装软件。
          </p>

          <figure
            v-for="block in DOCTOR_BLOCKS"
            :key="block.key"
            class="cmd panel panel--flat"
            :class="{ 'is-done': stateOf(block.key) === 'done' }"
          >
            <figcaption class="cmd__bar">
              <span class="cmd__label tiny">{{ block.label }}</span>
              <button
                type="button"
                class="copy"
                :class="{ 'is-done': stateOf(block.key) === 'done', 'is-fail': stateOf(block.key) === 'fail' }"
                :aria-label="`复制：${block.label}`"
                aria-live="polite"
                @click="copyBlock(block)"
              >
                <PixelIcon :name="stateOf(block.key) === 'done' ? 'check' : 'clipboard'" :size="14" />
                <span>{{ buttonText(block.key) }}</span>
              </button>
            </figcaption>
            <div class="cmd__body">
              <div v-for="(line, index) in block.lines" :key="`${block.key}-${index}`" class="cmd__line">
                <span class="cmd__prompt" aria-hidden="true">&gt;</span>
                <code class="cmd__text">{{ line }}</code>
              </div>
            </div>
          </figure>

          <ul class="doc-list">
            <li v-for="item in DOCTOR_ITEMS" :key="item.title" class="doc-item">
              <PixelIcon class="doc-item__icon" :name="item.icon" :size="16" />
              <span>
                <strong class="doc-item__title">{{ item.title }}</strong>
                <span class="doc-item__detail">{{ item.detail }}</span>
              </span>
            </li>
          </ul>

          <p class="step__desc muted">
            补齐依赖会经 <code>winget</code> 安装 Python / PowerShell，装完提示重启。
          </p>
        </article>

        <!-- ③ 选择模型并描述任务 -->
        <article v-reveal="{ delay: 180 }" class="step step--model">
          <div class="step__head">
            <span class="step__num" aria-hidden="true">03</span>
            <h3 class="step__title">选模型，描述任务</h3>
          </div>

          <div class="ways">
            <div class="way">
              <p class="way__label tiny">方式 A · 终端里切换</p>
              <ul class="keys">
                <li><kbd>F2</kbd> 打开模型选择器，切换后会记住</li>
                <li><code>/model</code> 直接在输入框切换</li>
              </ul>
              <p class="step__desc muted">
                OpenAI Chat / Responses、Anthropic、Gemini，以及任何兼容 OpenAI 协议的服务（含 Ollama）。
              </p>
            </div>

            <div class="way">
              <p class="way__label tiny">方式 B · 环境变量</p>
              <figure
                class="cmd panel panel--flat"
                :class="{ 'is-done': stateOf(ENV_BLOCK.key) === 'done' }"
              >
                <figcaption class="cmd__bar">
                  <span class="cmd__label tiny">{{ ENV_BLOCK.label }}</span>
                  <button
                    type="button"
                    class="copy"
                    :class="{
                      'is-done': stateOf(ENV_BLOCK.key) === 'done',
                      'is-fail': stateOf(ENV_BLOCK.key) === 'fail',
                    }"
                    :aria-label="`复制：${ENV_BLOCK.label}`"
                    aria-live="polite"
                    @click="copyBlock(ENV_BLOCK)"
                  >
                    <PixelIcon :name="stateOf(ENV_BLOCK.key) === 'done' ? 'check' : 'clipboard'" :size="14" />
                    <span>{{ buttonText(ENV_BLOCK.key) }}</span>
                  </button>
                </figcaption>
                <div class="cmd__body">
                  <div v-for="(line, index) in ENV_BLOCK.lines" :key="`env-${index}`" class="cmd__line">
                    <span class="cmd__prompt" aria-hidden="true">&gt;</span>
                    <code class="cmd__text">{{ line }}</code>
                  </div>
                </div>
              </figure>
            </div>
          </div>

          <p class="step__desc muted">
            然后在输入框里描述任务：文件、命令与桌面操作都经 C# 宿主工具执行，
            <kbd>Ctrl</kbd>+<kbd>E</kbd> 展开执行代码与工具结果，双击 <kbd>Esc</kbd> 停止任务。
          </p>
        </article>
      </div>

      <p class="qs-foot muted">
        默认权限档 <code>full</code>：宿主工具不逐次审批、也不授予管理员权限。
      </p>
    </div>
  </section>
</template>

<style scoped>
.quickstart {
  padding-top: var(--section-y);
}

.qs-head {
  display: grid;
  gap: var(--grid-2);
  margin-bottom: var(--grid-3);
}

.qs-title {
  font-size: var(--fs-h2);
}

.qs-lead {
  font-size: var(--fs-body);
}

.steps {
  display: grid;
  gap: var(--grid-3);
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 420px), 1fr));
  align-items: start;
}

/* ---------- 步骤卡：大号数字色块 + 顶部色条 ---------- */
.step {
  --tone: var(--c-mint);
  display: flex;
  flex-direction: column;
  gap: var(--grid-2);
  min-width: 0;
  padding: var(--grid-3);
  background: var(--c-surface);
  border: 2px solid var(--c-line);
  border-top: var(--bit) solid var(--tone);
  box-shadow: inset 0 0 0 2px rgba(6, 8, 10, 0.9), var(--shadow-hard);
}

.step--doctor {
  --tone: var(--c-amber);
}

.step--model {
  --tone: var(--c-cyan);
}

.step__head {
  display: flex;
  align-items: center;
  gap: var(--grid-2);
}

.step__num {
  display: grid;
  place-items: center;
  flex: none;
  width: calc(var(--grid-3) * 2);
  height: calc(var(--grid-3) * 2);
  background: var(--tone);
  color: var(--c-void);
  font-family: var(--font-pixel);
  font-size: var(--fs-h3);
  line-height: 1;
  box-shadow: var(--shadow-hard-sm);
}

.step__title {
  font-size: var(--fs-h3);
  color: var(--text);
}

.step__desc {
  font-size: var(--fs-small);
}

/* ---------- 命令块 + 复制按钮 ---------- */
.cmd {
  display: flex;
  flex-direction: column;
  min-width: 0;
  margin: 0;
  background: var(--c-void);
}

.cmd.is-done {
  border-color: var(--c-mint);
  animation: qs-flash 420ms steps(4, end) 1;
}

@keyframes qs-flash {
  0% {
    background: var(--c-void);
  }
  45% {
    background: var(--c-surface-3);
  }
  100% {
    background: var(--c-void);
  }
}

.cmd__bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--grid);
  padding: var(--bit) var(--grid);
  background: var(--c-surface-2);
  border-bottom: 2px solid var(--c-line);
}

.cmd__label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cmd__body {
  padding-block: var(--grid);
}

.cmd__line {
  display: flex;
  align-items: baseline;
  gap: var(--bit);
  padding-inline: var(--grid);
  font-size: var(--fs-small);
  line-height: 1.9;
  /* 长命令软换行，任何一种屏宽都不出现横向滚动 */
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.cmd__prompt {
  color: var(--c-mint-deep);
  user-select: none;
}

.cmd__text {
  flex: 1 1 auto;
  min-width: 0;
  color: var(--c-text);
}

.copy {
  display: inline-flex;
  align-items: center;
  gap: var(--bit);
  flex: none;
  padding: 2px var(--grid); /* 2px：像素补偿，与 kbd 描边对齐 */
  border: 2px solid var(--c-line-strong);
  border-bottom-width: var(--bit);
  background: var(--c-surface-3);
  color: var(--text-dim);
  font-family: var(--font-pixel);
  font-size: var(--fs-micro);
  letter-spacing: 0.08em;
  line-height: 1.6;
  cursor: pointer;
  transition: color var(--t-fast) var(--ease-step), background var(--t-fast) var(--ease-step),
    border-color var(--t-fast) var(--ease-step), transform var(--t-fast) var(--ease-step);
}

.copy:hover {
  color: var(--text);
  background: var(--c-surface-2);
  border-color: var(--c-mint);
}

.copy:active {
  transform: translateY(1px); /* 1px：像素补偿 */
}

.copy.is-done {
  color: var(--c-mint);
  border-color: var(--c-mint);
}

.copy.is-fail {
  color: var(--c-amber);
  border-color: var(--c-amber);
}

/* ---------- doctor 检查项 ---------- */
.doc-list {
  display: grid;
  gap: var(--grid);
  margin: 0;
  padding: 0;
  list-style: none;
}

.doc-item {
  display: flex;
  align-items: flex-start;
  gap: var(--grid);
  padding: var(--grid);
  background: var(--c-bg-soft);
  border: 2px solid var(--c-line);
}

.doc-item__icon {
  color: var(--c-amber);
  margin-top: 2px; /* 2px：像素补偿，与首行文字对齐 */
}

.doc-item__title {
  display: block;
  font-family: var(--font-mono);
  font-size: var(--fs-small);
  color: var(--text);
}

.doc-item__detail {
  display: block;
  font-size: var(--fs-micro);
  color: var(--text-mute);
}

/* ---------- 模型与任务 ---------- */
.ways {
  display: grid;
  gap: var(--grid-2);
}

.way {
  display: grid;
  gap: var(--grid);
}

.way + .way {
  padding-top: var(--grid-2);
  border-top: 2px solid var(--c-line);
}

.way__label {
  color: var(--c-cyan);
}

.keys {
  display: grid;
  gap: var(--grid);
  margin: 0;
  padding: 0;
  list-style: none;
  font-size: var(--fs-small);
  color: var(--text-dim);
}

.qs-foot {
  margin-top: var(--grid-3);
  font-size: var(--fs-small);
}
</style>
