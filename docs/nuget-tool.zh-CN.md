# AllAgent.NET .NET Tool

Computer Use：`allagent computer status` 查看状态与独立环境配置（始终可用，无总开关）。AI 使用 `computer_mode` 自动选择三种模式，内置 `/computer-use` 技能指导操作；软件测试优先后台控件点击与文本输入。Worker 随工具包分发，需要 Windows .NET 10 Desktop Runtime。完整后台键鼠需独立 Windows 会话/虚拟机中的 Worker 和安全隧道，未配置时会明确报错。

AllAgent.NET 的终端 AI Agent，安装后的命令为 `allagent`。

模型通过 Hyperlight CodeAct 调用文件、搜索和 Shell 工具。子代理包括调查、修改、审查和验证角色，支持单任务、并行、串行链派发；子代理不向用户提问，停止会等待实际清理。

## 环境要求

目前 Agent 执行支持 Windows x64 / ARM64，需要 .NET 10 或更高版本运行时；优先使用 Hyperlight（需要硬件虚拟化和 Windows Hypervisor Platform），启动失败自动使用 Monty 软件沙箱（无需虚拟化，首次安装需要联网）。ARM64 上 Hyperlight 不可用时同样回退 Monty。工具已启用主版本向前滚动，因此安装了较新 .NET 运行时的电脑也可启动它。安装工具仍需 .NET SDK 提供 `dotnet tool` 命令。工具包内嵌 PowerShell SDK 7.6.7，通过独立 worker 调用，无需安装系统 PowerShell。执行前自检 worker，缺失时需修复工具包。工具包自带 sandbox worker、Hyperlight 原生组件和 Python guest；宿主 Python 用于沙箱运行支持，检查可执行性和 3.10–3.14 版本范围；缺少或版本不符时提示通过 winget 安装 Python 3.13，成功后要求重启软件。React 前端源码构建需要 Node.js 与 npm；终端代理执行不依赖 Node.js。

## 安装与使用

安装已发布到 NuGet.org 的版本：

```powershell
# Windows x64
dotnet tool install --global AllAgent.NET.Terminal

# Windows on ARM（命令名同样是 allagent）
dotnet tool install --global AllAgent.NET.Terminal.arm64

allagent --help
allagent doctor
allagent doctor --install
allagent chat --workspace C:\Projects\MyProject
```

原生窗口工作台（WPF + WebView2 外壳随包分发在宿主同目录，需要已安装 Microsoft Edge WebView2 Runtime）：

```powershell
allagent -guimode                  # 静默启动：命令行立即返回，关闭窗口即结束会话
allagent gui                       # 前台运行同一个窗口，Ctrl+C 结束
allagent gui --gui-devtools        # 打开窗口内开发者工具
allagent -guimode --workspace C:\Projects\MyProject
```

窗口内是与 `/web-ui` 完全相同的 Web 工作台（同一工作区、模型配置、权限与后台任务，支持工作区切换、插件与技能管理）；标题栏提供刷新、**在浏览器中打开**与窗口控制。同一数据目录只允许一个窗口，重复启动会提示已在运行。

进入聊天后按 F2 配置模型或使用 `/login`。更新和卸载：

每次运行 `allagent` 都启动独立聊天界面：输入框、当前会话、模型选择、权限和窗口尺寸属于各自的进程。同一工作区默认共用隐藏目录 `.allagent/data`，历史在打开会话列表、恢复会话以及保存时刷新。共享 manifest 的读取、修改、原子提交和历史清理由跨进程锁保护；独立会话同时写入不会互相覆盖。另一终端更新或删除了同一会话时，旧对象保存会报冲突，需要重新打开，避免覆盖历史。公共 Python 环境、插件、用户技能和登录凭据继续复用 AppData。

后台任务归创建它的终端进程管理。多开不会将另一个活跃进程的任务标为中断，也不会覆盖它的任务记录；`tasks list/show` 可查看项目内的持久记录。退出无需命令，直接关闭终端；全部终端关闭后程序自动退出，不驻留后台。关闭窗口会结束当前实例，未完成任务在后续启动时记为中断，不会自动重跑；可通过 `--resume ID` 恢复历史。

迁移前启动的旧版共享后台需先结束旧进程再重启。`allagent attach` 仅用于显式连接仍在运行的旧版共享后台（沿用其工作区、权限和共享输入行为，`Ctrl+]` 断开连接），不会自动创建后台。新启动的独立实例不使用该连接模式。

供应商和模型一起自动恢复。F2 或 `/model` 直接显示“供应商 · 模型”的列表，上次选择排在首位。

```powershell
dotnet tool update --global AllAgent.NET.Terminal
dotnet tool uninstall --global AllAgent.NET.Terminal

# Windows on ARM
dotnet tool update --global AllAgent.NET.Terminal.arm64
dotnet tool uninstall --global AllAgent.NET.Terminal.arm64
```

也可以使用内置更新器（自动识别当前安装的是哪个包 ID）：

```powershell
allagent update --check
allagent update
```

更新器在 NuGet 工具形态下通过 `dotnet tool update` 更新，并重启 `allagent.cmd` shim 指向新版本，而不是旧 `.store` 路径的 DLL。详见 [自动更新](auto-update.zh-CN.md)。

## 从源码打包并通过本地 NuGet 源安装

在仓库根目录运行（打包需 .NET SDK）：

```powershell
# x64 -> AllAgent.NET.Terminal.1.0.1.nupkg
tools\build-nuget.ps1 -Version 1.0.1 -Runtime win-x64

# ARM64 -> AllAgent.NET.Terminal.arm64.1.0.1.nupkg
tools\build-nuget.ps1 -Version 1.0.1 -Runtime win-arm64

dotnet tool install --global AllAgent.NET.Terminal --add-source ./artifacts/packages --version 1.0.1
# ARM64 机器：
dotnet tool install --global AllAgent.NET.Terminal.arm64 --add-source ./artifacts/packages --version 1.0.1
```

更新到刚打包的本地版本：

```powershell
dotnet tool update --global AllAgent.NET.Terminal --add-source ./artifacts/packages --version 1.0.1
```

`build-nuget.ps1` 会按 RID 还原、显式构建内嵌 PowerShell worker、打包并检查包内是否包含 `allagent.dll`、五个 worker 和 `web/index.html`。两个包 ID 共用 `allagent` 命令；NuGet 需要按 RID 解析 ARM64 的 worker apphost，因此不能把两种架构塞进同一个 tool 包。

版本默认在 `src/AllAgent.NET.Terminal/AllAgent.NET.Terminal.csproj` 中维护，也可以通过 `-Version` 覆盖。各 worker 的文件作为工具运行文件与宿主同目录，不依赖另外发布 AllAgent.NET.Core 或 AllAgent.NET.Sandbox 包。

## 发布

确认包 ID 在 NuGet.org 可用、版本及作者信息正确，并用有权发布该包的 API Key 执行：

```powershell
dotnet nuget push ./artifacts/packages/AllAgent.NET.Terminal.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key <API_KEY>
dotnet nuget push ./artifacts/packages/AllAgent.NET.Terminal.arm64.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key <API_KEY>
```

GitHub Actions 的 `.github/workflows/release.yml` 会在 tag 发布时自动推送两个包（存在 `NUGET_API_KEY` 时）。API Key 请通过仓库 Secret 提供，不要提交到仓库。详见 [CI 与发布流水线](release-ci.zh-CN.md)。

聊天中使用 `/workspace D:\Projects\Demo` 切换工作目录，空格路径可写为 `/workspace "D:\My Projects\Demo"`；也支持相对当前工作目录的路径，如 `/workspace ..`。单独输入 `/workspace` 查看当前目录。切换后进入目标项目的新会话，保留当前模型和权限模式，重新加载技能、文件引用、MCP 和工具运行环境。旧会话历史保留，可以切回原目录后用 `/session` 恢复。默认数据目录随项目切换；显式 `--data-dir` 保持指定的目录。存在运行中的后台任务时，需要先完成或取消任务再切换；路径不存在或目标数据无法读取时，继续留在当前项目。
