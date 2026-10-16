# LanAgent 官网（website/）

LanAgent 的官方落地页：Vue 3 + Vite + TypeScript，像素 / 位图风格，组件来自
[Pixelium Design](https://shika-works.github.io/pixelium-design/zh/guide/intro)（`@pixelium/web-vue`）。

## 本地开发

```powershell
cd website
npm install
npm run dev          # http://localhost:5188
```

> 想跳过首屏的开机动画（截图 / 自动化检查）：访问 `http://localhost:5188/?noboot=1`。

## 构建与预览

```powershell
npm run typecheck     # vue-tsc --noEmit
npm run build         # typecheck + vite build → dist/
npm run preview       # 预览 dist/
```

## 目录结构

```
src/
  App.vue                 页面装配（主题、像素尺寸、主色都在这里初始化）
  main.ts                 入口：全局样式、v-reveal 指令
  components/             区块组件（Hero / 终端演示 / 能力 / 沙箱 / 界面 / 流程 / 快速开始 / FAQ / 页脚）
  directives/reveal.ts    v-reveal 滚动入场指令
  styles/                 tokens.css（设计令牌）· base.css（基础与工具类）· animations.css（steps 动画）
public/screenshot.jpg     产品界面截图（取自 docs/screenshots/）
CONTRIBUTING-PIXEL.md     设计契约：令牌、工具类、组件库 API、代码规范
SITE-CONTENT.md           产品事实清单与区块文案规格
```

## 约定

- 所有颜色、间距、动效节奏都走 `src/styles/tokens.css` 的 CSS 变量，组件里不写裸值。
- 像素感来自：4px 像素栅格、硬边描边（无圆角）、`steps()` 低帧率动画、网点/抖动底纹、扫描线。
- 动画必须尊重 `prefers-reduced-motion`（`animations.css` 里有全局降级）。
- 新组件请先读 `CONTRIBUTING-PIXEL.md`，文案以 `SITE-CONTENT.md` 的事实为准。
