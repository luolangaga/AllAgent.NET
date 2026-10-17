<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'

/**
 * 首次进入的开机动画。
 *
 * 注意这里**不做任何滚动锁**：遮罩只负责画面，退场由 CSS 动画收尾
 * （最后一帧 visibility: hidden + pointer-events: none），
 * 即使计时器被浏览器节流，页面也不会被挡住或滚不动。
 */
const visible = ref(false)
const progress = ref(0)
const lines = ['allagent.sys', 'mount workspace', 'spawn sandbox', 'attach host tools', 'ready']

let timers: ReturnType<typeof setTimeout>[] = []
let rafId = 0

function finish(): void {
  visible.value = false
}

onMounted(() => {
  const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
  // ?noboot=1 用于截图 / 自动化检查时跳过开机动画
  const skip = new URLSearchParams(window.location.search).has('noboot')
  if (reduced || skip) {
    return
  }
  visible.value = true

  const started = performance.now()
  const step = (now: number): void => {
    progress.value = Math.min(1, (now - started) / 1200)
    if (progress.value < 1) {
      rafId = window.requestAnimationFrame(step)
    }
  }
  rafId = window.requestAnimationFrame(step)
  timers.push(setTimeout(finish, 1700))
})

onBeforeUnmount(() => {
  for (const t of timers) {
    clearTimeout(t)
  }
  timers = []
  if (rafId !== 0) {
    window.cancelAnimationFrame(rafId)
  }
})
</script>

<template>
  <div v-if="visible" class="boot" role="presentation" @click="finish">
    <div class="boot-box">
      <p class="boot-brand">AllAgent.NET</p>
      <ul class="boot-list">
        <li v-for="(line, i) in lines" :key="line" :class="{ 'is-on': progress * lines.length > i }">
          <span class="boot-dot" />
          {{ line }}
        </li>
      </ul>
      <div class="boot-bar">
        <span class="boot-fill" :style="{ width: `${(progress * 100).toFixed(1)}%` }" />
      </div>
      <p class="boot-hint">点击跳过</p>
    </div>
  </div>
</template>

<style scoped>
.boot {
  position: fixed;
  inset: 0;
  z-index: var(--z-boot);
  display: grid;
  place-items: center;
  background: #05080a;
  cursor: pointer;
  /* 1.8s 后自己退场：即使 JS 计时被节流，最后一帧也会变成不可见、不可点击 */
  animation: boot-out 1800ms steps(9, end) forwards;
}

@keyframes boot-out {
  0%,
  82% {
    opacity: 1;
    visibility: visible;
    pointer-events: auto;
  }
  100% {
    opacity: 0;
    visibility: hidden;
    pointer-events: none;
  }
}

.boot-box {
  width: min(420px, 82vw);
  border: 2px solid var(--c-line);
  background: var(--c-surface);
  padding: 20px;
  box-shadow: var(--shadow-hard);
}

.boot-brand {
  font-family: var(--font-pixel);
  font-size: 1.5rem;
  letter-spacing: 0.3em;
  color: var(--c-mint);
  margin-bottom: 14px;
}

.boot-list {
  list-style: none;
  margin: 0 0 14px;
  padding: 0;
  display: grid;
  gap: 6px;
  font-family: var(--font-mono);
  font-size: 0.8rem;
  color: var(--c-text-mute);
}

.boot-list li {
  display: flex;
  align-items: center;
  gap: 8px;
}

.boot-list li.is-on {
  color: var(--c-text-dim);
}

.boot-dot {
  width: 8px;
  height: 8px;
  background: var(--c-line-strong);
}

.is-on .boot-dot {
  background: var(--c-mint);
}

.boot-bar {
  height: 10px;
  border: 2px solid var(--c-line);
  background: #0a0e12;
  padding: 2px;
}

.boot-fill {
  display: block;
  height: 100%;
  background: var(--c-mint);
}

.boot-hint {
  margin-top: 10px;
  font-size: var(--fs-micro);
  letter-spacing: 0.2em;
  text-transform: uppercase;
  color: var(--c-text-mute);
  text-align: right;
}
</style>
