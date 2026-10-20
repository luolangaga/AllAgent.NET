# Windows 安装包（exe）

一键安装包由 GitHub Actions 构建：**自包含 win-x64 / win-arm64** 的终端宿主 + Inno Setup 6 安装程序，安装完即可使用，不需要预装 .NET 运行时。

- 产物：`AllAgent.NET-<版本>-win-x64-setup.exe`、`AllAgent.NET-<版本>-win-arm64-setup.exe`
- 校验文件：分架构的 `SHA256SUMS.win-x64.txt` / `SHA256SUMS.win-arm64.txt`，发布时合并为 `SHA256SUMS.txt`
- 构建脚本：`tools/build-installer.ps1`；安装脚本：`installer/AllAgent.NET.iss`

## 一、安装过程

安装向导有简体中文与英文两种语言（默认简体中文），并遵循 Inno Setup 的标准流程：许可协议（GPL-3.0）→ 安装范围 → 目标目录 → **附加任务** → 安装 → 完成。

### 安装范围

默认“仅为我安装”到 `%LOCALAPPDATA%\Programs\AllAgent.NET`：不需要管理员权限，安装目录本身可写。也可以在开始时选择“为所有用户安装”，此时装到 `C:\Program Files\AllAgent.NET`（需要管理员权限，`allagent` 会写进机器级 PATH；仅为我安装时写用户级 PATH）。

### 附加任务（桌面快捷方式在这里选）

| 分组 | 任务 | 默认 | 说明 |
|---|---|---|---|
| 桌面快捷方式 | AllAgent.NET 终端 | 勾选 | 打开命令行聊天（`allagent chat`） |
| 桌面快捷方式 | AllAgent.NET 桌面端 | 勾选 | 打开原生窗口工作台（`allagent -guimode`） |
| 其他选项 | 将 allagent 命令加入 PATH | 勾选 | 任意终端里直接运行 `allagent` |
| 其他选项 | 补齐缺失的运行环境 | 勾选 | 见下一节 |
| 其他选项 | 安装完成后运行环境自检 | 勾选 | 完成后自动开一个控制台跑 `allagent doctor` |

取消勾选“桌面快捷方式”里的任意一项，就不会创建对应的桌面图标；开始菜单里始终保留终端、桌面端、环境自检和卸载四个入口。

### 快捷方式的行为

两个桌面快捷方式的“起始位置”都是当前用户的主目录，因此首次启动的工作区就是 `%USERPROFILE%`；进入终端后用 `/workspace D:\Projects\Demo` 或桌面端左上角的工作区切换即可换目录。

| 快捷方式 | 实际命令 | 行为 |
|---|---|---|
| AllAgent.NET 终端 | `allagent.exe chat` | 前台运行命令行聊天，关闭窗口即退出 |
| AllAgent.NET 桌面端 | `allagent.exe -guimode` | 命令行立即返回，派生隐藏宿主进程并打开 WPF + WebView2 窗口；关闭窗口结束会话 |

## 二、自动补齐的运行环境

安装时若勾选了“补齐缺失的运行环境”，安装程序会逐项检测并静默安装（已存在则跳过）：

| 组件 | 检测方式 | 安装方式 |
|---|---|---|
| WebView2 运行时 | Edge WebView2 客户端注册表（HKLM/HKCU） | 从微软官方 fwlink 下载引导程序，校验 Authenticode 签名后 `/silent` 安装 |
| ripgrep | PATH 中的 `rg.exe` | `winget install --id BurntSushi.ripgrep.MSVC` |

Python 不再由安装程序检测或安装：随包的 PowerShell SDK 与 Hyperlight 沙箱都不依赖宿主 Python，只有 Hyperlight 不可用、回退到 Monty 沙箱时才需要它，届时再运行 `allagent doctor --install` 按需补齐。

补齐失败（离线、没有 winget 等）不会中断安装，只会在完成后弹出一条提示，列出缺什么以及手动补齐命令：

```powershell
allagent doctor --install
```

Windows 10 1809 以下、非 x64 / ARM64 的系统会被安装程序直接拒绝。ARM64 安装包使用 `ArchitecturesAllowed=arm64`，x64 安装包使用 `x64compatible`（因此 x64 包也能在 ARM64 的 x64 仿真环境中安装）。

## 三、卸载

“应用和功能”或开始菜单里的卸载入口会移除程序目录、快捷方式，并把 `allagent` 从用户 PATH 中删除。

卸载结束前会询问**是否同时清除用户数据**，默认选中「是」：

- `%APPDATA%\AllAgent.NET`：模型配置、API Key、登录凭据、插件、技能、共享 Python 环境；
- `%LOCALAPPDATA%\AllAgent.NET`：更新缓存、窗口缓存与本地索引。

选择「否」会保留这些数据，重新安装后可直接继续使用。工作区项目目录下的 `.allagent\` 数据（会话、任务、审计、检查点）不会被卸载程序删除，避免误删项目历史。

## 四、本地构建安装包

前置条件：

- .NET 10 SDK
- Node.js + npm（构建 React 前端）
- Inno Setup 6（`ISCC.exe`，默认装在 `C:\Program Files (x86)\Inno Setup 6`）

```powershell
# 完整流程：还原 → 构建内嵌 PowerShell worker → 发布 → 编译安装包
tools\build-installer.ps1 -Version 1.0.7 -Runtime win-x64
tools\build-installer.ps1 -Version 1.0.7 -Runtime win-arm64

# 已经发布过 payload，只想重新编译安装包
tools\build-installer.ps1 -Version 1.0.7 -Runtime win-x64 -SkipPublish

# 前端已构建过，跳过 npm 构建
tools\build-installer.ps1 -Version 1.0.7 -Runtime win-arm64 -SkipWebBuild
```

脚本参数：`-Version`（留空取 `AllAgent.NET.Terminal.csproj` 的 `Version`）、`-Configuration`、`-Runtime`（`win-x64` / `win-arm64`）、`-OutputDirectory`（默认 `artifacts/installer`）、`-IsccPath`。

脚本做的事：

1. `dotnet restore src/AllAgent.NET.Terminal -r <rid>` — 按 RID 还原；中间只要跑过一次不带 RID 的构建，assets 文件就会丢掉 RID 目标，发布时报 `NETSDK1047`
2. `dotnet build src/AllAgent.NET.PowerShell -c Release -r <rid>` — 内嵌 PowerShell worker 是按路径收集的，必须先存在
3. 缺少 `node_modules` 时先 `npm ci`
4. `dotnet publish src/AllAgent.NET.Terminal -c Release -r <rid> --self-contained true` — 宿主与五个 worker 发布进同一个目录，共享一份自包含运行时
5. 清理 payload 中的 `.pdb` 与 `ref/`，并校验 `AllAgent.NET.Gui.exe`、`AllAgent.NET.Browser.exe`、`AllAgent.NET.ComputerUse.exe`、`AllAgent.NET.Sandbox.exe`、`AllAgent.NET.PowerShell.exe`、`Modules\`、`web\`、`builtin-plugins\` 齐全
6. 用 ISCC 编译 `installer/AllAgent.NET.iss`（`/DArchitecture=x64compatible|arm64`、`/DOutputSuffix=win-x64|win-arm64`），输出安装包与分架构 `SHA256SUMS.<rid>.txt`

版本号可以是 `1.0.7` 或 `1.0.7-beta.1`。安装包把完整版本用于显示和文件名，把数值部分（`1.0.7`）写进 exe 的版本资源；NuGet 包和 release tag 保留完整预发布版本。

## 五、发布目录结构

宿主与所有 worker 都在**同一个目录**里，共享一份自包含 .NET 运行时（`Microsoft.NETCore.App` + `Microsoft.WindowsDesktop.App`），不需要机器上安装任何 .NET：

```
allagent.exe                   终端宿主（含终端 / web-ui / gui 三种界面）
AllAgent.NET.Gui.exe           WPF + WebView2 窗口外壳
AllAgent.NET.Browser.exe       Browser Use worker
AllAgent.NET.ComputerUse.exe   Computer Use worker
AllAgent.NET.Sandbox.exe       Hyperlight microVM worker
AllAgent.NET.PowerShell.exe    内嵌 PowerShell SDK worker
Modules/                       PowerShell SDK 内置模块（worker 从这里加载）
web/                           React 工作台静态资源
everything/ 可选 Everything 文件名索引（默认不注册工具）
builtin-plugins/               内置插件
runtimes/                      各 worker 的按 RID 原生资源
```

这么放的原因：`Directory.Build.targets` 把每个 worker 的自包含发布合并到宿主目录，相同的框架文件落在同一条相对路径上自然只剩一份（原本是四份）。合并时按“相对路径先到先得”去重，并做了三件事保证不会串味：

- 宿主自身那份 net10.0 框架文件先丢弃，改由 WPF worker 提供桌面版框架（否则 WPF worker 会拿到只有门面的程序集）
- 从项目引用图继承来的 worker 构建输出（框架依赖版本）在加入自包含发布前先移除
- `Microsoft.Extensions.AI.Abstractions` 与 `System.Management` 在 `AllAgent.NET.Core` / `AllAgent.NET.ComputerUse` 中显式固定版本，避免宿主与 worker 各自解析到不同版本

## 六、CI 发布流程

`.github/workflows/release.yml`（windows-latest）：

| 触发 | 行为 |
|---|---|
| 推送 `v*` 标签 | 构建 x64/ARM64 安装包和两个 NuGet 包；创建/更新 GitHub Release；发布 nuget.org（配置了 `NUGET_API_KEY` 时）；同步 GitCode（配置了 `GITCODE_TOKEN` 时） |
| 手动 `workflow_dispatch` | 可选指定版本号；`dry_run` 只构建并上传 Actions 产物，不发 Release |

发布一个新版本：

```powershell
git tag v1.0.8
git push origin v1.0.8
```

流水线步骤：checkout → .NET 10 → Node 22 → 确认 Inno Setup 6（缺失时 `choco install innosetup`）→ 解析版本（标签去掉 `v`，或手动输入，或 csproj）→ `npm ci` → 并行构建 `win-x64` / `win-arm64`：`tools/build-installer.ps1` + `tools/build-nuget.ps1` → 合并 `SHA256SUMS.txt` → 发布 GitHub Release → 推送 NuGet → `tools/sync-gitcode-release.ps1` 同步 GitCode。

CI 细节见 [CI 与发布流水线](release-ci.zh-CN.md)。

## 七、常见问题

**安装包多大？** 当前约 **82 MB**（未压缩 payload 约 307 MB，LZMA2 压缩后约 2.7 倍）。体积来自终端宿主与五个 worker 各自的应用文件、一份共享的桌面运行时、嵌入的 PowerShell SDK 7.6.7、Hyperlight 原生组件与 Python guest；调试符号与 `ref/` 程序集在打包时已剔除。宿主与 worker 同目录共享一份运行时，是把它从 280 MB 压到 80 MB 档的关键。

**桌面端提示缺少 WebView2？** 安装时没勾选“补齐缺失的运行环境”，或当时处于离线状态。装一次 [WebView2 运行时](https://developer.microsoft.com/microsoft-edge/webview2/) 即可，也可以运行 `allagent doctor --install`。

**在安装目录里直接运行 `allagent.exe` 报 “Access to the path ... is denied”？** 那是管理员安装（Program Files）下把只读目录当成了工作区。现在程序检测到当前目录不可写时会改用 `%USERPROFILE%` 并打印一行提示，项目索引 `allagent-projects.json` 也会落到 `%APPDATA%\AllAgent.NET\`；想固定在某个项目里工作，用 `allagent chat --workspace D:\Projects\Demo` 或进入后用 `/workspace` 切换。

**ripgrep 没装上？** 需要 `winget`（App Installer）与联网。安装完成后也可以随时运行 `allagent doctor --install` 补装；Python 只在 Monty 备用沙箱需要时由该命令按需安装。

**卸载后 PATH 里还有残留？** 卸载程序会删除自己添加的那一条；如果同时手动改过 PATH，请检查 `HKCU\Environment\Path`。
