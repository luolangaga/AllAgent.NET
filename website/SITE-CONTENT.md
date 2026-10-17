# 官网文案与区块规格（事实清单）

> 所有数字与能力都来自仓库 `README.md`、`docs/allagent-advantages.zh-CN.md`。**不要发明新功能、新数字、新链接**。
> 站点语言：中文（简体），语气：技术、克制、有图像感；避免"革命性/颠覆"这类营销词。

## 产品定位（一句话）

AllAgent.NET 是**面向 Windows 本机工作流**的终端 AI Agent：模型侧只有 `execute_code` 一个入口，用 Python 组织步骤；Python 在隔离沙箱中运行；文件、命令与桌面操作全部经 **C# 宿主工具**受控执行。

## 事实清单（可直接引用）

- **宿主工具 35 个**（`[AgentTool]` 计数），其中 **12 个**是 `computer_*` 桌面工具。
- **命令执行**：`run_shell` 把 `exe + args` 序列化 → Base64，交给固定可信脚本 `run_shell.ps1`，以 `pwsh -NoLogo -NoProfile -NonInteractive -CommandWithArgs` 启动；**禁止命令字符串/模板**，参数不会被 PowerShell 二次解析。
- **环境**：PowerShell **7.6.x**（已验证的调度器，不回退 5.1）、Python **3.10–3.14** 宿主解释器、ripgrep、`.NET 8`（需支持 `.slnx` 的 SDK）。
- **沙箱双后端**：优先 **Hyperlight 微虚拟机**（需虚拟化，启动失败自动兜底）；兜底 **Monty**（Rust 实现的 Python 子集，**不需要**虚拟化、Docker 或管理员权限）。worker 只保留 Windows 系统环境变量，**模型服务凭据不进 worker**；每次执行恢复干净快照；Monty 内存上限 **128 MiB**；执行中失败**不自动换后端重放**代码，避免重复副作用。
- **权限三档**：`full`（默认，宿主工具不逐次审批、允许工作区外路径、**不授予管理员权限**）、`ask`（逐次授权，默认选中"允许这一次"）、`plan`（只读）。
- **文件工具**：`file_read` / `file_write` / `file_edit` / `list_directory` / `file_search` / `everything_search`，带页级与字节预算、diff 预览、原子写。
- **Windows 路径闸门**：拒绝 UNC 工作区、歧义名（`CON/PRN/AUX/NUL/COM1…`、点或空格结尾）、备用数据流（`README.md:secret`）、普通模式下的 reparse point。
- **截图进对话**：复制 Windows 截图后在输入框按 **Ctrl+V** 提交；支持原生 PNG 与 DIB/DIBV5 剪贴板；也可用 `@截图.png` 引用路径，图片随消息保存进会话。
- **Computer Use**：FlaUI / UIA3，12 个 `computer_*` 工具；**只由用户**用 `/computer on|off` 开关；AI 只能选择 `background`（UIA 语义操作，不需要焦点）、`foreground`（物理键鼠）、`isolated`（独立 Windows 会话 Worker，loopback + Bearer token）。有动作时在所有显示器显示柔光提示层；只枚举/截图不触发提示。
- **Browser Use**：默认开启，由 C# 实现。内置模式用 **WebView2 独立窗口**；外置模式自动启动并连接 Chrome/Edge。支持网页 JS（含 Promise）、页面观察、鼠标输入、标签页与截图；无需安装 Python 浏览器库或下载 Chromium。`/browser` 管理面板，`/browser on|off` 总开关。
- **子代理**：`explore / general / investigate / modify / review / verify` 六种角色；支持单任务、**并行（最多 8 个任务、6 个并发）**、串行链；子代理向主代理返回结果与阻塞原因，停止会等待实际收尾。
- **扩展**：内置 Skills、MCP 服务、DLL 插件；内置联网搜索与网页读取**不需要搜索 API Key**。
- **数据**：Terminal 数据默认在 `<workspace>/.allagent/data/`；`allagent.json` 是 v3 清单；每次会话修订存 `conversations/`；原子写 + fsync + 校验和回读；跨进程事务、单写入者租约；旧版 v2 迁移会保留备份。
- **环境检查**：`allagent doctor` / `/doctor` 检查 Python、PowerShell、ripgrep、共享库环境、pip、pip.md 与实际沙箱执行结果；`doctor install` 可经 `winget` 补装（安装 Python 3.13 / PowerShell 后提示重启）。检查本身不会安装软件。
- **共享库环境**：`python/venv/`，默认装 requests、beautifulsoup4、PyYAML、numpy、pypdf；`/install-pip` 安装并更新 `pip.md`，该文件按实际元数据生成并自动注入模型上下文。
- **模型**：OpenAI Chat / Responses、Anthropic、Gemini，以及任何兼容 OpenAI 协议的服务（含 Ollama）；F2 或 `/model` 切换，选择会记住。
- **快捷键**：F2 模型 · F3 恢复会话 · Ctrl+P 命令搜索 · F1 常用操作 · Ctrl+E 查看执行代码与工具结果 · Ctrl+B 侧栏 · Ctrl+J / Shift+Enter 多行 · Ctrl+V 贴图 · 双击 Esc 停止任务 · `/diff` `/undo` `/redo` · `/queue add` · `/steer` · `/sandbox` · `/status`。

## 安装（命令行，必须原样出现）

```powershell
dotnet tool install --global AllAgent.NET.Terminal
allagent --help
allagent doctor

allagent chat --workspace C:\Projects\MyProject
```

升级 / 卸载：`dotnet tool update --global AllAgent.NET.Terminal`、`dotnet tool uninstall --global AllAgent.NET.Terminal`。

环境变量方式（不把 Key 写进项目）：

```powershell
$env:ALLAGENT_PROVIDER = 'openai'
$env:ALLAGENT_BASE_URL = 'https://api.openai.com/v1'
$env:ALLAGENT_MODEL = '<模型 ID>'
$env:ALLAGENT_API_KEY = '<你的 API Key>'
.\agent.ps1 chat --workspace C:\Projects\MyProject
```

## 链接

- 仓库：https://github.com/luolangaga/AllAgent.NET
- 协议：GPL-3.0-only；第三方声明见 `THIRD-PARTY-NOTICES.md`
- 文档：`docs/browser-use.zh-CN.md`、`docs/computer-use.zh-CN.md`、`docs/codeact.zh-CN.md`、`docs/execution.zh-CN.md`、`docs/nuget-tool.zh-CN.md`、`docs/plugins.zh-CN.md`

## 区块规格（每个组件一个文件，默认导出为 `<script setup>` 组件）

每个区块组件必须自己输出 `<section class="section" id="...">`，内部第一层是 `<div class="shell">`；不要引入路由、不要请求网络、不要新增依赖。

| 组件 | id | 内容要点 |
|---|---|---|
| `CapabilityGrid.vue` | `capabilities` | 6 张能力卡片（位图图标 + 标题 + 描述 + 底部标签）：1) 结构化命令执行（`run_shell`，exe+args 字面量，PowerShell 7.6）2) CodeAct 单入口（`execute_code` + 35 个宿主工具）3) 双沙箱隔离（Hyperlight / Monty）4) Windows 路径闸门与目录授权 5) 截图直接进对话（Ctrl+V）6) 子代理与扩展（六角色 + Skills/MCP/插件）。鼠标悬停要有像素 glitch 或错位效果，卡片错峰入场。 |
| `SandboxDiagram.vue` | `sandbox` | 一张自绘的"执行链路"图：**模型 → execute_code → 沙箱（Hyperlight microVM ⇄ Monty 兜底）→ C# 宿主工具（文件 / 命令 / 桌面 / 浏览器）→ 本机 Windows**。连接线上要有像素方块沿路径流动的动画；沙箱两条路径要能表达"优先 / 兜底"；旁侧给 2–3 条要点（凭据不进 worker、每次执行干净快照、失败不重放）。 |
| `QuickStartSection.vue` | `quickstart` | 三步：① 安装（3 行命令，可复制）② 检查环境（`allagent doctor`，并列出它会检查的项）③ 选择模型并描述任务（F2 / 环境变量两种方式）。命令块要有复制按钮（用 `navigator.clipboard` 并降级），复制成功给出像素风反馈。 |
| `FaqSection.vue` | `faq` | 用 `PxCollapse` + `PxCollapseItem` 做 6 条 FAQ：默认权限是什么、沙箱为什么有两个、需要 Docker/管理员吗、会不会把我的 Key 给沙箱、数据存在哪、Windows 之外能用吗（当前 Agent 执行仅支持 Windows x64）。 |
| `SiteFooter.vue` | 无 | 页脚：Logo 与一句话简介、四列链接（产品 / 文档 / 资源 / 协议）、`GPL-3.0-only` 声明、Pixelium Design 与 Fusion Pixel 字体致谢、构建时间戳可选。 |
| `ShowcaseSection.vue` | `showcase` | 真实界面展示：用 `<img src="/screenshot.jpg">`（图片已在 `website/public/screenshot.jpg`）+ `image-rendering: pixelated`；进入视口时做一次"像素化 → 清晰"的揭示动画（可用 CSS 实现，别依赖外部库）；旁边列 3–4 条界面要点（工作区/对话、模型与 API、技能、MCP 服务、设置、最近对话）。 |
| `WorkflowSection.vue` | `workflow` | 子代理与任务控制：用 `PxTimeline` 展示 `explore → investigate → modify → review → verify` 的协作链；右侧列出运行中可用的控制（`/steer` 追加指导、`/queue add` 排队、双击 Esc 停止、`/diff` `/undo` `/redo`）。 |
