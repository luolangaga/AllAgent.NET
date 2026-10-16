<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'

/**
 * PixelField —— Hero 背景的位图动画。
 *
 * 低分辨率离屏画布（128×72 像素格）+ 有序抖动（Bayer 4×4）着色，
 * 再用 CSS `image-rendering: pixelated` 放大，得到真正的位图质感而不是模糊渐变。
 * 帧率被限制在 ~20fps，保持"低帧率"的复古节奏，同时避免占满 CPU。
 */
const GRID_W = 128
const GRID_H = 72
const FRAME_MS = 50

const host = ref<HTMLCanvasElement | null>(null)

let rafId = 0
let lastFrame = 0
let started = 0
let imageData: ImageData | null = null
let io: IntersectionObserver | null = null
let visible = true

/** 2×2 → 4×4 的 Bayer 抖动矩阵 */
const BAYER = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5]

/** 四档调色，全部带透明度，叠加在页面底色上 */
const LEVELS: [number, number, number, number][] = [
  [111, 242, 176, 0],
  [111, 242, 176, 15],
  [111, 242, 176, 32],
  [255, 201, 92, 26],
]

function draw(t: number): void {
  const canvas = host.value
  if (canvas === null) {
    return
  }
  const ctx = canvas.getContext('2d')
  if (ctx === null) {
    return
  }
  if (imageData === null) {
    imageData = ctx.createImageData(GRID_W, GRID_H)
  }
  const data = imageData.data

  for (let y = 0; y < GRID_H; y += 1) {
    for (let x = 0; x < GRID_W; x += 1) {
      const cx = (x - GRID_W / 2) / (GRID_W / 2)
      const cy = (y - GRID_H / 2) / (GRID_H / 2)
      const radius = Math.sqrt(cx * cx + cy * cy)

      // 三组正弦叠加出一条向内收拢的波纹，越靠外越稀疏
      const wave =
        Math.sin(x * 0.13 + t * 1.1) * 0.5 +
        Math.sin(y * 0.21 - t * 0.7) * 0.3 +
        Math.sin(radius * 7.5 - t * 1.6) * 0.55

      const density = wave * 0.5 + 0.5 - radius * 0.55
      const threshold = (BAYER[(y % 4) * 4 + (x % 4)] + 0.5) / 16
      const level = density > threshold ? (density > 0.78 ? 2 : 1) : 0

      // 一条 1 像素高的扫描束缓慢扫过，保持克制
      const sweep = Math.abs(((t * 9) % (GRID_H + 40)) - y) < 1 ? 1 : 0
      const palette = LEVELS[sweep === 1 ? 3 : level]

      const index = (y * GRID_W + x) * 4
      data[index] = palette[0]
      data[index + 1] = palette[1]
      data[index + 2] = palette[2]
      data[index + 3] = palette[3]
    }
  }

  ctx.putImageData(imageData, 0, 0)
}

function loop(now: number): void {
  rafId = window.requestAnimationFrame(loop)
  if (!visible || now - lastFrame < FRAME_MS) {
    return
  }
  lastFrame = now
  draw((now - started) / 1000)
}

onMounted(() => {
  const canvas = host.value
  if (canvas === null) {
    return
  }
  const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches
  canvas.width = GRID_W
  canvas.height = GRID_H
  started = performance.now()
  draw(reduce ? 12 : 0)

  if (reduce) {
    return
  }

  if (typeof IntersectionObserver !== 'undefined') {
    io = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          visible = entry.isIntersecting
        }
      },
      { threshold: 0 },
    )
    io.observe(canvas)
  }

  rafId = window.requestAnimationFrame(loop)
})

onBeforeUnmount(() => {
  if (rafId !== 0) {
    window.cancelAnimationFrame(rafId)
  }
  io?.disconnect()
  io = null
})
</script>

<template>
  <canvas ref="host" class="pixel-field" aria-hidden="true" />
</template>

<style scoped>
.pixel-field {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  display: block;
  image-rendering: pixelated;
  image-rendering: crisp-edges;
  pointer-events: none;
  opacity: 0.62;
}
</style>
