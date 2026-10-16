# LanAgent 优势总览（Windows 优先的本地 Agent）

> 元信息：源码版本 `1.0.6`（`src/LanAgent.Terminal/LanAgent.Terminal.csproj`）；检查日期 **2026-10-15**；机器 **LAPTOP-CE9T4R0L / Windows 10.0.26200**。
> 方法：源码阅读 + 本机实测。文中每条结论都标注证据位置；标注【实测】的项由本机命令输出直接支持，未标注的是读码/读文档结论。
> 配套文档：[问题与限制](lanagent-issues.zh-CN.md)。

## 一句话定位

LanAgent 是**面向 Windows 本机工作流**的终端 AI Agent：模型侧只用 `execute_code` 组织步骤，Python 在隔离沙箱里运行，所有文件/命令/桌面操作经 C# 宿主工具受控执行。它把 Windows 上最容易出错、最难做对的几件事（PowerShell 参数传递、路径歧义、桌面 UIA、microVM 与凭据隔离）收进宿主并写成默认路径。

---

## 能力地图

| 领域 | 提供什么 | 关键证据 |
|---|---|---|
| 命令执行 | `run_shell` 经 PowerShell 7.6 调度，`exe + args` 字面量传递，无字符串拼接 | `src/LanAgent.Core/Tools/SystemTools.cs:151`、`src/LanAgent.Core/Runtime/Scripts/run_shell.ps1` |
| 文件 | `file_read/file_write/file_edit/list_directory/file_search/file_glob`，页级/字节预算、diff 预览、原子写 | `SystemTools.cs`、`SystemTools.Read.cs`、`SystemTools.Search.cs` |
| 桌面 | Computer Use：`computer_*` 共 12 个工具，FlaUI UIA3，三模式 | `Tools/ComputerUseTools.cs`、`src/LanAgent.ComputerUse/` |
| 隔离 | Hyperlight microVM 优先、Monty 软件沙箱兜底、凭据不进 worker | `CodeAct/HyperlightProcessSandbox.cs`、`CodeAct/FallbackCodeSandbox.cs` |
| 权限 | `ask / full / plan` 三档、外部目录授权、审计、检查点 | `Runtime/AgentOptions.cs:38`、`Tools/SystemTools.cs:13-40` |
| 编排 | 子代理（explore/general/investigate/modify/review/verify）、后台任务、Skills、MCP、插件 | `Tools/AgentDelegationTools.cs`、`docs/subagent-configuration.zh-CN.md` |
| 环境 | `doctor` 检查 + winget 补装 + 共享 pip 环境 + `pip.md` 自动注入 | `Runtime/EnvironmentDoctor.cs`、`docs/host-tools.zh-CN.md` |
| 数据 | `lanagent.json`(v3) + `conversations/` 修订、校验和、v2 迁移备份、单写入者租约 | `Data/DataStore.cs:40`、`Terminal/DataDirectoryLease.cs` |

宿主工具总数：`rg --count-matches "\[AgentTool\(" src` = **35** 个，其中 **12** 个是 `computer_*`。

---

## 优势详解

### 1. 命令执行不靠"拼字符串"，Windows 引号/中文/空格问题被结构性消除
- `run_shell` 把 `exe + args` 序列化为 JSON → Base64 → 交给**固定可信脚本** `run_shell.ps1`，以 `pwsh -NoLogo -NoProfile -NonInteractive -CommandWithArgs <脚本文本> <payload>` 启动（`SystemTools.cs:151-176`）。脚本只有几行：解码 payload，再 `& $exe @literalArguments`。
- 被禁用的正是"命令字符串/模板"，因此参数不会被 PowerShell 二次解析。
- 【实测】`exe="node", args=["-e", …, "a b", "c\"d", "中文 路径"]` → 回显 `["a b","c\"d","中文 路径"]`，三个参数原样落位。
- 【实测】guest Python 内 `base64.b64encode(script.encode("utf-16-le"))` → `pwsh -EncodedCommand` → 输出含中文与引号的内容零损伤。
- 找不到程序时返回明确环境错误（`Executable '…' was not found on PATH or at an absolute path…`），不伪装成"白名单拒绝"。

### 2. Windows 路径与文件语义由宿主在授权之前把住
`Tools/WorkspacePolicy.cs` 是专门的 Windows 语义闸门：拒绝 UNC 工作区（非完全访问）、拒绝歧义段（点/空格结尾、`CON/PRN/AUX/NUL/CONIN$/CONOUT$/COM¹²³/LPT¹²³`）、拒绝备用数据流（根之后出现 `:`）、普通模式拒绝 reparse point（含新文件的已存在祖先）。
【实测】从宿主内部探测：
- `file_read path="NUL"` → `Windows device names and ambiguous path suffixes are not allowed.`
- `file_read path="README.md:secret"` → 直接拒绝（工作区路径非法）。

配套的大小写/分隔符语义也都按 Windows 处理：`Data/StorageTransaction.cs:13`、`Tools/FileMutationQueue.cs:7`、`Terminal/SessionOperations.cs:19`、`Tools/SystemTools.Search.cs:167`（默认 IgnoreCase）、`Tools/ShellExecutablePolicy.cs:61`（补全 `.exe/.com/.ps1/.cmd/.bat`，优先 `.ps1` 启动器）。

### 3. 截图与图片可以直接进对话（原生剪贴板 + 附件管线）
- `Terminal/ClipboardText.cs` 用 user32 P/Invoke 读 `PNG`、CF_DIB、CF_DIBV5、CF_UNICODETEXT；原生 PNG 走共享内存，老式 DIB 退回固定 STA 的 `System.Windows.Forms.Clipboard.GetImage()` 转 PNG（`:73-90`），且注释明确"只在人的粘贴手势下调用，绝不通过 Agent 工具"。
- `Terminal/PastedImages.cs` 把图片暂存到 `%TEMP%\LanAgent-attachments`（最多 8 张）并以 `@"路径"` 引用进消息。
- 【实测】`file_read docs/screenshots/iconiq-chat.jpg` → `note: Image attached to the next model request`，模型侧真的收到了图像。
- 【实测】`computer_screenshot` 返回 `imageAttached: true`，模型侧看到了实时窗口画面。

### 4. Computer Use：非侵入式默认、三种模式、只由用户开关
- 开关属于用户：`/computer on|off`；AI 只能通过 `computer_mode` 选 `background|foreground|isolated`（`ComputerUseTools.cs`）。工具描述写明 "This cannot enable a disabled Computer Use switch"。
- 默认后台模式走 UIA 语义操作（Invoke/SelectionItem/Toggle/ExpandCollapse/LegacyIAccessible 默认动作、`Value.SetValue`），**不需要焦点、不发物理按键**；物理点击/快捷键/聚焦只在 foreground 或 isolated 可用，且预检直接拒绝（`ComputerUseTools.cs:18-29`）。
- 隔离模式要求独立 Windows 会话 Worker：loopback HTTP + Bearer token，且拒绝在调用者同一台机器同一会话中运行（`ComputerUse/Program.cs`）；未配置时明确失败、**不静默回退前台**。
- 托盘支持覆盖 Explorer 图标、隐藏图标溢出、Windows 11 XAML 溢出面板与经典 `NotifyIconOverflowWindow`（`ComputerUse/SystemTray.cs`、`docs/computer-use.zh-CN.md`）。
- 有动作时在所有显示器显示柔光提示层；**仅枚举/读控件/截图不触发**提示层（`docs/computer-use.zh-CN.md`）。
- 【实测】`computer_status` → `enabled: true, mode: BackgroundOnly, workerInstalled: true, isolatedConfigured: false`；`computer_windows` 返回本机真实窗口（HWND:PID:启动时间）；`computer_tree(任务栏)` 返回 25 个节点（`TaskbarFrame`、`StartButton`、`Appid: Microsoft.Windows.Explorer` …）。

### 5. 隔离执行：microVM 优先、软件沙箱兜底，且不静默降级
- Python 始终在选定沙箱中执行；`HostPythonSandbox` 已标记 `[Obsolete]` 仅作兼容壳（`CodeAct/HostPythonSandbox.cs`）。
- worker 启动白名单只保留 Windows 系统环境变量（`SystemRoot/WINDIR/TEMP/TMP/PATH/DOTNET_ROOT(_X64)/USERPROFILE/LOCALAPPDATA/ProgramFiles/ProgramFiles(x86)/ProgramData`），其余全部剔除 → **模型服务凭据不进 worker**（`HyperlightProcessSandbox.cs:46-49`）。
- 原生运行时错误会丢弃 worker 并在下次重建；**不自动重放**已发生宿主副作用的代码。
- 显式指定 Hyperlight 时启动失败会报错而非降级；auto 模式才回退 Monty（`FallbackCodeSandbox.cs:24-51`）。
- 发布产物带 `sandbox/`（含 `HyperlightSandbox.PInvoke.dll`），桌面/终端构建把 `computer/` worker 一起打包（`Directory.Build.targets`）。

### 6. 权限与审计：分档、可记住目录、可撤销
- 三档模式 `ask/full/plan`，完全访问只取消宿主工具逐次审批、不授予管理员权限（README:42、`docs/execution.zh-CN.md:19-23`）。
- 外部目录访问弹"允许这一次 / 拒绝 / 此会话记住目录"，记住范围=该目录及子路径、仅当前会话进程；不继承给新会话或子代理（`SystemTools.cs:20-40`）。
- 工作区内文件修改有检查点，可 `/undo`；外部路径、Shell/MCP 与 Computer Use 的副作用**明确不承诺撤销**（README、docs/execution）。
- 审计写入 `%APPDATA%\LanAgent\audit.jsonl`，Computer Use 只记录参数哈希（`docs/computer-use.zh-CN.md`、`Runtime/AgentOptions.cs:55`）。
- 磁盘凭据用 Windows DPAPI（当前用户）加密（`Extensions/OAuthAuthentication.cs:29,36`）。
- 【实测】DPAPI 往返：`Protect` 得 246 字节密文，`Unprotect` 还原一致（只用随机探针字符串，未触碰真实凭据）。

### 7. 工具面与编排：单一入口、可观测、有预算
- 模型只看到 `execute_code`，其余全部经 guest 内 `call_tool(...)`（`docs/execution.zh-CN.md`、`tests/LanAgent.Tests/NativeRuntimeIntegrationTests.cs`）。
- 输出有界：工具文本 20,000 字符 / 完整 JSON 64,000 字符，超限落盘到 `<工作区>/.lanagent/tool-output/` 并返回 `stdoutPath/stderrPath`（`docs/host-tools.zh-CN.md`；【实测】本次 `dotnet test` 输出即为落盘尾读）。
- `file_read` 支持行/列分页与图片识别；`file_search/file_glob` 直接启动已安装 `rg`（Windows 上找 `rg.exe`，`SystemTools.Search.cs:194`），无命令拼接。
- 后台命令有 `shell_status` / `cancel_shell`，超时不重复启动（`SystemTools.cs`）。
- 子代理提供六种角色，支持单任务/并行/串行链，子代理向父代理返回结果与阻塞原因（`Tools/AgentDelegationTools.cs`）。【实测】本次派发的 `explore` 子代理 `exit=0 / stop=Completed`，独立产出了"非 Windows 失败点"清单，与主报告结论一致。

### 8. 环境自举：把 Windows 的现实当成默认场景
- `EnvironmentDoctor` 实测校验：宿主 Python 3.10–3.14（`-I -S -c` JSON 探针，禁用 Store 别名自动安装）、PowerShell **7.6.x**（先 PATH，再 `%ProgramFiles%\PowerShell\7\pwsh.exe`）、`ripgrep`（`winget install --id BurntSushi.ripgrep.MSVC`）。
- 缺依赖时经用户确认后用 winget 安装；安装完成提示重启再继续（`EnvironmentDoctor.cs:58-113`）。PATH 刷新会合并 Machine/User 两级。
- 【实测】本机：`pwsh 7.6.6`、`rg 15.1.0(PCRE2+JIT)`、`winget v1.29.380`、`dotnet 11.0.100-rc.1`；`dotnet test --filter FullyQualifiedName~EnvironmentDoctorTests` **10/10 通过（170 ms）**。
- 共享 pip 环境在 `%APPDATA%\LanAgent\python\venv`，`pip.md` 由实际安装元数据生成并自动注入模型上下文；后装库的 wheel README 存到 `python/docs/<包>/`，可 `python_library_docs` 分页阅读。【实测】venv = Python 3.14.4 + numpy 2.5.4 / pandas 3.0.6 / lxml 6.1.3 / pillow 12.3.0 / requests 2.34.2，与 `pip.md` 表完全一致；`python_library_docs("pandas")` 返回了 wheel 自带 README。

### 9. 数据持久化与多终端
- 原子写 + fsync + 校验和读回；`lanagent.json` 是 v3 清单，会话按修订存 `conversations/`，旧版 v2 迁移保留备份（`Data/DataStore.cs`、`Data/StorageTransaction.cs`、README 权限与数据段）。
- 同一数据目录只允许一个写入者（`terminal.lock`，`FileShare.None`）；`lanagent-projects.json` 用命名 Mutex 串行写入（`Terminal/ProjectDataDirectory.cs`）。
- `lanagent attach` 通过命名管道（`PipeOptions.CurrentUserOnly`）+ ConPTY 接入旧后台会话（`Terminal/TerminalSession.cs:19-70`、`Terminal/PseudoConsole.cs`）。
- 【实测】`%APPDATA%\LanAgent` 已有 `credentials/ python/ plugins/ conversations/ checkpoints/ skills/ lanagent.log audit.jsonl terminal.lock lanagent.legacy-v2.json.bak`，说明分层与迁移在本机真实发生过。

### 10. 界面与分发型态明确
- 终端：Spectre.Console 全屏 TUI + 鼠标事件（`ConsoleModes.cs:15-34`）、F1/F2/F3 菜单、Ctrl+E 执行详情、`F2` 切模型（README 日常使用表）。
- 桌面：Avalonia 11.2 + WebView2，Release 用内置 `EmbeddedWebServer` 服务 `avares` 资源，Debug 指 Vite（`Desktop/App.axaml.cs`）。
- 分发：`dotnet tool install --global LanAgent.Terminal`（`ToolCommandName=lanagent`，`RollForward=Major`），发布 `-r win-x64 --self-contained`；`sandbox/`、`computer/` 随包分发（`.csproj`、`Directory.Build.targets`）。
- 【实测】Debug 产物含 `computer\LanAgent.ComputerUse.exe`；release 产物含 `sandbox\`。

---

## 什么时候它最香

| 场景 | 为什么合适 |
|---|---|
| 修 .NET/C# 项目的构建与测试 | 可信别名直接跑 `dotnet/git/rg/node/npm`，输出有界可落盘 |
| 排查 Windows 窗口/终端报错 | 截图直进对话 + `computer_*` 读控件树、点按钮、验证结果 |
| 测试自研 Windows 桌面应用 | 后台 UIA 无需抢焦点；需要键鼠时切 isolated 独立桌面 |
| 企业内网机器 | WinINet 代理识别、DPAPI 凭据、winget 补依赖，不依赖 Node |
| 需要"可审计的自动化" | 三档权限、目录授权气泡、audit.jsonl、检查点撤销 |

不适合：非 Windows 平台、需要管道/重定向的复杂 Shell 流水线、需要管理员提权、需要 OCR/坐标级视觉自动化的场景（详见 [问题文档](lanagent-issues.zh-CN.md)）。

---

## 本次实测记录（2026-10-15，LAPTOP-CE9T4R0L）

| 项 | 结果 |
|---|---|
| `pwsh` 版本 | 7.6.6 ✅ |
| `rg` / `winget` / `dotnet` | 15.1.0 / v1.29.380 / 11.0.100-rc.1 ✅ |
| `EnvironmentDoctorTests` | 10/10 通过，170 ms ✅ |
| `computer_status` | enabled=true, BackgroundOnly, workerInstalled=true ✅ |
| `computer_windows` / `computer_tree` / `computer_screenshot` | 返回真实窗口 / 25 节点 / `2880×96 → 1600×53, PrintWindow` ✅ |
| `run_shell` 字面量参数与 `-EncodedCommand` | 参数原样、中文与引号无损 ✅ |
| 路径护栏（`NUL`、ADS） | 均被拒绝 ✅ |
| DPAPI Protect/Unprotect | 246 字节密文，往返一致 ✅ |
| 共享 venv | 3.14.4 + 与 pip.md 一致的库版本 ✅ |
| 子代理 `explore` | exit=0，独立复核结论一致 ✅ |

**未验证**：Hyperlight microVM 的实际执行（未跑 acceptance 测试）、isolated Computer Use（本机 `isolatedConfigured=false`）、winget 自动补装流程（未触发）、完整模型对话链路（未见已配置模型）。
