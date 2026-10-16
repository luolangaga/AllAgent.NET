import type { Directive } from 'vue'

/**
 * v-reveal —— 滚动进入视口时触发一次像素风入场动画。
 *
 * 用法：
 *   <div v-reveal />
 *   <li v-reveal="120" />            // 延迟 120ms
 *   <li v-reveal="{ delay: 90 }" />
 *
 * 元素会被加上 `reveal` 与 `is-in` 类，动画由 animations.css 里的
 * steps() 关键帧负责，保证低帧率的位图节奏。
 */
export type RevealOptions = {
  /** 入场延迟（毫秒），常用于列表交错 */
  delay?: number
  /** 使用像素裁剪揭示而不是普通位移 */
  pixel?: boolean
}

type RevealValue = RevealOptions | number | undefined

let observer: IntersectionObserver | null = null

function getObserver(): IntersectionObserver | null {
  if (typeof IntersectionObserver === 'undefined') {
    return null
  }
  if (observer === null) {
    observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) {
            continue
          }
          const el = entry.target as HTMLElement
          el.classList.add('is-in')
          observer?.unobserve(el)
        }
      },
      { rootMargin: '0px 0px -8% 0px', threshold: 0.08 },
    )
  }
  return observer
}

function normalize(value: RevealValue): Required<RevealOptions> {
  if (typeof value === 'number') {
    return { delay: value, pixel: false }
  }
  return { delay: value?.delay ?? 0, pixel: value?.pixel ?? false }
}

export const reveal: Directive<HTMLElement, RevealValue> = {
  mounted(el, binding) {
    const { delay, pixel } = normalize(binding.value)
    el.classList.add(pixel ? 'reveal-pixel' : 'reveal')
    if (delay > 0) {
      el.style.setProperty('--d', `${delay}ms`)
    }

    const io = getObserver()
    if (io === null) {
      el.classList.add('is-in')
      return
    }
    io.observe(el)
  },
  unmounted(el) {
    observer?.unobserve(el)
  },
}
