# 官网前端设计契约（像素 / 位图风）

> 本文件是 `website/` 目录的开发约定，供人和其他 Agent 共同遵守。任何新组件都必须复用这里的令牌与工具类。

## 1. 技术栈与目录

- Vue 3.5 + TypeScript（`strict`、`noUnusedLocals`、`noUnusedParameters`）+ Vite 7
- UI 库：[Pixelium Design](https://shika-works.github.io/pixelium-design/zh/guide/intro)（npm 包 `@pixelium/web-vue`，像素风组件库）
- 目录：
  - `src/main.ts` 入口（注册全局样式、`v-reveal` 指令、主题）
  - `src/App.vue` 页面装配
  - `src/components/*.vue` 区块组件
  - `src/directives/reveal.ts` 入场动画指令
  - `src/styles/{tokens,base,animations}.css` 设计令牌与全局样式
  - `src/data/*.ts` 文案数据（可选）

**禁止**：新增第三方依赖、引入 Tailwind/UnoCSS、使用圆形圆角、使用库外组件、运行 `npm install`。

## 2. 视觉方向

- 主题：**磷光 CRT 终端 + 8bit 位图**。深底、硬边、无圆角、发光克制。
- 一切"高级感"来自：像素栅格对齐（4px 的整数倍）、`steps()` 低帧率动画、网点/抖动底纹、扫描线、图层错位色差。
- 严禁：渐变光球、玻璃拟态、柔和阴影、字体渲染模糊。所有 `img` 一律 `image-rendering: pixelated`（已在 base.css 全局设置）。

## 3. 设计令牌（`src/styles/tokens.css`，已存在，直接用 `var()`）

颜色：`--c-void --c-bg --c-bg-soft --c-surface --c-surface-2 --c-surface-3 --c-line --c-line-strong`
文本：`--c-text --c-text-dim --c-text-mute`
强调：`--c-mint --c-mint-deep --c-amber --c-amber-deep --c-cyan --c-violet --c-rose --c-lime`
语义别名：`--bg --surface --surface-2 --line --text --text-dim --text-mute --accent --accent-alt`
像素：`--bit`(4px) `--grid`(8px) `--grid-2` `--grid-3`
阴影：`--shadow-hard` `--shadow-hard-sm` `--glow-mint` `--glow-amber` `--glow-cyan`
字体：`--font-pixel`（Fusion Pixel 像素字体）`--font-mono` `--font-sans`
字号：`--fs-hero --fs-h2 --fs-h3 --fs-body --fs-small --fs-micro`
布局：`--page`(1240) `--page-wide`(1600) `--gutter` `--section-y`
动效：`--t-fast` `--t-base` `--t-slow` `--ease-step` `--ease-step-6` `--ease-out-hard`
层级：`--z-bg --z-content --z-sticky --z-overlay --z-boot`

## 4. 已有工具类（`src/styles/base.css` / `animations.css`）

结构：`.shell` `.shell-wide` `.section`（含上边框与纵向留白）`.section--flush` `.stack` `.row`
面板：`.panel`（硬边双层描边 + 硬阴影）`.panel--flat` `.pixel-cut`（8bit 斜切角）
底纹：`.dither` `.dither-dense` `.grid-lines` `.scanlines`（需父级 `position:relative`）`.vignette`
文本：`.eyebrow` `.muted` `.tiny` `.text-accent` `.text-amber` `.rule`
交互：`.hover-lift`
动画：`v-reveal` 指令（进场，可传 `{ delay: 120 }` 或数字）；`.caret` `.scan-sweep` `.marquee`（配合 `.marquee-host` 悬停暂停）`.sprite-blink` `.float-y` `.pulse-line` `.glitch-host` + `.glitch` `.spin-step` `.matrix-dot`

## 5. `v-reveal` 用法

```vue
<div v-reveal>默认入场</div>
<li v-reveal="{ delay: 90 * i }">交错入场</li>
```
内部用 IntersectionObserver 给元素加 `.is-in`（配合 `.reveal`/`.reveal-pixel` 的 `steps()` 动画），只在首次进入视口时触发。

## 6. 文案与语气

- 全站中文；面向 Windows 开发者，**不吹牛、不写未实现的能力**，每个能力都能对应到仓库里的实现。
- 术语沿用产品文档：`execute_code`、`CodeAct`、Hyperlight microVM、Monty 沙箱、宿主工具（C#）、`run_shell`、PowerShell 7.6、`doctor`、Skills、MCP、DLL 插件、子代理（explore/general/investigate/modify/review/verify）、权限档位 `full / ask / plan`、数据目录 `.lanagent/data`。
- 命令一律用等宽字体 + `.panel` 代码块呈现；可复制的内容用 `CopyButton`/`PxButton` 风格。

## 7. 代码规范

- 一律 `<script setup lang="ts">` + `<style scoped>`；组件文件名用大驼峰。
- 事件、ref、computed 都要有类型；**不允许 `any`**；未使用的导入/变量必须删除（会构建失败）。
- 颜色/间距只能取令牌，禁止写 `#fff`、`12px` 之外的魔法值（个别 1~2px 的像素补偿可例外，需注释）。
- 组件必须响应式：小屏（<768px）单列、大屏多列；横向不出现滚动条。
- 所有动画必须在 `prefers-reduced-motion: reduce` 下失效（`animations.css` 已有全局降级，自写 keyframes 时不要绕过）。
- 自写 keyframes 放在组件的 `<style scoped>` 里，命名 `kebab-case`，时长 ≤ 6s，避免 `box-shadow`/`filter` 大面积高频动画（性能）。

## 8. 库 API 速查（`@pixelium/web-vue`）

```ts
// 按需导入（推荐），样式已在 main.ts 全局引入
import { Button, Tag, Card, Collapse, CollapseItem, Timeline, TimelineItem,
         CountTo, Typewriter, Carousel, Bubble, Alert, Progress, Divider,
         Space, TextOutline, Icon, Link } from '@pixelium/web-vue/es'
```
- `PxButton`：`theme`(primary/success/warning/danger/info/sakura/notice)、`variant`(primary/plain/outline/text)、`size`(small/medium/large)、`shape`(rect/round/circle/square)、`color`、`block`、`loading`、`disabled`、插槽 `icon`。
- `Tag`：`theme` `variant` `size` `color` `closable`。
- `Card`：`title` `bordered` `shape`；插槽 `default`。
- `Collapse`/`CollapseItem`：`active`/`default-active`、`accordion`、`variant`；`CollapseItem` 必填 `index`、`title`。FAQ 用它。
- `Timeline`/`TimelineItem`：`direction`、`content-span`；`TimelineItem` 有 `title` `content` `footer` `theme` `mark`。
- `CountTo`：`to` `from` `duration` `precision` `start-delay` `autoplay` `separator` `formatter`。
- `Alert`：`type`(info/success/warning/error/normal/notice) `variant` `title` `closable` `show-icon`。
- `Progress`：`percentage` `theme` `size` `variant`(solid/checkered) `indicator-placement`。
- `TextOutline`：`color` `outline-width` `auto-padding`（标题描边）。
- `Bubble`：`variant`(plain/primary) `theme` `tail` `tail-placement`。
- `Divider`：`direction` `margin` `variant`。`Space`：`margin`(small/medium/large) `direction` `wrap`。
- 全局已在 `main.ts` 启用暗色主题（`html.dark`）与像素字体。

## 9. 自检

- 只写文件，**不要**执行 `npm install` / `npm run build`（父代理会统一 typecheck 与构建）。
- 完成后回报：改动文件清单 + 每个文件的一句话说明 + 任何 API 不确定点。
