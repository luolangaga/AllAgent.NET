<script setup lang="ts">
import { onMounted } from 'vue'
import { BackTop as PxBackTop, setPixelSize, setThemeColor, useThemeMode } from '@pixelium/web-vue/es'
import BootOverlay from '@/components/BootOverlay.vue'
import CapabilityGrid from '@/components/CapabilityGrid.vue'
import FaqSection from '@/components/FaqSection.vue'
import HeroSection from '@/components/HeroSection.vue'
import QuickStartSection from '@/components/QuickStartSection.vue'
import SandboxDiagram from '@/components/SandboxDiagram.vue'
import SiteFooter from '@/components/SiteFooter.vue'
import SiteHeader from '@/components/SiteHeader.vue'
import TerminalPreview from '@/components/TerminalPreview.vue'
import WorkflowSection from '@/components/WorkflowSection.vue'

// 全站固定深色像素主题：组件库的 :root.dark 变量在 setup 阶段就会被应用
useThemeMode('dark')
// 4px 像素点，和 tokens.css 里的 --bit 对齐
setPixelSize(4)
// 主色换成磷光薄荷绿，让组件库的按钮/标签与站点配色一致
setThemeColor('primary', '#3fd39a')

// 异步挂载：浏览器初始的 #hash 定位会落空，这里补一次并在加载完成后校正
function scrollToHash(): void {
  const hash = window.location.hash
  if (hash.length < 2) {
    return
  }
  document.querySelector(hash)?.scrollIntoView({ behavior: 'auto', block: 'start' })
}

onMounted(() => {
  window.requestAnimationFrame(scrollToHash)
  window.addEventListener('load', scrollToHash, { once: true })
  setTimeout(scrollToHash, 700)
})
</script>

<template>
  <div class="page">
    <BootOverlay />
    <SiteHeader />

    <main>
      <HeroSection />
      <TerminalPreview />
      <CapabilityGrid />
      <SandboxDiagram />
      <WorkflowSection />
      <QuickStartSection />
      <FaqSection />
    </main>

    <SiteFooter />

    <PxBackTop :visibility-height="640" :right="20" :bottom="20" />
    <div class="crt" aria-hidden="true" />
  </div>
</template>

<style scoped>
.page {
  position: relative;
  min-height: 100vh;
  background: var(--bg);
}

.crt {
  position: fixed;
  inset: 0;
  z-index: var(--z-overlay);
  pointer-events: none;
  background:
    repeating-linear-gradient(
      to bottom,
      rgba(0, 0, 0, 0) 0 2px,
      rgba(0, 0, 0, 0.1) 3px,
      rgba(0, 0, 0, 0.1) 4px
    ),
    radial-gradient(140% 100% at 50% 0%, transparent 55%, rgba(3, 5, 6, 0.55) 100%);
  mix-blend-mode: multiply;
  opacity: 0.7;
}
</style>
