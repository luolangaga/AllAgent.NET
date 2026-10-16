# LanAgent .NET Tool

Computer Use：`lanagent computer on|off|status`，聊天内 `/computer on|off|status` 控制总开关。AI 使用 `computer_mode` 自动选择三种模式，内置 `/computer-use` 技能指导操作；默认关闭，软件测试优先后台控件点击与文本输入。Worker 随工具包分发，需要 Windows .NET 10 Desktop Runtime。完整后台键鼠需独立 Windows 会话/虚拟机中的 Worker 和安全隧道，未配置时会明确报错。

LanAgent 的终端 AI Agent，安装后的命令为 `lanagent`。

模型通过 Hyperlight CodeAct 调用文件、搜索和 Shell 工具。子代理包括调查、修改、审查和验证角色，支持单任务、并行、串行链派发；子代理不向用户提问，停止会等待实际清理。

## 环境要求

目前 Agent 执行支持 Windows x64，需要 .NET 10 或更高版本运行时；优先使用 Hyperlight（需要硬件虚拟化和 Windows Hypervisor Platform），启动失败自动使用 Monty 软件沙箱（无需虚拟化，首次安装需要联网）。工具已启用主版本向前滚动，因此安装了较新 .NET 运行时的电脑也可启动它。安装工具仍需 .NET SDK 提供 `dotnet tool` 命令。工具包内嵌 PowerShell SDK 7.6.7，通过独立 worker 调用，无需安装系统 PowerShell。执行前自检 worker，缺失时需修复工具包。工具包自带 sandbox worker、Hyperlight 原生组件和 Python guest；宿主 Python 用于沙箱运行支持，检查可执行性和 3.10–3.14 版本范围；缺少或版本不符时提示通过 winget 安装 Python 3.13，成功后要求重启软件。React 前端源码构建需要 Node.js 与 npm；终端代理执行不依赖 Node.js。

## 安装与使用

安装已发布到 NuGet.org 的版本：

```powershell
dotnet tool install --global LanAgent.Terminal
lanagent --help
lanagent doctor
lanagent doctor --install
lanagent chat --workspace C:\Projects\MyProject
```

进入聊天后按 F2 配置模型或使用 `/login`。更新和卸载：

每次运行 `lanagent` 都启动独立聊天界面：输入框、当前会话、模型选择、权限和窗口尺寸属于各自的进程。同一工作区默认共用隐藏目录 `.lanagent/data`，历史在打开会话列表、恢复会话以及保存时刷新。共享 manifest 的读取、修改、原子提交和历史清理由跨进程锁保护；独立会话同时写入不会互相覆盖。另一终端更新或删除了同一会话时，旧对象保存会报冲突，需要重新打开，避免覆盖历史。公共 Python 环境、插件、用户技能和登录凭据继续复用 AppData。

后台任务归创建它的终端进程管理。多开不会将另一个活跃进程的任务标为中断，也不会覆盖它的任务记录；`tasks list/show` 可查看项目内的持久记录。退出无需命令，直接关闭终端；全部终端关闭后程序自动退出，不驻留后台。关闭窗口会结束当前实例，未完成任务在后续启动时记为中断，不会自动重跑；可通过 `--resume ID` 恢复历史。

迁移前启动的旧版共享后台需先结束旧进程再重启。`lanagent attach` 仅用于显式连接仍在运行的旧版共享后台（沿用其工作区、权限和共享输入行为，`Ctrl+]` 断开连接），不会自动创建后台。新启动的独立实例不使用该连接模式。

供应商和模型一起自动恢复。F2 或 `/model` 直接显示“供应商 · 模型”的列表，上次选择排在首位。

```powershell
dotnet tool update --global LanAgent.Terminal
dotnet tool uninstall --global LanAgent.Terminal
```

## 从源码打包并通过本地 NuGet 源安装

在仓库根目录运行（打包需 .NET SDK）：

```powershell
dotnet pack src/LanAgent.Terminal -c Release -p:Version=1.0.1
dotnet tool install --global LanAgent.Terminal --add-source ./artifacts/packages --version 1.0.1
```

更新到刚打包的本地版本：

```powershell
dotnet tool update --global LanAgent.Terminal --add-source ./artifacts/packages --version 1.0.1
```

版本默认在 `src/LanAgent.Terminal/LanAgent.Terminal.csproj` 中维护，也可以通过 `-p:Version=1.0.2` 覆盖。sandbox 的文件作为工具运行文件放入包内的 `sandbox/` 子目录，不依赖另外发布 LanAgent.Core 或 LanAgent.Sandbox 包。

## 发布

确认包 ID 在 NuGet.org 可用、版本及作者信息正确，并用有权发布该包的 API Key 执行：

```powershell
dotnet nuget push ./artifacts/packages/LanAgent.Terminal.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key <API_KEY>
```

API Key 请通过本地安全配置提供，不要提交到仓库。这里只配置和生成工具包；公共发布需要单独执行上述命令。

聊天中使用 `/workspace D:\Projects\Demo` 切换工作目录，空格路径可写为 `/workspace "D:\My Projects\Demo"`；也支持相对当前工作目录的路径，如 `/workspace ..`。单独输入 `/workspace` 查看当前目录。切换后进入目标项目的新会话，保留当前模型和权限模式，重新加载技能、文件引用、MCP 和工具运行环境。旧会话历史保留，可以切回原目录后用 `/session` 恢复。默认数据目录随项目切换；显式 `--data-dir` 保持指定的目录。存在运行中的后台任务时，需要先完成或取消任务再切换；路径不存在或目标数据无法读取时，继续留在当前项目。
