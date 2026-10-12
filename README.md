# LanAgent

**面向 Windows 本机工作流的 AI Agent。**

在熟悉的 Windows 终端里，让 AI 检查项目、修改文件、调用本机开发工具，并验证结果。LanAgent 围绕 PowerShell、Windows 路径、原生剪贴板和 Hyperlight 微虚拟机设计；Agent 执行目前支持 **Windows x64**。

[快速开始](#快速开始) · [Windows 上的优势](#为什么适合在-windows-上工作) · [使用指南](#日常使用) · [开发与构建](#开发与构建) · [GPL-3.0](LICENSE)

## 为什么适合在 Windows 上工作

### 直接使用本机的开发环境

Agent 可以在指定工作区内调用已安装的 `git`、`dotnet`、`rg`、Node.js 包管理器等工具，适合检查 .NET 项目、修复构建、整理代码和执行本地验证。

命令执行采用 **PowerShell 7.6**，程序路径和参数分别传入；环境检查不会退回 Windows PowerShell 5.1 或 `cmd`。你可以看到实际要运行的程序、参数和工作目录，减少 Windows 路径、空格和命令转义带来的歧义。

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

模型通过 Python 组织步骤，Python 在 **Hyperlight 微虚拟机**中运行；文件、网络和命令操作通过受控工具交给 C# 宿主执行。每次代码执行恢复干净的虚拟机快照，worker 不继承模型服务凭据。

这让模型的计算代码与 Windows 宿主操作分开管理。**获准运行的本机程序仍具有当前用户的权限**；微虚拟机不隔离已经批准的 Shell 或 MCP 进程，也不会自动撤回它们的副作用。

### 环境问题在开始执行前暴露

`doctor` 检查 Python、PowerShell 和沙箱运行环境，识别 Python 的 Microsoft Store 别名，避免把别名误当作可用解释器。`doctor --install` 可以在确认后通过 `winget` 补装支持的依赖。

缺少必需环境时，Agent 会停止执行并说明问题，便于在 Windows 上定位配置错误。

## 快速开始

### 1. 准备环境

| 环境 | 要求 |
|---|---|
| 系统 | Windows x64，启用硬件虚拟化及 Windows Hypervisor Platform |
| .NET | 支持 `.slnx` 的 .NET SDK（9 或更高）；项目目标框架为 .NET 8 |
| Python | 3.10–3.14，需可执行的宿主 Python |
| PowerShell | 7.6.x |
| 模型 | 可用的模型服务配置，或受支持的账号登录 |
| Node.js / npm | 仅桌面 Web 前端开发或构建需要；终端内置搜索无需安装 |

### 2. 从源码启动

在 PowerShell 中执行：

```powershell
git clone https://github.com/luolangaga/LanAgent.git
cd LanAgent

# 检查环境；--install 会在确认后尝试安装缺失依赖
.\agent.ps1 doctor
.\agent.ps1 doctor --install

# 在你要处理的项目中开始聊天
.\agent.ps1 chat --workspace C:\Projects\MyProject
```

模型和扩展管理不需要启动沙箱；首次任务才检查执行环境。

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
| 恢复最近会话 | F3、`/resume` |
| 搜索所有命令 | Ctrl+P；F1 打开常用操作 |
| 引用项目文件 | `@路径`，含空格用 `@"路径"` |
| 输入多行 | Ctrl+J / Shift+Enter / Alt+Enter |
| 查看执行代码与工具结果 | Ctrl+E；点击执行块 |
| 停止当前任务 | 双击 Esc 确认，或 Ctrl+C |
| 查看文件变更与撤回 | `/diff`、`/undo`、`/redo` |
| 调整显示 | `/settings`；Ctrl+B 显示或隐藏侧栏 |

任务运行时可以继续输入并回车排队，或用 `/steer` 补充当前任务。会话支持历史分支、时间线、子代理及后台任务；Skills、MCP 与 DLL 插件可扩展工具能力。内置联网搜索和网页读取无需搜索 API Key。

### 权限与数据

默认使用逐次授权模式：可信 Shell 别名可直接执行，文件写入、其他程序和外部工具按规则询问。审批默认选中 **“允许这一次”**，需回车确认；N / Esc 拒绝。`/permissions plan` 切换只读规划，`/permissions full` 开启完全访问。

`/undo` 可撤回具有检查点的工作区文件工具修改，不能撤回 Shell、MCP 或工作区外的副作用。具体边界见 [执行与权限说明](docs/execution.zh-CN.md)。

本地配置与会话默认保存在 `%APPDATA%\LanAgent\lanagent.json`，带备份；`--data-dir` 可指定独立数据目录。请求仍会把任务所需内容发送给所选模型服务。本地持久化不代表离线推理。

提示词保留稳定前缀并固定工具顺序，Anthropic 使用系统与历史消息缓存断点；侧栏展示服务端返回的缓存 Token 命中率。实际命中取决于模型与服务策略。

## 开发与构建

终端开发不需要 Node.js：

```powershell
dotnet build src/LanAgent.Terminal
dotnet test tests/LanAgent.Tests

# 发布 Windows x64 版本（自带 .NET 运行时）
dotnet publish src/LanAgent.Terminal -c Release -r win-x64 --self-contained true -o artifacts/lanagent-win-x64
```

发布后保留整个输出目录及 `sandbox/` 子目录。宿主 Python、PowerShell 和 Windows 虚拟化环境仍需满足要求。

桌面端使用 Avalonia + Vue，在两个终端分别启动：

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
| `LanAgent.Desktop` / `LanAgent.Web` | Avalonia 桌面宿主与 Vue 聊天界面 |
| `LanAgent.Tests` | 工具边界、生命周期、终端行为及实际微虚拟机测试 |

`LanAgent.Core` 可嵌入其他 .NET 应用；扩展工具通过 `[AgentTool]` 注册，模型统一经 `execute_code` 调用。Hyperlight 相关依赖目前包含预览版本，升级需重新验证实际虚拟机测试。

## 更多文档

- [CodeAct 与工具执行](docs/codeact.zh-CN.md)
- [执行详情、权限与目录授权](docs/execution.zh-CN.md)
- [账号登录](docs/login.zh-CN.md)
- [.NET Tool 打包与安装](docs/nuget-tool.zh-CN.md)
- [插件开发](docs/plugins.zh-CN.md)
- [终端交互与任务控制](docs/terminal-followup.zh-CN.md)
- [自定义代理角色示例](docs/agent-roles.example.json)

欢迎通过 [Issues](https://github.com/luolangaga/LanAgent/issues) 报告问题或提出建议。提交改动时请说明复现步骤、行为变化与验证方式；环境或沙箱问题请附上 `doctor` 输出，并移除密钥和个人数据。

## 开源协议

LanAgent 使用 [GPL-3.0-only](LICENSE) 协议。第三方代码的声明与许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
