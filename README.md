<div align="center">

# AllAgent.NET

[![Release](https://img.shields.io/github/v/release/luolangaga/AllAgent.NET?style=for-the-badge&labelColor=1f2937&color=3b82f6&logo=github&logoColor=white)](https://github.com/luolangaga/AllAgent.NET/releases/latest)
[![NuGet](https://img.shields.io/nuget/v/AllAgent.NET.Terminal?style=for-the-badge&labelColor=1f2937&color=8b5cf6&logo=nuget&logoColor=white)](https://www.nuget.org/packages/AllAgent.NET.Terminal)
[![Platform](https://img.shields.io/badge/Windows-x64-0078d6?style=for-the-badge&labelColor=1f2937&logo=windows&logoColor=white)](#环境要求)
[![.NET](https://img.shields.io/badge/.NET-10-512bd4?style=for-the-badge&labelColor=1f2937&logo=dotnet&logoColor=white)](#从源码构建)
[![License](https://img.shields.io/badge/License-GPL--3.0--only-0078d6?style=for-the-badge&labelColor=1f2937)](LICENSE)

**在 Windows 上，用一句自然语言驱动你的开发工作流。**

面向 Windows 本机开发的终端 AI Agent：它读写工作区文件、调用本机开发工具、运行验证，并把模型生成的计算代码放进隔离沙箱执行。

[快速开始](#快速开始) · [核心能力](#核心能力一览) · [安全与隔离](#安全与隔离) · [开发与构建](#开发与构建) · [文档](#更多文档) · [许可证](#许可证)

</div>

---

## 核心能力一览

- **CodeAct 执行**：模型只拿到一个 `execute_code` 工具，用 Python 组织步骤；文件、搜索、Shell 等 I/O 全部回到 C# 宿主工具。见 [CodeAct 与 Hyperlight 接入](docs/codeact.zh-CN.md)。
- **沙箱双后端**：优先 Hyperlight 微虚拟机，初始化失败自动切换到 Monty 软件沙箱；两个后端都不可用时阻止执行。
- **本机工具**：`run_shell` 通过随包的 PowerShell SDK worker 调度受信程序；`powershell_script` 在具名持久会话中执行 PowerShell 代码并返回结构化对象。见 [宿主工具](docs/host-tools.zh-CN.md) 与 [PowerShell 持久会话](docs/powershell-session.zh-CN.md)。
- **文件与搜索**：`file_read` / `file_write` / `file_edit` / `list_directory`；按名称查找用内置 Everything 便携索引（`everything_search`），按内容搜索用 ripgrep（`file_search`）。见 [文件搜索](docs/file-search.zh-CN.md)。
- **会话与子代理**：历史分支、时间线、检查点与撤回；子代理提供调查、修改、审查、验证等角色，支持单任务、并行和串行链派发。见 [子代理配置](docs/subagent-configuration.zh-CN.md)。
- **浏览器与桌面操作**：Browser Use 由 C# 实现，走内置 WebView2 或外置 Chrome/Edge，`web_search` 借它打开搜索页取结果、`web_fetch` 读取网页正文；Computer Use 用 FlaUI 操作 Windows 界面，无需开关，技能按需加载。
- **三种界面**：终端（Spectre.Console）、`/web-ui` 浏览器工作台、`allagent gui` 原生窗口（WPF + WebView2），共享同一份工作区、会话与权限。
- **Web 工作台三件套**：顶栏浮窗提供工作区文件预览、完整的 Git 管理（源代码管理布局、AI 生成提交信息、彩色提交图、分支/贮藏/推送拉取）和集成终端（ConPTY + xterm.js，多标签且刷新不中断）。见 [Web 工作台：文件、Git 与终端](docs/webui-files-git-terminal.zh-CN.md)。
- **插件 Web 扩展**：插件通过 `allagent-plugin.json` 的 `web` 块向工作台贡献页面、自定义工具卡与命令面板入口；插件界面在独立源的沙箱 iframe 中自绘（任意框架），经 SDK 双向通信并实时同步主题与上下文。见 [插件 Web 前端扩展](docs/plugin-web-ui.zh-CN.md)。
- **多模型**：OpenAI Chat / Responses、Anthropic、Gemini、Ollama，以及兼容 OpenAI 协议的服务；也可用 `login` 登录受支持的账号或插件。

## 为什么适合在 Windows 上工作

### 直接调用已安装的开发工具

`git`、`rg`、`dotnet`、`node` 以及 `npm` / `npx` / `pnpm` / `yarn` 会被自动发现为可信别名，可直接检查 .NET 项目、修复构建、整理代码并跑验证；其他程序用 `--allow-shell 别名=绝对可执行文件路径` 加入可信列表。工具卡片显示实际参数、工作目录和结果。

### 隔离模型代码，保留宿主工具能力

模型的计算代码在沙箱里运行，文件、网络和进程操作通过受控工具交回宿主；每次执行使用干净会话。Monty 默认不挂载宿主文件、不开放网络，只通过 `call_tool` 桥接 C# 工具，并限制内存（128 MiB）、时间与输出。

### Windows 路径与目录授权交给宿主

文件工具由 C# 宿主解析路径，默认围绕当前工作区；访问外部目录时按权限模式询问，可以只允许本次，也可以在会话内记住该目录。Windows 备用数据流（ADS）会被拒绝。处理 `C:\Projects\...` 这类本机路径时，边界规则明确。

### 截图可以直接进入对话

复制 Windows 截图后，在输入框按 **Ctrl+V** 或 **Shift+Insert** 暂存图片，回车提交给支持视觉的模型；也可以直接引用路径：

```text
帮我分析这个报错 @截图.png
检查这张界面的排版 @"C:\Pictures\界面截图.png"
```

图片随消息一起提交给支持视觉的模型，排查窗口报错和界面问题不必另开网页上传。

### 环境问题在动手前暴露

`allagent doctor` 检查 Browser Use / WebView2、宿主 Python 3.10–3.14、内嵌 PowerShell SDK、ripgrep、PowerShell 能力、执行模式与工作区；`doctor --install` 在确认后通过 winget 补齐 Python 和 ripgrep。缺少必需环境时，Agent 会停止执行并说明原因，避免把配置错误当成任务失败。

## 快速开始

### 环境要求

| 项目 | 要求 |
|---|---|
| 系统 | Windows x64（Agent 执行目前只支持 Windows x64） |
| .NET | 源码构建需要 .NET 10 SDK；运行时要求 .NET 10 或更高（工具已允许主版本向前滚动） |
| Python | 3.10–3.14，需可执行；缺失或版本不符时按提示用 winget 安装 Python 3.13 |
| 虚拟化 | Hyperlight 需要硬件虚拟化与 Windows Hypervisor Platform；没有时自动使用 Monty，无需虚拟化 |
| PowerShell | 无需安装，随包嵌入 PowerShell SDK 7.6.7，由独立 worker 运行 |
| ripgrep | 内容搜索依赖它，`allagent doctor --install` 可补齐 |
| Node.js / npm | 只有构建 React 前端时需要，终端 Agent 执行不依赖 Node.js |
| 图形界面 | 原生窗口需要 Microsoft Edge WebView2 Runtime |

### 安装

**Windows 安装包（推荐）**：从 [Releases](https://github.com/luolangaga/AllAgent.NET/releases) 下载 `AllAgent.NET-<版本>-win-x64-setup.exe`。安装时可以选择创建「终端」和「桌面端」两个桌面快捷方式，并自动补齐 WebView2 运行时、Python 3.13 与 ripgrep；自包含发布，无需预装 .NET 运行时。详见 [Windows 安装包](docs/windows-installer.zh-CN.md)。

**通过 .NET Tool 安装**：

```powershell
dotnet tool install --global AllAgent.NET.Terminal
allagent --help
allagent doctor
```

升级或卸载：

```powershell
dotnet tool update --global AllAgent.NET.Terminal
dotnet tool uninstall --global AllAgent.NET.Terminal
```

从源码打包并用本地 NuGet 源安装的流程见 [.NET Tool 打包与安装](docs/nuget-tool.zh-CN.md)，本地构建 exe 安装包的流程见 [Windows 安装包](docs/windows-installer.zh-CN.md)。

### 首次运行与初始化

```powershell
allagent chat --workspace C:\Projects\MyProject
```

第一次进入聊天会初始化环境：内嵌 PowerShell SDK worker 实际启动自检，检查 ripgrep 与宿主 Python 版本，缺失时给出 winget 命令并在安装后提示重启。模型与扩展管理不需要启动沙箱，首次执行任务时才校验执行环境。离线或只改配置时可用 `--no-initialize` 跳过初始化。

### 配置模型

进入聊天后按 **F2** 选择提供商与模型，或用 `login` 登录受支持账号；`/providers edit 配置名` 编辑地址、Key、协议与参数，`/model` 随时切换。终端会记住上次的选择。

也可以临时用环境变量配置，不把 API Key 写进项目：

```powershell
$env:ALLAGENT_PROVIDER = 'openai'
$env:ALLAGENT_BASE_URL = 'https://api.openai.com/v1'
$env:ALLAGENT_MODEL = '<模型 ID>'
$env:ALLAGENT_API_KEY = '<你的 API Key>'
allagent chat --workspace C:\Projects\MyProject
```

### 图形界面与 Web 工作台

| 入口 | 行为 |
|---|---|
| `allagent -guimode` | 校验参数后派生一个隐藏宿主进程并立即返回，关闭窗口即结束 |
| `allagent gui` | 前台运行同一个窗口，Ctrl+C 结束；`--gui-url` 指向 Vite 开发服务器，`--gui-devtools` 打开窗口内开发者工具 |
| 聊天内 `/web-ui`（或 `allagent web-ui`） | 在浏览器打开 Web 工作台，与终端共享工作区、模型配置、权限和后台任务 |

```powershell
allagent -guimode
allagent gui --gui-url http://127.0.0.1:5173/
allagent web-ui
```

窗口是随包分发的 WPF + WebView2 外壳（`AllAgent.NET.Gui.exe`），同一数据目录只允许一个窗口。工作台页面包括对话、模型与 API、技能、工具、PowerShell、插件、MCP 服务、任务与设置；「PowerShell」页与终端的 `/powershell` 面板共享同一份模块目录，可以在浏览器里搜索 PowerShell Gallery、安装模块并查看导入验证后的导出命令。

## 日常使用

### 常用快捷键

| 操作 | 快捷键 |
|---|---|
| 切换模型 | F2、`/model` |
| 打开常用操作 | F1 |
| 选择或恢复会话 | F3、`/session` |
| 搜索全部命令 | Ctrl+P |
| 引用项目文件 | `@路径`，路径含空格用 `@"路径"` |
| 输入多行 | Ctrl+J、Shift+Enter |
| 搜索输入历史 | Ctrl+R |
| 显示或隐藏侧栏 | Ctrl+B |
| 粘贴文本或截图 | Ctrl+V、Shift+Insert |
| 停止当前任务 | Esc |
| 退出 | `/exit`、Ctrl+D、空输入时按两次 Ctrl+C |

### 常用命令

| 命令 | 用途 |
|---|---|
| `/model`、`/models`、`/providers` | 切换模型、查看可用模型、管理提供商配置 |
| `/permissions restricted\|ask\|full\|plan`、`/plan` | 切换权限模式 |
| `/sandbox status\|auto\|hyperlight\|monty` | 查看或切换沙箱 |
| `/questions default\|minimal\|balanced\|proactive\|status` | 调整 Agent 主动提问的倾向（0–3） |
| `/new`、`/session`、`/branch`、`/fork`、`/timeline` | 新建、恢复、复制与定位历史 |
| `/compact`、`/context`、`/stats`、`/cache` | 总结较早上下文（完整历史保留）、查看占用与累计 Token 用量、缓存策略 |
| `/diff`、`/undo`、`/redo`、`/checkpoints` | 查看文件修改、撤回与重做 |
| `/background`、`/tasks`、`/steer`、`/queue` | 后台任务、结果与授权、步骤边界指导、排队输入 |
| `/agents`、`/subagents`、`/parent`、`/children` | 子代理配置与父子会话切换 |
| `/skills`、`/plugins`、`/mcp`、`/hooks`、`/powershell` | 技能、插件、MCP、生命周期 Hook 与 PowerShell 能力 |
| `/browser`、`/computer` | 浏览器操作与桌面操作开关 |
| `/tools`、`/status`、`/doctor` | 工具契约、当前状态、环境检查 |
| `/help all` | 查看全部内置命令 |

### 权限模式

- **restricted（默认）**：限制模式。工作区内的文件读写和可信 Shell 别名（`git`、`rg`、`dotnet`、`node`、`npm` 等，可用 `--allow-shell` 扩展）直接执行；工作区外的路径、未信任的程序以及 MCP / 桌面 / 插件工具仍逐次授权。也可显式写 `--restricted` 或 `/permissions restricted`。
- **full**：完全访问。宿主工具通常不再逐次审批，允许工作区外路径；沙箱隔离仍然生效，且不授予管理员权限。`--full-access` 或 `/permissions full`。
- **ask**：`--ask` 或 `/permissions ask` 打开逐次审批，工作区内的写入也要询问。审批卡片默认选中「允许这一次」，`Enter` 确认、`Y` 允许、`N` 或 `Esc` 拒绝、`A` 记住该目录。
- **plan**：`/permissions plan` 或 `/plan` 进入只读规划，文件读取可用，写入、Shell 和 MCP 操作停用。
- **simple**：`/permissions simple` 或 `/simple` 进入简单模式，只保留 `powershell_script`、`file_read`、`file_edit` 三个宿主工具；技能、MCP、子代理、插件、浏览器与桌面工具停用，新建文件、建目录和执行都交给 PowerShell。Web 工作台输入框的权限菜单同样可选。
- 单次代码执行超时默认 600 秒（`--ask` 下 60 秒），用 `--code-timeout` 调整。

### 会话、分支与子代理

任务运行中可用 `/steer` 在模型步骤边界补充指导，用 `/queue add 任务` 显式排队下一条任务。会话支持历史分支（`/branch`、`/fork`）、时间线（`/timeline`）和检查点（`/checkpoints`）；`/compact` 让模型总结较早的上下文并保留完整历史（终端、GUI 与 `/web-ui` 都可用）。

子代理由 `/agents` 配置模型、权限、预算与失败策略，`/subagents` 设置默认模型（默认跟随当前模型）。子代理不会直接向用户提问，而是把结果和阻塞原因返回主代理；停止会等待实际收尾。`/parent`、`/children` 在父子会话之间切换。

### 多开终端

每个 `allagent` 实例拥有独立的输入框、当前会话、模型选择和窗口尺寸，同一工作区共享 `.allagent/data/` 内的项目历史，保存走跨进程事务。后台任务只归当前实例所有，关闭窗口会结束该实例，遗留任务在下次启动时标记为中断（可用 `--resume ID` 恢复历史）；所有终端关闭后不驻留后台。

### 多会话后台运行

一个会话正在执行时，切走不会再中断它：`/new`、`/session <ID>`、`/parent`、`/children`、F3 或直接切到另一个会话都可以随时进行，正在跑的会话继续在后台执行，多个主会话可以同时跑。

- **标记**：终端侧栏（右下角「后台会话」）常驻列出其他正在运行的会话；最近会话列表、F3 选择器与 Web 侧栏会标出「运行中 / 需要授权 / 需要回答」；Web/图形界面右下角有浮层，可直接跳转或停止某个后台会话。
- **回到实时视图**：切回正在跑的会话会重放该轮已产生的输出并继续实时渲染，不会丢失或重复内容。
- **输入即指导**：给正在运行的会话发文本会作为指导注入当前任务（等同 `/steer`），不会另起一轮；要另起任务先停止它（Esc）。
- **需要你时挂起**：后台会话遇到授权或提问会挂起等待并在列表打标记，切回该会话立刻弹出对话框继续；不会代替你批准或拒绝。
- **完成提示**：后台会话结束时状态行/侧栏提示一次（Web 端为提示条），不自动跳转。
- **跨工作区**：`/workspace` 切换后，旧工作区的会话继续执行；侧栏会显示其他工作区的运行中数量，切回去用 `/session <ID>` 回到实时视图。
- **运行中可用**：`/new`、`/session [ID]`、`/sessions`、`/resume`、`/parent`、`/children`、`/todo`、`/status`、`/copy`、`/export`、`/tasks`、`/queue`、`/steer`、`/help`、`/thinking`、`/details`、`/web-ui status`；权限、沙箱与撤回类命令（`/undo`、`/redo`、`/rewind`）需要先停止全部会话。

## 安全与隔离

### 沙箱选择

- 首次执行前优先验证 Hyperlight（需要硬件虚拟化与 Windows Hypervisor Platform）；缺少 worker、虚拟化不可用或初始化失败时，自动安装并验证 Monty 1.1.0，运行时位于共享目录下的 `runtimes/monty-1.1.0/`，首次安装需要联网，不需要 Docker、管理员权限或重启电脑。
- Monty 是 Rust 实现的 Python 子集解释器，不是完整 CPython：不支持第三方包、`struct`、生成器等；默认不挂载宿主文件、不开放网络，只有 `call_tool` 桥接 C# 工具，继续经过宿主权限与审计。
- 沙箱只在初始化阶段切换；执行中失败不会换后端重放代码，避免重复修改文件或重复执行命令。
- `/sandbox` 或 `/sandbox status` 查看选择与实际后端，`/sandbox auto`（默认）优先 Hyperlight 并兜底 Monty，`/sandbox hyperlight` / `/sandbox monty` 强制指定后端。选择保存在当前工作区，切换会释放当前运行时，需先停止正在运行的任务。

### 宿主工具边界

- 模型只能调用 `execute_code`；文件、网络和进程操作全部由 `[AgentTool]` 标注的 C# 工具完成，权限与路径规则由宿主执行。
- `run_shell` 只接受可信列表中的程序，程序路径与参数分别传入，不拼接命令字符串；`powershell_script` 通过 worker 的 `--serve` 协议运行。
- 获准运行的程序仍具有当前用户的权限；沙箱不隔离已批准的 Shell 或 MCP 进程，也不会撤回它们产生的副作用。
- `/undo` 只能撤回带检查点的文件工具修改，不能撤回 Shell、MCP 或工作区外的副作用。细节见 [执行与权限](docs/execution.zh-CN.md)。
- 审批、审计与工具结果写入项目数据目录；请求会把任务所需内容发送给所选模型服务，提 Issue 前请移除密钥与个人数据。

## 数据与迁移

### 目录规则

| 内容 | 默认位置 |
|---|---|
| 项目数据（清单、会话、任务、审计 `audit.jsonl`） | `<工作区>/.allagent/data/` |
| 覆盖项目数据目录 | `--data-dir <路径>` |
| 工作区到数据目录的映射 | 程序目录下的 `allagent-projects.json` |
| 插件、OAuth 凭据、用户技能、共享运行时 | `%APPDATA%\AllAgent.NET`（显式指定 `--data-dir` 时改用该目录） |
| 日志 | `%APPDATA%\AllAgent.NET\allagent.log` |

用户目录下的外部技能只自动扫描 `~/.agents/skills/`；AllAgent.NET 自身技能目录和项目级技能按原规则加载。

### 清单与修订

`allagent.json` 是 v3 清单/设置文件，每个会话的不可变修订单独保存在 `conversations/`，保存当前会话不会重写其他历史。写入使用原子替换、fsync 与校验和回读，保留主清单与备份清单引用的会话版本；坏文件不会被静默替换成空数据库。

### 旧数据迁移

首次为某个工作区建立项目数据目录时，会自动导入旧 AppData 数据库中属于该工作区的会话，以及未记录工作区的旧会话和配置，旧目录保持可恢复。旧版单文件数据库在写入时转换为拆分格式，并保留 `allagent.legacy-v2.json.bak`。旧版本程序不支持新格式，请用新版本读取。

## 开发与构建

### 从源码运行

```powershell
.\agent.ps1 chat --workspace C:\Projects\MyProject
dotnet run --project src/AllAgent.NET.Terminal -- chat --workspace C:\Projects\MyProject
```

### 开发环境（Vite + 原生窗口）

```powershell
.\dev.ps1
```

`dev.ps1` 启动 Vite 开发服务器（`http://localhost:5173`），再用 `dotnet run ... gui --gui-url http://127.0.0.1:5173/` 打开窗口。也可以分两个终端手动启动，或用 `dev.bat`。

### Web 前端

```powershell
npm --prefix src/AllAgent.NET.Web install
npm --prefix src/AllAgent.NET.Web run dev

cd src/AllAgent.NET.Web
npm run build      # tsc --noEmit && vite build，产物在 dist/
npx tsc --noEmit   # 只做类型检查
npm test           # vitest run
```

### 构建与测试

```powershell
# 跳过 Web 构建，只编译终端宿主
dotnet build src/AllAgent.NET.Terminal -p:SkipWebBuild=true

# 默认先 npm run build，再把 dist/ 拷到宿主旁边的 web/
dotnet build src/AllAgent.NET.Terminal

dotnet test tests/AllAgent.NET.Tests

# 发布 Windows x64 自包含版本
dotnet publish src/AllAgent.NET.Terminal -c Release -r win-x64 --self-contained true -o artifacts/allagent-win-x64
```

发布后请保留整个输出目录：终端宿主与各 worker（`AllAgent.NET.Gui.exe`、`AllAgent.NET.Browser.exe`、`AllAgent.NET.ComputerUse.exe`、`AllAgent.NET.Sandbox.exe`、`AllAgent.NET.PowerShell.exe`）同目录共享一份运行时，另有 `Modules/`、`web/`、`everything/` 与 `builtin-plugins/`；缺少 `AllAgent.NET.PowerShell.exe` 或 `Modules/` 时需重新安装或修复工具包。`AllAgent.NET.Core` 可嵌入其他 .NET 应用，扩展工具用 `[AgentTool]` 和 `[Description]` 注册，生成器产出 `IAgentToolSet`。

### 解决方案结构

| 项目 | 职责 |
|---|---|
| `src/AllAgent.NET.Core` | UI 无关的 Agent 引擎、模型服务、数据存储、桥接与技能 |
| `src/AllAgent.NET.Terminal` | Spectre.Console 终端宿主（`chat`、`web-ui`、`-guimode`） |
| `src/AllAgent.NET.Gui` | WPF + WebView2 原生窗口外壳，不依赖 Core |
| `src/AllAgent.NET.Web` | React 19 + Iconiq UI + Vite 5 + TypeScript 聊天界面 |
| `src/AllAgent.NET.Sandbox` | Hyperlight 隔离执行 worker，随包与宿主同目录 |
| `src/AllAgent.NET.PowerShell` | 嵌入的 PowerShell SDK worker，随包与宿主同目录（模块在 `Modules/`） |
| `src/AllAgent.NET.Browser` | C# 浏览器运行时（WebView2 与外置浏览器） |
| `src/AllAgent.NET.ComputerUse` | 基于 FlaUI 的桌面操作 worker |
| `src/AllAgent.NET.ToolGenerator` | Roslyn 增量生成器：JSON Schema 与类型化分发 |
| `tests/AllAgent.NET.Tests` | xUnit：工具边界、Agent 生命周期与实际微虚拟机测试 |

## 常见问题与排错

| 现象 | 处理 |
|---|---|
| 不确定缺什么环境 | `allagent doctor`（聊天内 `/doctor`）看检查表；`doctor --install` 在确认后装依赖 |
| 内容搜索不可用 | 安装 ripgrep：`winget install --id BurntSushi.ripgrep.MSVC` |
| 提示宿主 Python 缺失或版本不符 | 需要 3.10–3.14；按提示用 winget 安装 Python 3.13，安装后重启软件 |
| 沙箱启动失败 | `/sandbox status` 查看实际后端与兜底原因；没有虚拟化时用 `/sandbox monty` |
| PowerShell 工具报 worker 缺失 | worker 随包分发，报缺失说明安装损坏，重新安装 AllAgent.NET |
| 模型连不上 | `/login status` 查看账号与连接状态，`/providers show 配置名` 查看脱敏配置 |
| 只想改配置，不想初始化环境 | 加 `--no-initialize` |
| 需要查看日志 | `%APPDATA%\AllAgent.NET\allagent.log`，其中包含 `[Gui]`、`[GuiHost]` 与桥接调用记录 |

## 更多文档

- [CodeAct 与 Hyperlight 接入](docs/codeact.zh-CN.md)
- [Python 执行过程与权限模式](docs/execution.zh-CN.md)
- [宿主文件与命令工具](docs/host-tools.zh-CN.md)
- [文件搜索与工具输出](docs/file-search.zh-CN.md)
- [执行预算与重试](docs/runtime-output-budgets.zh-CN.md)
- [内置 Browser Use](docs/browser-use.zh-CN.md) · [Windows Computer Use](docs/computer-use.zh-CN.md)
- [PowerShell 持久会话](docs/powershell-session.zh-CN.md) · [PowerShell 能力商店](docs/powershell-store.zh-CN.md)
- [账号登录与模型连接](docs/login.zh-CN.md)
- [子代理独立配置](docs/subagent-configuration.zh-CN.md) · [自定义代理角色示例](docs/agent-roles.example.json)
- [终端交互与任务控制](docs/terminal-followup.zh-CN.md) · [TODO 执行清单](docs/todo.zh-CN.md)
- [多会话后台运行与跨工作区](docs/multi-session-runs.zh-CN.md)
- [Web 工作台：文件浏览、Git 管理与集成终端](docs/webui-files-git-terminal.zh-CN.md)
- [.NET Tool 打包与安装](docs/nuget-tool.zh-CN.md)
- [插件开发指南](docs/plugins.zh-CN.md) · [插件开发入门](docs/plugin-development-beginner.zh-CN.md) · [插件 Web 前端扩展](docs/plugin-web-ui.zh-CN.md) · [执行积极度插件](plugins/AllAgent.NET.Diligence/README.md)

## 许可证

本项目采用 **GPL-3.0-only** 开源协议。

- 源代码可自由使用、修改和分发
- 衍生作品必须以相同协议开源
- 详见 [LICENSE](LICENSE)；第三方代码的声明与许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)

---

<div align="center">

欢迎通过 [Issues](https://github.com/luolangaga/AllAgent.NET/issues) 报告问题或提出建议。提交改动时请说明复现步骤、行为变化与验证方式；环境或沙箱问题请附上 `doctor` 输出，并移除密钥和个人数据。

**如果觉得有用，给个 Star 吧！**

<sub>Windows x64 · .NET 10 · GPL-3.0-only</sub>

</div>






