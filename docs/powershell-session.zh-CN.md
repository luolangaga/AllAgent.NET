# PowerShell 持久会话（powershell_script）

`powershell_script` 在随包内嵌的 PowerShell SDK worker 中以命名持久 runspace 执行 PowerShell 代码。变量、函数、导入的模块和当前目录在同一会话的多次调用之间保留；管道对象按现有有界投影规则返回 JSON；错误、警告、详细、调试、信息和进度六类输出流随结果一起返回。

`run_shell` 继续用于外部程序和交付脚本，`powershell_invoke` 继续用于模块目录中的单条已验证命令；需要脚本、会话状态、对象输出或 Windows 原生管理时使用 `powershell_script`。

## 调用示例

```python
r = call_tool("powershell_script", script="$os = Get-CimInstance Win32_OperatingSystem")
print(r["ok"], r["data"]["session"])
r = call_tool("powershell_script", script="$os | Select-Object Caption, Version")
print(r["data"]["records"])
```

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| script | 必填 | 完整 PowerShell 源码，最多 64,000 字符 |
| output | objects | `objects`（有界对象投影）/ `text`（纯文本）/ `schema`（类型与成员） |
| session | main | 命名会话；字母或数字开头，允许字母、数字、点、短横线、下划线，最多 64 字符 |
| reset | false | 运行前重建 runspace，清除变量、函数和导入 |
| working_directory | . | 相对或绝对目录；工作区外目录需要目录授权 |
| timeout_seconds | 120 | 0 表示不限；超时终止该会话进程树 |

## 输出模式

- `objects`（默认）：`data.records` 为每个结果对象的有界 JSON 投影（深度 5、64 属性、100 数组项、字符串 4000 字符）。建议用 `Select-Object` 收窄字段（`$os | Select-Object Caption`），并把可复用结果存入会话变量，避免重复执行命令。
- `text`：`data.text` 为每个结果的 `ToString()`，适合以文本为主的脚本。
- `schema`：`data.types` 描述去重后的结果类型（最多 5 种），每种包含 `typeNames`、`count`、`properties`（名称与类型）和 `methods`。用于在执行前确认对象形状，等价于在会话内使用 `Get-Member`，但结果有界。

格式化命令（`Format-Table`、`Format-List`）产生的是显示用记录，不是原始对象；需要对象数据时不要格式化，直接投影或 `Select-Object`。

## 结果字段

| 字段 | 含义 |
| --- | --- |
| mode、session | 本次调用的输出模式与会话名 |
| records / text / types | 对应输出模式的结果 |
| streams | `errors`、`warnings`、`verbose`、`debug`、`information`、`progress`，仅包含非空流 |
| consoleOutput | `[Console]::WriteLine` 或未进入管道的进程级输出；与协议流共用 stdout，宿主有界转存 |
| exitCode | 本次运行的 `LASTEXITCODE`；脚本未调用原生命令时为 0，出现未抑制错误或终止异常时为 1 |
| success | 无终止异常、无未抑制错误且 `exitCode` 为 0 |
| durationMs、truncated、recordCount、sessionRestarted、timedOut、notice | 诊断与状态字段，按需出现 |

脚本以默认 `Continue` 错误偏好运行：非终止错误进入 `errors` 流并使 `success` 为 false；`-ErrorAction Ignore` 或 `SilentlyContinue` 抑制的错误不计入失败。需要快速失败时在脚本内设置 `$ErrorActionPreference='Stop'`。

## 会话语义与生命周期

- 每个命名会话对应一个常驻 worker 进程；同一会话的调用串行执行，不同会话互不影响。
- 会话空闲 15 分钟后回收；同时最多保留 8 个会话，超出时按最久未使用淘汰；Agent runtime 释放时全部停止。
- 超时或用户取消会终止该会话的进程树：本次返回 `timedOut`，下次调用得到新会话并携带 `sessionRestarted=true`（状态已丢失）。
- 会话进程崩溃或 EOF 时自动重建，结果携带 `sessionRestarted=true`。
- `reset=true` 在同一进程内重建 runspace，开销小于重启进程；工作目录会回到进程启动目录。
- `working_directory` 通过 `Set-Location` 持久化在会话内（影响后续调用与脚本中的相对路径）。

## 输出预算

| 项目 | 上限 |
| --- | ---: |
| records | 512 条或约 160,000 字符，先到者生效 |
| text | 约 160,000 字符 |
| 每类流 | 32 条（progress 8 条），单条 4000 字符 |
| consoleOutput | 20,000 字符 |
| 最终工具 JSON | 由现有 `MaxToolResultCharacters`（默认 64,000）再次裁剪 |

触发上限时设置 `truncated=true`，records 模式附带总 `recordCount`。裁剪时提示用 `Select-Object` 收窄后重试。

## 安全边界

- 每次调用需要执行审批；完全访问模式跳过逐次审批。工作目录沿用既有外部目录授权流程；规划模式拒绝执行。
- 随包 worker 启动自检与超时、取消、进程树终止沿用现有机制；审批、审计（仅记录参数哈希）和角色工具白名单在 C# 宿主层实施。
- **Runspace 不是安全边界，worker 也不是沙箱**：脚本以当前用户权限运行，可访问文件、网络、注册表和系统状态。已批准脚本的副作用与 `run_shell` 相同，不能通过 `/undo` 自动撤回。
- 模块安装仍通过能力商店；提升权限、修改系统状态需要用户明确授权。

## Windows 原生管理

| 领域 | 常用命令 | 说明 |
| --- | --- | --- |
| 设备与硬件 | `Get-CimInstance Win32_*`、`Get-PnpDevice` | 系统信息、驱动与设备状态 |
| 事件日志 | `Get-WinEvent` | 按日志名、级别与时间筛选系统崩溃和故障 |
| 网络 | `Get-NetTCPConnection`、`Get-NetAdapter` | 端口、连接与网卡配置 |
| 注册表 | `Get-ItemProperty HKLM:\...` | 与文件系统一致的 provider 路径语法 |
| 证书 | `Get-ChildItem Cert:\` | 证书存储、有效期与用途 |
| 进程与服务 | `Get-Process`、`Get-Service`（含 `-Include` 需要 `Get-CimInstance Win32_Service`） | 运行状态与启动配置 |
| 计划任务 | `Get-ScheduledTask`、`Get-ScheduledTaskInfo`（ScheduledTasks 模块） | 任务定义与上次运行结果 |

可用范围取决于 Windows 版本、已安装模块和当前权限；预加载 Management、Utility、Security、Diagnostics 和 CimCmdlets。`Get-Command`、`Get-Help`、`Get-Member` 在会话内直接可用，用于发现命令与对象形状，不需要额外工具。

## 与其他工具的关系

| 工具 | 用途 | 审批 |
| --- | --- | --- |
| `run_shell(exe, args)` | 外部程序；参数为字面量数组，不解析脚本文本 | 可信别名免审批，其他逐次 |
| `powershell_invoke(module, command, parameters)` | 目录中单条已验证命令，类型化参数绑定 | 每次审批 |
| `powershell_script(script, ...)` | PowerShell 脚本、持久会话、对象/流的完整能力 | 每次审批 |

需要交付或长期复用的 `.ps1` 脚本仍用 `file_write` 写入后以绝对路径交给 `run_shell.exe` 执行。

## 实现说明

- worker 新增 `--serve` 模式：stdin/stdout 逐行 JSON（`ready` / `run` / `result` 帧），同进程内复用 runspace；对象投影与 `--invoke` 共用同一实现。
- 既有 `--probe`、`--execute`、`--invoke`、`--command-info`、`--modules` 模式不变。
- 宿主侧由 `PowerShellSessions`（`src/AllAgent.NET.Core/Runtime/PowerShellSessions.cs`）管理进程、帧协议、超时与回收；测试见 `tests/AllAgent.NET.Tests/PowerShellScriptTests.cs`。

```powershell
dotnet test tests/AllAgent.NET.Tests -c CodeActValidation
```

参考：[PowerShell SDK](https://learn.microsoft.com/en-us/powershell/scripting/developer/windows-powershell)、[about_Output_Streams](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_output_streams)、[Get-CimInstance](https://learn.microsoft.com/en-us/powershell/module/cimcmdlets/get-ciminstance)、[Get-WinEvent](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.diagnostics/get-winevent)、[about_Registry_Provider](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_registry_provider)。
