# LanAgent 环境使用反馈（官网开发 + 内置浏览器实测）

> 元信息
> - 日期：2026-10-16
> - 会话任务：为 LanAgent 做一个像素 / 位图风官网（`website/`，Vue 3 + Vite + Pixelium Design），并在内置浏览器里实测前端
> - 执行者：宿主 Agent（CodeAct，Python 在 Monty 沙箱，I/O 全部走 C# 宿主工具）
> - 证据来源：`browser` 工具的 `evaluate` 实测输出、`run_shell` / `shell_status` 返回、宿主机实测命令
> - 关联产出：[`website/README.md`](../website/README.md)、[`website/CONTRIBUTING-PIXEL.md`](../website/CONTRIBUTING-PIXEL.md)、[`website/SITE-CONTENT.md`](../website/SITE-CONTENT.md)

---

## 0. 摘要（按优先级）

| # | 问题 | 影响 | 建议 |
|---|---|---|---|
| 1 | 内置 WebView2 页面在后台被挂起：动画时钟停摆、`document.getAnimations()` 为空、`transitionend` 不触发 | Vue `<transition>` 的元素会永久滞留在 DOM；官网首屏遮罩因此锁死滚动，成为**用户可见 Bug** | `browser status` 暴露「页面是否被挂起/遮挡」；对被遮挡但 `visibilityState === "visible"` 的页面不要停渲染 |
| 2 | 保存的 CDP 端点过期时，`open` 只报 `Unable to connect to the remote server` | 无法区分「没连过 / 端点已死 / 等授权」，Agent 会做无效重试 | 结构化错误码 + 直接给出「请执行 `/browser reconnect`」；用户显式要求其它模式时允许一次性降级 |
| 3 | 后台 shell 会静默中断（`state: 7`，`应用退出中断任务`） | 预览服务死掉后，后续截图「成功但无文件」，排查成本高（我误判为 Edge 问题） | 后台任务终止时主动通知；或在依赖端口的调用上提示「该端口已不可用」 |
| 4 | `file_search` 匹配项的 `file` 字段有时为 `null` | 输出形如 `None:10: ...`，无法定位文件 | 保证 `file` / 路径字段始终存在 |
| 5 | 图片附件超限是硬拒绝 | 1440×11200 的全页截图被判定超限后直接从上下文移除，无法核对下半页 | `computer_screenshot` / `file_read(image)` 自动降采样或分段；或返回可裁剪的句柄 |
| 6 | Monty 沙箱 `os` 是子集（无 `os.path`） | 报 `AttributeError: 'module' object has no attribute 'path'`，需要猜边界 | 错误信息里直接写明能力边界与替代写法 |
| 7 | `subagent` 并行批量失败时顶层错误过于笼统 | 5 个子任务 4 个成功，顶层只有「A delegated task failed」，成功的产出容易被整批重跑 | 顶层错误带上失败 `taskId` 列表与成功计数 |
| 8 | 三处小摩擦：`web_fetch` 丢链接、抓 npm registry 超时、`ask_user` 选项无法表达「只改某一块」 | 拖慢调研与沟通 | 见 §8 |

---

## 1. 内置 WebView2 在后台被挂起（最该修）

**现象**

- 用内置浏览器 `reload` 页面后 3～4 秒，首屏遮罩 `.boot` 仍在 DOM 中，`opacity: 1`、`visibility: visible`、`pointer-events: auto`。
- 同一时刻对遮罩取动画：`document.getAnimations()` 过滤后返回 **空数组**（动画根本没开始跑），而 `setTimeout(..., 100)` 探针却返回 `timerFired: true`。
- 该页面由 Vue 的 `<transition>` 包裹：`transitionend` 不来 → 元素不卸载；此前该遮罩还锁了 `document.body.style.overflow = 'hidden'`，于是页面既被盖住又滚不动（用户上次反馈的「整个页面没办法滚动」）。

**实测（一次完整探针）**

```json
{
  "visibilityState": "visible",
  "hidden": false,
  "hasFocus": false,
  "overlayExists": true,
  "animationCount": 0,
  "animationCurrentTime": null,
  "timerFired": true,
  "elapsedMs": 605
}
```

**影响**

- UI 自动化验证不可靠：所有依赖「动画走完」的状态（遮罩退出、滚动揭示、过渡收尾）都可能停在中间态。
- 对被测站点提出过高要求：任何「用 JS 在若干秒后解锁页面」的实现都会踩坑。

**已做的站点侧兜底（`website/src/components/BootOverlay.vue`）**

1. 退场改为纯 CSS 动画的最后一帧：`opacity: 0; visibility: hidden; pointer-events: none`，即使 JS 计时被节流，遮罩也不会再挡住点击。
2. **彻底移除滚动锁**，不再触碰 `document.body.style.overflow`。
3. 保留 `?noboot=1` 供截图 / 自动化跳过。

**环境侧建议**

- `browser status` / `state` 增加 `suspended` 或 `renderingActive` 之类的字段。
- 若页面 `visibilityState === "visible"` 且窗口只是被遮挡，尽量不冻结动画时钟与 rAF。

---

## 2. `browser` 连接过期时的错误与模式约束

**现象**

- `status`：`running: false`，`cdpUrl: ws://127.0.0.1:58184/...`，`browserName: Microsoft Edge`，`awaitingBrowserAuthorization: false`。
- 实测该端口已关闭（`urlopen` → `WinError 10061`，且 `msedge.exe` 进程数为 **0**）。
- `open` 返回：`Unable to connect to the remote server`。
- 同时用 `mode: "builtin"` 打开被拒：`Browser mode is selected by the user. Saved mode: chromium. Do not switch browsers; use the saved mode or ask the user to change it in /browser.`
- 用户在 `/browser` 切到内置模式后，同样形式的 `open` 立即成功，返回 `browser.mode = "builtin"`，`profile = %APPDATA%\LanAgent\browser-use\webview2`。

**影响**

- 三态（未连接 / 端点已死 / 等授权）被压成一句网络错误；Agent 倾向重试而不是请用户重连。
- 文档建议「不要在 `open` 里指定 mode」，但用户口头要求「用内置浏览器」时没有合法通道。

**建议**

- `open` 失败时返回结构化原因，例如 `{"reason":"staleEndpoint","hint":"/browser reconnect","endpoint":"ws://127.0.0.1:58184"}`。
- 允许在「保存模式的端点不可用」且用户显式点名模式时做一次性切换（或直接在错误里提示“请在 /browser 里切换”）。

---

## 3. 后台 shell 静默中断导致连锁假故障

**现象**

- `vite preview` 后台任务（`run_shell background=true`）随后变成 `state: 7`，`error: 应用退出中断任务；请显式重新发起`。
- 浏览器那一侧表现为：截图命令 `exitCode 0`，但文件不存在；`--dump-dom` 输出为空；页面停留在上一次内容。

**影响**

- 我先后把它误判为 Edge headless 限制、profile 锁、图片服务问题，浪费了多轮排查。

**建议**

- 后台任务终止时推送一次通知（或让 `shell_status` 在被再次轮询前保持显著标记）。
- 在 `browser open` / `run_shell` 返回里附带「本地端口可用性」提示（例如 `4173: closed`）。

---

## 4. `file_search` 匹配项缺少文件路径

**现象**：一次批量搜索（`pattern` 里含 `|` 交替）返回的行形如：

```
None:10: import ShowcaseSection from '@/components/ShowcaseSection.vue'
None:49:   <section id="showcase" class="section showcase">
```

文本与行号正确，`file` 字段为 `null`。同一批次其它 pattern 的路径正常。

**建议**：匹配对象始终带绝对或工作区相对路径；缺失时显式报错而不是填空。

---

## 5. 图片附件超限是硬拒绝

**现象**：`file_read` 一张 1440×11200 的整页截图，返回：

```
[附件 bottom.png 被图片服务拒绝，已从模型上下文移除；原始附件已备份。]
```

同会话中 1440×1020 的截图正常；900×2250 经降采样后也能送达（`processingNote: Original 900x2250; attached 800x2000`）。

**影响**：验证长页面时必须自己找工具裁切——我是用 `Add-Type -AssemblyName System.Drawing` 写 `.ps1` 裁剪 + 缩放才看上下半页。

**建议**：超限时自动降采样 / 纵向切片，或返回「可裁剪句柄 + 尺寸提示」。

---

## 6. Monty 沙箱的能力边界提示

| 报错 | 场景 | 期望 |
|---|---|---|
| `NameError: name 'json' is not defined` | 沙箱里没 `import json` | 文档已写明“每个 execute_code 都要显式 import”，这个算我的问题；错误信息可以补一句提示 |
| `AttributeError: 'module' object has no attribute 'path'` | `os.path.getsize(...)`，沙箱的 `os` 无 `path` 子模块 | 错误里直接说明「Monty 的 os 不提供 path」，或列出可用子集 |

---

## 7. `subagent` 并行批量的顶层错误

**现象**：5 个任务并行，4 个成功产出（文件已落盘），1 个失败；顶层返回：

```
ok: false
error: A delegated task failed, was cancelled or needs parent input. Inspect results; subsequent chain steps were not executed.
```

而 `data.results` 里 4 份完整产出都在。

**影响**：只看 `ok/error` 的调用方会整批重跑，浪费 token 与时间。

**建议**：顶层错误里带 `failed: [taskId]`、`succeeded: n`，并在文案里明确「results 仍含成功产出」。

---

## 8. 三处小摩擦

1. **`web_fetch` 丢失链接**：VitePress 站点转文本后 `href` 全部消失，`/zh/guide/quick-start`、`/zh/guide/` 又都是 404（真实路径是 `/zh/guide/starting`）。最后是用宿主 Python + `urllib` 抓 HTML 里的 `href` 才把文档树列全。建议提供「同站链接提取」或再抓一次原始 HTML 的能力。
2. **抓 npm registry 超时**：为查版本抓 5 个包的完整 registry JSON，`run_shell` 180s 超时。建议宿主提供轻量包查询（只取 `dist-tags`）。
3. **`ask_user` 无法表达「只改一块」**：我给的四个选项里没有“保留整体风格、只重做终端预览”，用户选了“整站风格重做”，于是我按最坏情况大改了一轮样式（随后按要求全部还原）。建议允许“选项 + 补充说明”，或支持 `freeform` 优先。

---

## 附录 A：站点侧最终状态（可核对）

- `website/` 结构：`src/components/{HeroSection,TerminalPreview,CapabilityGrid,SandboxDiagram,WorkflowSection,QuickStartSection,FaqSection,SiteFooter,SiteHeader,PixelIcon,PixelField,BootOverlay}.vue`，`src/styles/{tokens,base,animations}.css`，`src/directives/reveal.ts`。
- 校验：`npx vue-tsc --noEmit` → `0`；`npx vite build` → `0`（约 1.0–1.4s，468 modules）。
- 预览服务：`npm run preview` → `http://127.0.0.1:4173/`（`?noboot=1` 跳过开机动画）。
- 终端预览区块的还原依据（源码位置）：
  - 颜色：`src/LanAgent.Terminal/TerminalTheme.cs` 的 `night` 主题（`#11131A` / `#1A1D27` / `#7DCFFF` / `#DCE1EB`）。
  - 布局：`src/LanAgent.Terminal/FullScreenTerminal.cs`（消息区 + `left["input"]` 的 Heavy 边框「输入任务」面板 + 右栏 `Sidebar()` + 底部三行 footer）。
  - 工具卡片：同一文件里 `new Panel(new Text(summary)).Header(TerminalUi.ToolLabel(...))`。
  - 审批列表：`ApprovalPreview.cs` 的 `❯ 1. 允许这一次`。
- 实测截图（在 `.gitignore` 的 `artifacts/site/`，仅本机）：`v3-top.png`（首屏）、`v4-faq.png`、`tall.png`、`bottom-crop.png`，以及本次终端预览的浏览器实测截图。

## 附录 B：环境侧最小复现清单

1. 内置 WebView2 打开一个「`v-if + <transition>` 遮罩 + `body.style.overflow = 'hidden'`」的页面 → 把浏览器窗口切到后台 5s → 回到前台观察遮罩是否退出、页面能否滚动。
2. 让保存的 CDP 端点失效（关掉带 `--remote-debugging-port` 的 Edge），再调 `browser open` → 观察错误文案是否可执行。
3. 起一个 `run_shell background=true` 的本地服务，等它被中断，然后做一次依赖该端口的 `browser screenshot` → 观察是否有明确提示。
4. 用 `[AgentTool]` 搜索类工具搜一个带 `|` 交替的 pattern → 检查返回项的路径字段。
5. `subagent` 并行 5 个任务，故意让 1 个失败 → 检查顶层错误是否列出失败 id。
