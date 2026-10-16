import { createApp } from 'vue'

// Pixelium Design：像素风组件库（样式三个入口都来自 dist）
import '@pixelium/web-vue/dist/pixelium-vue.css'
import '@pixelium/web-vue/dist/font.css'
import '@pixelium/web-vue/dist/normalize.css'

// 本项目的设计令牌 → 基础样式 → 动画，顺序不能改（后者覆盖前者）
import '@/styles/tokens.css'
import '@/styles/base.css'
import '@/styles/animations.css'

import App from '@/App.vue'
import { reveal } from '@/directives/reveal'

const app = createApp(App)
app.directive('reveal', reveal)
app.mount('#app')
