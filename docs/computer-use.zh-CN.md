# Windows Computer Use

使用 FlaUI.UIA3 5.0.0，通过 C# 宿主工具执行；Python CodeAct 只编排 `call_tool`，不直接访问 Windows API。默认关闭，启用后的默认模式为后台。支持 UIA 与窗口截图；目前截图使用 PrintWindow，GPU/透明窗口可能返回不完整画面。尚未接入 Windows Graphics Capture、OCR 或坐标动作。

## 开关和三种模式

聊天中输入：

```text
/computer on
/computer status
/computer off
```

独立命令（无需配置模型或启动 Hyperlight）：

```powershell
allagent computer on
allagent computer status --json
allagent computer off
```

用户只控制总开关；AI 使用 `computer_mode(mode="background|foreground|isolated", reason="任务需要…")` 自主选择操作模式。每次启动默认后台，不恢复上次的前台模式。`computer_status` 会告诉 AI 开关、模式和独立环境配置状态。旧的 `/computer mode` 命令会提示由 AI 选择，不再要求用户手动切模式。

总开关保存到所选 `--data-dir` 下的 DataStore 设置。已有聊天进程运行时，用该聊天中的 `/computer` 修改共享开关；独立命令不热更新另一个已运行进程。父/子 Agent 共用开关和串行操作锁；模式切换等待当前操作结束，并撤销使用旧模式排队的请求。关闭可取消正在执行的本机 Worker。总开关关闭后，AI 无法通过模式选择工具将它重新开启。

| 模式 | 可用操作 | 使用场景 |
|---|---|---|
| `background` | 控件点击、输入整段文本、选择、切换、展开、滚动、读取与验证；优先 UIA patterns，可用 LegacyIAccessible 默认动作 | AI 写完软件后的界面测试，默认选择 |
| `foreground` | 上述操作，加窗口激活、物理点击和快捷键 | 用户允许直接操作桌面时 |
| `isolated` | 在独立 Windows 会话/VM Worker 中执行上述操作 | 用户与 Agent 使用各自的桌面 |

同一套 `click` 和 `type_text` 操作在后台模式自动选择控件语义操作，不需要用户先聚焦窗口。`type_text`/`set_value` 对支持 Value pattern 的控件设置完整内容；`click` 优先 Invoke、SelectionItem、Toggle、ExpandCollapse，再尝试有默认动作的 LegacyIAccessible。控件不支持时，AI 应根据任务改选独立环境或前台。

当前用户桌面共享真实键鼠与焦点，无法给任意应用提供第二套独立输入；完整键鼠任务要在后台完成，需要独立 Windows 桌面/虚拟机。独立环境中的应用和用户已经打开的本机应用不是同一个实例，切环境后必须重新枚举窗口。用户明确要求不打断时，AI 不得默默回退到前台。UIA Invoke 仍可能让目标应用自行弹窗或切换焦点；最小化窗口可能不提供完整的 UIA 树。自绘应用是否可操作取决于其可访问接口。

所有会改变界面的工具标记为 Execute，遵循现有 `/permissions ask|full|plan`：ask 逐次审批、full 免审批、plan 拒绝操作。关闭状态即使 full 也拒绝执行。工具调用记录在 `audit.jsonl`，只记录参数哈希。电脑上的修改不能通过工作区文件检查点撤销。

## Agent 操作流程

AI 首次执行点击、输入、聚焦或托盘动作前，本机所有显示器都会显示浅蓝色边缘柔光、缓慢环绕的细边框和底部胶囊提示：“AI正在操作你的电脑，建议不要进行其他操作”。布局与柔光衰减参考 [omni-computer-use 的 MIT 开源 overlay](https://github.com/Jason26214/omni-computer-use/blob/16e4b7acc416a51bb6583dfe89cefff5249f799b/src/omni_computer_use/overlay.py)，版权声明随程序分发。提示层不抢焦点、不拦截键鼠，不进入任务栏或 Agent 的窗口列表。仅枚举窗口、读取控件与截图不会启动提示层。

同一轮任务从首次动作起持续显示，至少保留 3 秒；最后一次动作/任务结束后延迟 3 秒再淡出。延迟期间的新操作复用原窗口并取消退出，切换模式不会重建提示层。关闭开关或取消任务立即撤销输入执行，提示切换为“已停止电脑操作”后延迟收起；任务完成显示“本次电脑操作已结束”。宿主退出时管道 EOF 会在满足最短显示时间后关闭窗口，不留下长期悬浮窗口。独立环境中的提示层显示在被操作的桌面，连续请求也会复用它。

Core 库调用者可以用 `using var activity = options.ComputerUse.BeginActivityScope(ct)` 包住自己的完整操作任务，使提示层在多次调用之间保留；不创建作用域时，每个动作结束后开始延迟退出。提示是告知，不提供另一套键盘焦点，也不改变后台 UIA 的能力。

托盘应用：`computer_tray` 返回图标和 `overflowControls`（隐藏图标展开按钮）。图标不在当前列表时，先对观察到的展开按钮执行 `computer_action(action="click")`，再读取托盘。`computer_tray_action` 的 `open` 使用语义默认动作；单击/双击/右键使用 foreground 或 isolated。右键后重新枚举窗口、读取菜单并调用其中的“打开/设置”项，再验证应用窗口出现。Windows 11 XAML 溢出面板（TopLevelWindowForOverflowXamlIsland）及经典 NotifyIconOverflowWindow 都纳入发现范围。托盘属于 Explorer，不能通过激活隐藏的应用主窗口替代托盘操作。

本机 WinIsland 实测流程：展开隐藏图标 → 找到“WinIsland - 灵动岛” → 右键打开“上下文”菜单 → UIA 调用“打开设置”。默认动作仅代表图标收到调用，不能自动认定设置窗口已打开。

`computer_screenshot` 捕获指定窗口，不激活窗口。图片经宿主验证后作为真实图像消息送入模型下一次请求，并保存到内部观察历史；Base64 不写入 Python 输出或工具卡片。每步最多保留最后两张图。截图卡片展示尺寸、方法和目标窗口。

AutomationId 是可选 UIA 属性。树节点缺少它时按空值处理；节点属性不可用时仍继续遍历子节点，不把根属性错误当成空树。已在本机 WinIsland 验证 Control View 可读取 CPU、GPU、网络等结构。

1. `computer_windows` 获取 `windowId`（绑定 HWND、PID 与进程启动时间）。
2. `computer_tree(windowId=...)` 获取有界控件树和临时 `elementId`，返回角色、名称、AutomationId、坐标、状态、支持的 patterns 和 Value 文本；密码内容不返回。
3. `computer_action(windowId=..., elementId=..., action=...)` 使用控件支持的语义操作。
4. `computer_wait` 等待明确的控件/状态/文本，并再读控件树或检查业务输出；API 完成不代表任务完成。

`computer_tree` 支持 `offset` 分页，使用返回的 `nextOffset` 继续。`computer_find` 按精确 name/automationId/role 查找；匹配多个控件时不要盲选第一个。树通过 Control View TreeWalker 有界遍历，避免一次物化上千子节点。元素 ID 同时验证 RuntimeId、角色、名称和 AutomationId；控件改名或重建后，旧 ID 拒绝执行。

```python
r = call_tool("computer_tree", windowId="从 computer_windows 返回的真实 ID")
if r["ok"]:
    for e in r["data"]["elements"]:
        if e["name"] == "Apply" and "Invoke" in e["patterns"]:
            print(call_tool("computer_action", windowId=r["data"]["windowId"], elementId=e["elementId"], action="invoke"))
```

支持 action：`click`、`type_text`、`invoke`、`set_value`（使用 text 参数）、`select`、`toggle`、`expand`、`collapse`、`scroll_up`、`scroll_down`。`double_click`、`right_click`、`computer_focus`、`computer_keys` 使用前台或独立环境。快捷键例：`CTRL+S`、`ALT+F4`、`RETURN`。Foreground/isolated 的 type_text 在控件无 Value pattern 时才使用聚焦后的 Unicode 键盘输入。不要猜测 ID 或自动重试可能已经完成的写操作。

内置 [computer-use SKILL.md](../src/AllAgent.NET.Core/Skills/computer-use/SKILL.md) 随 Core 程序集分发，启动时为老用户也补齐；用户已编辑的技能不会被覆盖。Agent 可以 `skill_read(name="computer-use")` 加载，用户也可用 `/computer-use 任务说明` 调用。技能教 AI 自动选模式、软件测试优先后台、定位控件、处理不支持的操作与核验结果。

终端卡片会展示窗口名称和数量、控件数量、所选模式、目标窗口/控件、输入摘要、读回结果和真实执行方式。条件等待成功显示“已观察到预期界面状态”；动作返回显示“任务结果需界面验证”。Python 结束但内部工具失败时，总标题显示“含 N 项操作失败”，不会统一标成成功。

## 隔离模式部署

需要自己准备独立 Windows VM 或另一个交互登录会话；功能不会创建 VM、切换用户桌面或绕过 UAC。Worker 必须在目标交互桌面内启动，不能从 SSH 服务的 Session 0 启动后假定可以控制桌面。

1. 在独立 Windows 环境安装 .NET 8 **Desktop Runtime**，复制发布输出中的整个 `computer/` 目录。
2. 创建仅自己可读的 token 文件，内容至少 32 字符。在目标桌面终端启动：

   ```powershell
   .\AllAgent.NET.ComputerUse.exe --serve 18765 C:\Agent\computer-token.txt
   ```

   服务仅监听目标机 `127.0.0.1`。若 HttpListener 报访问拒绝，管理员为运行账户配置该 loopback URL 的 URL ACL；不向公网绑定。

3. 使用可信 SSH 隧道等方式将本机端口转发到 VM 的监听端口，例如（监听 Worker 已在交互桌面运行）：

   ```powershell
   ssh -N -L 18765:127.0.0.1:18765 your-user@your-vm
   ```

4. 将同一 token 安全保存到本机文件，配置并启用：

   ```text
   /computer isolated http://127.0.0.1:18765/ C:\Agent\computer-token.txt
   /computer on
   ```

AI 再根据任务调用 `computer_mode(mode="isolated", reason="需要完整后台键鼠")`。配置存在不代表环境可用，首个观察请求仍会检查连接、认证与实际会话。Worker 拒绝与调用者相同机器、相同 Windows Session 的请求；没有配置、连接失败、非交互会话或身份验证失败都会报错。没有 VM/独立 Session 的机器不能直接获得完整后台键鼠能力。

本机请求 20 秒超时，取消时终止 UIA 子进程；隔离服务每个请求启动子进程，15 秒超时后终止。关闭会阻止后续操作；已送达隔离环境的单次动作可能先完成，不能撤销已经发生的 UI 变化。查看状态后再决定是否重新操作。

## 构建与验证

```powershell
dotnet build src/AllAgent.NET.Terminal
dotnet test tests/AllAgent.NET.Tests --filter FullyQualifiedName~ComputerUseTests
```

Terminal 构建、发布及 .NET Tool 打包均包含 `computer/` Worker。Core 保持 `net10.0`，Windows Worker 为 `net10.0-windows`；库调用者可用 `AgentOptions.ComputerUse.Configure(...)`，并设置 `WorkerPath`。FlaUI Worker 需要 Windows Desktop Runtime；其他 Agent 操作不因此要求该运行时。Worker 在专用 MTA 进程中运行，启用 PerMonitorV2 DPI。

测试窗口专用于验证后台 SetValue、Invoke 和状态读取，不触碰用户已打开的软件。独立 VM 模式的真实端到端验收需要已配置的独立环境。

本轮设计对照了 [微软 UFO 的控件动作与 ID/名称验证](https://github.com/microsoft/UFO/blob/main/documents/docs/mcp/servers/app_ui_executor.md) 和 [Windows-MCP 的有界树遍历](https://github.com/CursorTouch/Windows-MCP/blob/2be40438df05adaf191cdc3ccd8290470ccd1003/src/windows_mcp/tree/service.py)，实现仍使用项目要求的 FlaUI。具体借鉴点是：先定位再执行、动作后观察、遍历预算、控件身份验证与语义/物理动作分层；未直接复制其 Python 实现。

参考：[FlaUI](https://github.com/FlaUI/FlaUI)、[微软 UIA Control Patterns](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-controlpatternsoverview)、[UIA 线程要求](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)。
