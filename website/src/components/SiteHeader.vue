<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { Button as PxButton } from '@pixelium/web-vue/es'
import PixelIcon from '@/components/PixelIcon.vue'

/** 顶部导航：粘性像素条 + 滚动进度方块。 */
const NAV: { href: string; label: string }[] = [
  { href: '#demo', label: '终端' },
  { href: '#capabilities', label: '能力' },
  { href: '#sandbox', label: '沙箱' },
  { href: '#workflow', label: '流程' },
  { href: '#quickstart', label: '快速开始' },
  { href: '#faq', label: '常见问题' },
]

const REPO_URL = 'https://github.com/luolangaga/AllAgent.NET'

const compact = ref(false)
const progress = ref(0)
const menuOpen = ref(false)

let rafId = 0

function measure(): void {
  rafId = 0
  const doc = document.documentElement
  const max = doc.scrollHeight - doc.clientHeight
  progress.value = max > 0 ? Math.min(1, doc.scrollTop / max) : 0
  compact.value = doc.scrollTop > 24
}

function onScroll(): void {
  if (rafId !== 0) {
    return
  }
  rafId = window.requestAnimationFrame(measure)
}

function goQuickStart(): void {
  menuOpen.value = false
  document.querySelector('#quickstart')?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

onMounted(() => {
  measure()
  window.addEventListener('scroll', onScroll, { passive: true })
  window.addEventListener('resize', onScroll, { passive: true })
})

onBeforeUnmount(() => {
  window.removeEventListener('scroll', onScroll)
  window.removeEventListener('resize', onScroll)
  if (rafId !== 0) {
    window.cancelAnimationFrame(rafId)
  }
})
</script>

<template>
  <header class="bar" :class="{ 'bar--compact': compact }">
    <div class="shell bar-inner">
      <a class="brand" href="#top" @click="menuOpen = false">
        <PixelIcon name="terminal" :size="26" class="brand-icon" />
        <span class="brand-text">ALLAGENT</span>
        <span class="brand-badge tiny">GPL-3.0</span>
      </a>

      <nav class="nav" aria-label="页面导航">
        <a v-for="item in NAV" :key="item.href" class="nav-link" :href="item.href">{{ item.label }}</a>
      </nav>

      <div class="actions">
        <a class="ghost" :href="REPO_URL" target="_blank" rel="noreferrer noopener">
          <PixelIcon name="branch" :size="18" />
          <span class="ghost-text">GitHub</span>
        </a>
        <PxButton theme="primary" variant="outline" size="small" @click="goQuickStart">安装</PxButton>
        <button
          class="menu-toggle"
          type="button"
          :aria-expanded="menuOpen"
          aria-label="切换导航菜单"
          @click="menuOpen = !menuOpen"
        >
          <span class="menu-line" />
          <span class="menu-line" />
          <span class="menu-line" />
        </button>
      </div>
    </div>

    <nav v-if="menuOpen" class="menu" aria-label="移动端导航">
      <a v-for="item in NAV" :key="item.href" :href="item.href" @click="menuOpen = false">{{ item.label }}</a>
      <a :href="REPO_URL" target="_blank" rel="noreferrer noopener" @click="menuOpen = false">GitHub 仓库</a>
    </nav>

    <div class="progress" aria-hidden="true">
      <span class="progress-fill" :style="{ width: `${(progress * 100).toFixed(2)}%` }" />
    </div>
  </header>
</template>

<style scoped>
.bar {
  position: sticky;
  top: 0;
  z-index: var(--z-sticky);
  background: rgba(8, 12, 15, 0.94);
  border-bottom: 2px solid var(--c-line);
  backdrop-filter: none;
}

.bar-inner {
  display: flex;
  align-items: center;
  gap: var(--grid-2);
  height: 68px;
  transition: height var(--t-base) var(--ease-step);
}

.bar--compact .bar-inner {
  height: 56px;
}

.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  border-bottom: 0;
  color: var(--c-text);
}

.brand-icon {
  color: var(--c-mint);
}

.brand-text {
  font-family: var(--font-pixel);
  font-size: 1.05rem;
  letter-spacing: 0.22em;
}

.brand-badge {
  color: var(--c-text-mute);
  border: 2px solid var(--c-line);
  padding: 1px 6px;
}

.nav {
  display: flex;
  gap: 2px;
  margin-left: auto;
}

.nav-link {
  position: relative;
  padding: 6px 12px;
  color: var(--c-text-dim);
  border-bottom: 0;
  font-size: var(--fs-small);
  letter-spacing: 0.08em;
}

.nav-link::after {
  content: '';
  position: absolute;
  left: 10px;
  right: 10px;
  bottom: 2px;
  height: 2px;
  background: var(--c-mint);
  transform: scaleX(0);
  transform-origin: left;
  transition: transform var(--t-fast) var(--ease-step);
}

.nav-link:hover {
  color: var(--c-text);
}

.nav-link:hover::after {
  transform: scaleX(1);
}

.actions {
  display: flex;
  align-items: center;
  gap: var(--grid);
  margin-left: auto;
}

.ghost {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  border: 2px solid var(--c-line);
  color: var(--c-text-dim);
  font-size: var(--fs-small);
}

.ghost:hover {
  border-color: var(--c-amber);
  color: var(--c-amber);
}

.menu-toggle {
  display: none;
  flex-direction: column;
  gap: 3px;
  padding: 8px;
  background: transparent;
  border: 2px solid var(--c-line);
  cursor: pointer;
}

.menu-line {
  display: block;
  width: 16px;
  height: 2px;
  background: var(--c-text-dim);
}

.menu {
  display: none;
  flex-direction: column;
  border-top: 2px solid var(--c-line);
  background: #080c0f;
}

.menu a {
  padding: 12px var(--gutter);
  border-bottom: 2px dashed rgba(34, 49, 60, 0.7);
  color: var(--c-text-dim);
}

.progress {
  position: absolute;
  left: 0;
  right: 0;
  bottom: -2px;
  height: 2px;
  background: rgba(34, 49, 60, 0.6);
}

.progress-fill {
  display: block;
  height: 100%;
  background: var(--c-mint);
  box-shadow: 0 0 12px -2px rgba(111, 242, 176, 0.8);
}

@media (max-width: 1080px) {
  .nav {
    display: none;
  }

  .menu-toggle {
    display: flex;
  }

  .menu {
    display: flex;
  }
}

@media (max-width: 560px) {
  .ghost-text {
    display: none;
  }

  .brand-badge {
    display: none;
  }
}
</style>
