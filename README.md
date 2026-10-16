# LanAgent

Browser Use 默认开启，由 C# 实现。内置模式使用 WebView2 独立窗口，外置模式支持自动启动并连接 Chrome/Edge。支持网页 JS（含 Promise）、页面观察、鼠标输入、标签页和截图；无需安装 Python 浏览器库或下载 Chromium。操作说明通过内置 `/browser-use` skill 按需加载，识别到的其他浏览器操作技能会被屏蔽。`/browser` 打开管理面板，选择浏览器后自动启动、检测连接，`/browser on|off` 控制总开关。[详细连接教程与当前能力边界](docs/browser-use.zh-CN.md)。

Windows Computer Use 使用 FlaUI，默认关闭。用户用 `/computer on|off` 控制总开关，AI 自动选择后台、前台或独立 Windows 环境。后台支持控件点击与文本输入，软件测试优先后台；完整后台键鼠需独立环境。内置 `/computer-use` 技能指导操作，卡片显示目标、模式和实际结果。[使用与隔离环境配置](docs/computer-use.zh-CN.md)。

**面向 Windows 本机工作流的 AI Agent。**

LanAgent 支持文件与内容搜索、会话分支、上下文整理和失败重试。子代理提供调查、修改、审查、验证四种角色，支持单任务、并行和串行链派发；子代理向主代理返回结果和阻塞原因，停止会等待实际收尾。

在熟悉的 Windows 终端里，让 AI 检查项目、修改文件、调用本机开发工具，并验证结果。LanAgent 围绕 PowerShell、Windows 路径、原生剪贴板和 Hyperlight 微虚拟机设计；Agent 执行目前支持 **Windows x64**。

[快速开始](#快速开始) · [Windows 上的优势](#为什么适合在-windows-上工作) · [使用指南](#日常使用) · [开发与构建](#开发与构建) · [GPL-3.0](LICENSE)

## 为什么适合在 Windows 上工作

### 直接使用本机的开发环境

Agent 可以在指定工作区内调用已安装的 `git`、`dotnet`、`rg`、Node.js 包管理器等工具，适合检查 .NET 项目、修复构建、整理代码和执行本地验证。

`run_shell` 使用随包的 **PowerShell SDK 7.6.7** worker 调度程序，将程序路径和参数分别传入。不依赖系统安装的 PowerShell。SDK 在独立 .NET worker 中运行，保留超时及进程树取消。工具卡片展示实际参数、工作目录和结果。

### 截图可以直接进入终端对话

复制 Windows 截图后，在输入框按 **Ctrl+V** 或 **Shift+Insert** 即可暂存图片，回车提交给支持视觉的模型。支持原生 PNG 与位图/DIB 剪贴板，也可以引用图片路径：

```text
帮我分析这个报错 @截图.png
检查这张界面的排版 @"C:\Pictures\界面截图.png"
```

图片按提交时的内容保存到会话，之后删除原图仍可恢复记录。对排查窗口报错、终端输出和桌面界面问题，省去了另开网页上传图片的步骤。

### Windows 路径与目录授权由宿主处理

文件工具由 C# 宿主解析路径，默认围绕当前工作区工作。访问外部目录时会询问，可只允许本次，也可在当前会话记住目录；写入和执行程序仍按各自的审批规则处理。

宿主检查 Windows 设备路径、备用数据流和歧义路径，普通模式还限制链接路径。处理 `C:\Projects\...` 这类本机目录时，路径边界有明确规则。

### 隔离模型代码，同时保留本机工具能力

默认模式下，模型通过 Python 组织步骤，Python 优先在 **Hyperlight 微虚拟机**中运行，启动失败时自动切换到 **Monty 软件沙箱（无需虚拟化）**；文件、网络和命令操作通过受控工具交给 C# 宿主执行。每次代码执行恢复干净的虚拟机快照，worker 不继承模型服务凭据。

默认使用完全访问模式，也可用 `lanagent --full-access` 或 `/permissions full` 选择。Python 始终在选定的隔离沙箱中执行，不再运行宿主 AST 白名单审核，语法与能力错误由隔离运行时返回；文件、网络与程序执行通过 C# 宿主工具完成。完全访问仅取消宿主工具逐次审批，允许工作区外路径，不授予管理员权限。`--ask` 或 `/permissions ask` 开启审批；`/permissions plan` 使用只读策略。两种权限模式均要求 Python、随包 PowerShell SDK worker 和至少一个可用沙箱；缺少沙箱时不得降级为宿主 Python。默认单次代码超时为 600 秒，可用 `--code-timeout` 调整。

第一次进入终端聊天会初始化环境：内嵌 PowerShell SDK worker 会实际启动自检，无需安装系统 PowerShell；worker 缺失或损坏时提示修复 LanAgent。ripgrep 通过 `winget install --id BurntSushi.ripgrep.MSVC` 补齐。宿主 Python 检查可执行性与 3.10–3.14 版本范围；缺少或版本不符时提示通过 winget 安装 Python 3.13，安装完成后提示重启。不自动创建共享 pip 库环境或安装 Python 第三方包。扩展能力统一通过 `/powershell` 模块商店管理；Hyperlight 启动时保留标准库与 UTF-16LE/Base64 校验。离线或仅管理配置时可用 `--no-initialize` 跳过。

Python 仅使用沙箱已有的语言与标准库能力；提示词禁止安装或导入第三方 Python 包，也禁止通过宿主 Python 绕过。pip 清单不再注入上下文，`/install-pip` 已移除。已存在的 Python 环境与库文件保留；AI 能力清单改为 PowerShell 模块摘要，并通过 `powershell_capabilities` 查询导出命令。

`/doctor` 与 `lanagent doctor` 检查宿主 Python 的可执行性和版本、PowerShell 能力清单、ripgrep 和选定沙箱的实际执行结果；`/doctor install` 修复依赖，通过 winget 安装 Python 后提示重启软件。检查本身不会安装软件。

这让模型的计算代码与 Windows 宿主操作分开管理。**获准运行的本机程序仍具有当前用户的权限**；微虚拟机不隔离已经批准的 Shell 或 MCP 进程，也不会自动撤回它们的副作用。

### 环境问题在开始执行前暴露

`doctor` 检查 Python、PowerShell 和沙箱运行环境，识别 Python 的 Microsoft Store 别名，避免把别名误当作可用解释器。`doctor --install` 可以在确认后通过 `winget` 补装支持的依赖。

缺少必需环境时，Agent 会停止执行并说明问题，便于在 Windows 上定位配置错误。

## 快速开始

### 1. 准备环境

| 环境 | 要求 |
|---|---|
| 系统 | Windows x64；Hyperlight 需要虚拟化，Monty 无需虚拟化 |
| .NET | .NET 10 SDK；项目目标框架为 .NET 10 |
| Python | 3.10–3.14，需可执行的宿主 Python |
| PowerShell | 随包嵌入 SDK，无需单独安装 |
| 模型 | 可用的模型服务配置，或受支持的账号登录 |
| Node.js / npm | 仅 React 前端源码构建需要 Node.js / npm；C# 代理执行不依赖 Node.js |

### 2. 通过 NuGet 安装

从 NuGet.org 安装已发布版本：

```powershell
dotnet tool install --global LanAgent.Terminal
lanagent --help
lanagent doctor
```

从旧版本升级：

```powershell
dotnet tool update --global LanAgent.Terminal
```

新安装者执行 `dotnet tool install`；从旧版本升级时执行 `dotnet tool update`。

首次运行前检查 Windows 环境：

```powershell
lanagent doctor
# 可选：检查并在确认后通过 winget 安装依赖
lanagent doctor --install
```

NuGet Tool 会安装 `lanagent` 命令。完成模型配置后即可使用：

```powershell
lanagent chat --workspace C:\Projects\MyProject
```

进入聊天后按 **F2** 配置模型或选择提供商，也可使用 `/login` 登录。安装后升级或卸载：

```powershell
dotnet tool update --global LanAgent.Terminal
dotnet tool uninstall --global LanAgent.Terminal
```

如要从源码启动开发版本，可继续使用 `./agent.ps1`；模型和扩展管理无需启动沙箱，首次执行任务时才检查执行环境。

### 3. 选择模型，描述任务

进入聊天后按 **F2** 配置或选择模型，也可用 `/login` 选择 Codex 或认证插件。支持 OpenAI Chat、Responses、Anthropic、Gemini 和兼容 OpenAI 协议的服务（包括 Ollama）。实际功能取决于服务端及模型。

终端会保存上次选择的提供商与模型，下次进入自动复用；按 F2 或 `/model` 随时切换。

```text
检查这个 .NET 项目为什么无法构建，修复后运行相关测试。
```

也可临时通过环境变量配置，不把 API Key 写进项目：

```powershell
$env:LANAGENT_PROVIDER = 'openai'
$env:LANAGENT_BASE_URL = 'https://api.openai.com/v1'
$env:LANAGENT_MODEL = '<模型 ID>'
$env:LANAGENT_API_KEY = '<你的 API Key>'
.\agent.ps1 chat --workspace C:\Projects\MyProject
```

如需安装为全局 `lanagent` 命令，见 [.NET Tool 打包与安装](docs/nuget-tool.zh-CN.md)。

## 日常使用

| 操作 | 快捷键 / 命令 |
|---|---|
| 切换模型 | F2、`/model` |
| 设置提问偏好 | `/questions` 选择四档；`/questions status` 查看当前档位 |
| 恢复最近会话 | F3、`/resume` |
| 搜索所有命令 | Ctrl+P；F1 打开常用操作 |
| 引用项目文件 | `@路径`，含空格用 `@"路径"` |
| 输入多行 | Ctrl+J / Shift+Enter / Alt+Enter |
| 查看执行代码与工具结果 | Ctrl+E；点击执行块 |
| 停止当前任务 | 双击 Esc 确认，或 Ctrl+C |
| 查看文件变更与撤回 | `/diff`、`/undo`、`/redo` |
| 调整显示 | `/settings`；Ctrl+B 显示或隐藏侧栏 |

任务运行时继续输入并回车，默认作为指导补充当前任务，在模型步骤边界送达；`/steer` 等效。用 `/queue add 任务说明` 显式排队下一条任务。会话支持历史分支、时间线、子代理及后台任务；Skills、MCP 与 DLL 插件可扩展工具能力。联网搜索通过真实浏览器打开搜索页并提取结果，默认使用 Bing，无需搜索 API Key 或本地搜索服务；支持显式选择 Google、DuckDuckGo。需要启用 Browser Use（`/browser on`），验证码需在浏览器中完成。网页读取仍使用 `web_fetch`。

提问偏好支持 `/questions default`（0，默认，不追加任何提示词）、`minimal`（1，少问，仅必要时提问）、`balanced`（2，适中，澄清关键歧义）、`proactive`（3，多问，主动确认目标与偏好）。设置保存在当前工作区的数据目录，恢复会话、新建会话和重启后继续生效；切回 `default` 即移除该偏好提示词。该设置调整主 Agent 的澄清提问倾向，不改变工具权限审批或子代理不能直接向用户提问的规则。

### 权限与数据

默认使用完全访问模式：宿主工具通常不逐次审批，并允许工作区外路径；沙箱隔离仍然生效，且不授予管理员权限。通过 `--ask` 启动或 `/permissions ask` 切换逐次授权：可信 Shell 别名可直接执行，文件写入、其他程序和外部工具按规则询问。审批默认选中 **“允许这一次”**，需回车确认；N / Esc 拒绝。`/permissions plan` 切换只读规划，`/permissions full` 开启完全访问。

`/undo` 可撤回具有检查点的工作区文件工具修改，不能撤回 Shell、MCP 或工作区外的副作用。具体边界见 [执行与权限说明](docs/execution.zh-CN.md)。

终端的会话与配置默认保存在工作目录的 `.lanagent/data/`（Windows 下隐藏）。`lanagent.json` 是小型清单，每个会话单独保存于 `conversations/`，保存当前会话不会重写其他历史。程序可执行文件所在目录的 `lanagent-projects.json` 记录各工作目录与数据路径；已登记的路径不会重复写入。`--data-dir` 可覆盖项目数据目录。插件、OAuth 登录凭据、用户技能和 Python 公共运行库继续复用 `%APPDATA%\LanAgent`，避免每个项目重复安装；显式指定 `--data-dir` 时这些资源也使用指定目录。

用户目录下的外部技能仅自动扫描 `~/.agents/skills/`；不会扫描 `~/.codex/skills/`、`~/.claude/skills/` 或 `~/.config/opencode/skills/`。LanAgent 自身技能目录及项目级技能仍按原规则加载。

首次使用项目数据目录时，自动导入旧 AppData 数据库中属于当前工作区的会话，以及未记录工作区的旧会话、配置；旧目录保持可恢复。旧版单文件数据库写入时自动转换为拆分格式，并保留 `lanagent.legacy-v2.json.bak`。清单与会话通过校验和、原子替换和落盘刷新保存，保留主清单与备份引用的会话版本；坏文件不会被静默替换成空数据库。旧版本程序不支持新格式，请使用新版本读取。请求仍会把任务所需内容发送给所选模型服务。

提示词保留稳定前缀并固定工具顺序，Anthropic 使用系统与历史消息缓存断点；侧栏展示服务端返回的缓存 Token 命中率。实际命中取决于模型与服务策略。

## 开发与构建

终端开发不需要 Node.js：

```powershell
dotnet build src/LanAgent.Terminal
dotnet test tests/LanAgent.Tests

# 发布 Windows x64 版本（自带 .NET 运行时）
dotnet publish src/LanAgent.Terminal -c Release -r win-x64 --self-contained true -o artifacts/lanagent-win-x64
```

发布后保留整个输出目录及 `sandbox/` 子目录。宿主 Python 仍需满足要求，保留随包 `powershell/` 目录；无虚拟化时使用 Monty。

桌面端使用 Avalonia + React / Iconiq UI，在两个终端分别启动：

```powershell
# 终端 1：安装根目录和 Web 项目依赖，启动 Vite
npm install
npm --prefix src/LanAgent.Web install
npm --prefix src/LanAgent.Web run dev

# 终端 2：启动桌面宿主，使用 Vite 开发服务
dotnet run --project src/LanAgent.Desktop -p:SkipWebBuild=true
```

| 项目 | 职责 |
|---|---|
| `LanAgent.Core` | UI 无关的 Agent 引擎、模型服务、数据与宿主工具 |
| `LanAgent.Terminal` | Spectre.Console 终端界面 |
| `LanAgent.Sandbox` | Hyperlight 隔离执行 worker |
| `LanAgent.ToolGenerator` | C# 工具 Schema 与类型化分发生成器 |
| `LanAgent.Desktop` / `LanAgent.Web` | Avalonia 桌面宿主与 React / Iconiq UI 聊天界面 |
| `LanAgent.Tests` | 工具边界、生命周期、终端行为及实际微虚拟机测试 |

`LanAgent.Core` 可嵌入其他 .NET 应用；扩展工具通过 `[AgentTool]` 注册，模型统一经 `execute_code` 调用。Hyperlight 相关依赖目前包含预览版本，升级需重新验证实际虚拟机测试。

## 更多文档

- [CodeAct 与工具执行](docs/codeact.zh-CN.md)
- [执行详情、权限与目录授权](docs/execution.zh-CN.md)
- [账号登录](docs/login.zh-CN.md)
- [.NET Tool 打包与安装](docs/nuget-tool.zh-CN.md)
- [插件开发](docs/plugins.zh-CN.md)
- [插件开发新手教程（Core 1.1.0）](docs/plugin-development-beginner.zh-CN.md)
- [执行积极度插件：省力、标准、勤快](plugins/LanAgent.Diligence/README.md)
- [终端交互与任务控制](docs/terminal-followup.zh-CN.md)
- [自定义代理角色示例](docs/agent-roles.example.json)

欢迎通过 [Issues](https://github.com/luolangaga/LanAgent/issues) 报告问题或提出建议。提交改动时请说明复现步骤、行为变化与验证方式；环境或沙箱问题请附上 `doctor` 输出，并移除密钥和个人数据。

## 开源协议

LanAgent 使用 [GPL-3.0-only](LICENSE) 协议。第三方代码的声明与许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。


多开终端时，每个 `lanagent` 实例拥有独立输入框、当前会话和窗口尺寸，同一工作区共享 `.lanagent/data` 内的项目历史。保存采用跨进程事务，打开会话列表时刷新历史；关闭终端会自动退出对应实例；所有终端关闭后不驻留后台。后台任务随当前实例运行，关闭窗口会停止该实例，后续启动将遗留任务记为中断。详见 [终端说明](docs/nuget-tool.zh-CN.md)。

### 双沙箱自动兜底

首次执行前优先验证 Hyperlight；缺少 worker、虚拟化不可用或初始化失败时，自动安装并验证 Monty 1.1.0。备用运行时使用共享配置目录下独立的 `runtimes/monty-1.1.0/` Python 环境，首次安装需要联网，不需要 Docker、管理员权限或重启电脑。终端和 `/status` 显示实际后端，`/doctor` 仅检查，`/doctor install` 可补齐依赖。

Monty 是 Rust 实现的 Python 子集解释器，并非完整 CPython：不支持第三方包、struct、生成器、继承等；具体限制会加入模型上下文。默认不挂载宿主文件、不开放网络，只有 `call_tool` 桥接 C# 工具，继续经过宿主权限与审计。每次执行使用干净会话，内存上限 128 MiB，并限制时间和输出。沙箱只在初始化阶段切换；执行中失败不会自动换后端重放代码，避免重复文件修改或命令。两个后端均不可用时阻止 Agent 执行。

`/sandbox` 或 `/sandbox status` 查看沙箱选择和实际运行后端。`/sandbox auto`（默认）优先 Hyperlight、初始化失败自动兜底 Monty；`/sandbox hyperlight` 与 `/sandbox monty` 强制使用指定后端，失败时报告错误。切换会释放当前运行时，下次任务前验证并启用；选择保存在当前工作区，重启后沿用。运行任务期间可查看状态，切换前需先完成或停止当前任务及后台任务。

### PowerShell 能力商店

`lanagent powershell search 关键词` 搜索 PSGallery；`lanagent powershell install 模块 [版本]` 安装模块及依赖。聊天内使用 `/powershell`。`list` 查看已验证模块，`commands 模块` 查看导出命令。AI 自动获得模块摘要，并通过 `powershell_capabilities` 查询详细命令。[使用说明](docs/powershell-store.zh-CN.md)。
