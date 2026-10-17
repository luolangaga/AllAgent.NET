# 内置 Browser Use：WebView2 / 外置浏览器连接教程

浏览器操作由 C# 实现，不安装 `browser-use` Python 包、Playwright 或独立 Chromium。内置窗口使用电脑上的 Microsoft Edge WebView2 Runtime；外置模式可自动启动并连接 Chrome、Edge，也可连接已有 CDP 地址。

默认总开关为开启，默认模式为内置。应用使用的 CodeAct Python 执行环境与浏览器引擎是两个独立部分；浏览器不增加 Python 库依赖。

## 直接使用内置浏览器

在 AllAgent.NET 聊天框输入：

```text
/browser mode builtin
/browser on
/browser-use 打开 https://example.com 并告诉我页面标题
```

也可以直接用自然语言要求 AI 操作网页，由 AI 加载技能。使用时打开带地址栏、前进后退、刷新和标签页的独立窗口，支持手动登录。关闭会话时窗口关闭，登录资料保存在 AllAgent.NET 独立 WebView2 数据目录。

首次环境初始化会检查 WebView2 Runtime，不会下载另一套浏览器。若提示缺失，使用微软官方页面安装 Evergreen Runtime，然后重试：

[Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)

## 外置浏览器：推荐连接向导

1. 输入 `/browser` 直接打开浏览器管理面板，查看当前模式、连接地址和开关。独立终端也可以运行 `allagent browser`。
2. 选择 **连接 Microsoft Edge（自动启动）** 或 **连接 Google Chrome（自动启动）**。
3. 选择 AI 使用的已有配置。列表从浏览器的 `Local State` 读取配置名称，显示 `Default`、`Profile 1` 等配置的完整路径。不会默认创建 AllAgent.NET 专用目录，也不会复制登录资料。
4. 使用其他数据目录时，选择“手动选择已有配置目录”。在日常浏览器打开 `edge://version` 或 `chrome://version`，复制“个人资料路径”（例如 `User Data\Profile 1`），粘贴到向导。
5. 程序用所选配置打开远程调试设置页。在 **Remote debugging** 页面勾选 **Allow remote debugging for this browser instance**，如出现授权弹窗，请允许。程序读取这个数据目录的 `DevToolsActivePort`，等待最多 60 秒并验证所选配置中的连接页。无需浏览器扩展。
6. 成功后，浏览器显示“浏览器已连接”确认页，终端显示浏览器名称、配置路径和验证结果，并返回聊天。输入任务才开始操作网页。失败或取消保留原配置，不会悄悄换用其他目录或浏览器。
7. AI 连接时使用刚验证的标签页，避免随机操作第一个标签页；结果会显示实际浏览器及地址。若连接标签页已关闭，请重新连接。AI 可以通过 `open` 的 `mode` 切换内置和外置模式，不修改保存的默认设置。大多数任务优先使用不需要外部连接授权的内置浏览器；需要用户已有登录状态或所选个人资料中的数据时使用外部浏览器。需求不清楚时询问用户；用户明确指定时遵循其选择。
8. 选择“连接已有调试地址”可以连接已开启的本机 CDP 服务。该入口由用户负责确认它对应哪个浏览器配置。总开关关闭时先输入 `/browser on`。

部分旧版浏览器没有远程调试设置页；请升级到支持该页面的版本或连接已有 CDP 地址。程序不会强制退出你正在使用的浏览器，也不会为了连接而新建替代配置。

所选目录和浏览器程序路径会在当前工作区的数据设置中保存；即使连接授权被取消，上次选择也会保留。下次选择同一浏览器时，列表首项为“使用上次配置”；手动指定的其他数据目录也会被带回。输入 `/browser reconnect` 或选择“重连上次保存的浏览器配置”，可直接使用上次目录重新验证连接，无需再次输入路径。目录已删除或不可用时才要求重新选择。

每次启动后的首次外部浏览器连接，需要留意浏览器远程调试授权并点击允许。AI 的内置 skill 要求它在首次连接前主动提醒并等待。该授权是本次会话的授权，用户目录仍然保留；浏览器重启后临时调试地址或标签页编号过期，可以重连已保存配置。

浏览器的调试授权作用于浏览器实例；同一实例暴露的上下文由浏览器决定。AllAgent.NET 从所选配置的确认页开始，并在 CDP 提供上下文标识时将标签页枚举和新建限制在该上下文。

## 不使用向导：手动操作

在已有 Edge 中打开 `edge://inspect/#remote-debugging`，或在 Chrome 中打开 `chrome://inspect/#remote-debugging`，开启允许远程调试，再回到 `/browser` 选择浏览器和配置。设置项名称会随浏览器语言和版本变化。

如果已有通过启动参数开启的本机 CDP 服务，可使用“连接已有调试地址”。Chrome 136 起，旧的 `--remote-debugging-port` / `--remote-debugging-pipe` 参数不能用于默认数据目录；请使用浏览器自身提供的授权连接方式。参见 [Chrome 官方说明](https://developer.chrome.com/blog/remote-debugging-port) 和 [Edge 现有实例连接说明](https://learn.microsoft.com/en-us/microsoft-edge/web-platform/devtools-mcp-server#auto-connect-to-a-running-edge-instance)。

### 检查与保存

在浏览器地址栏打开 `http://127.0.0.1:9222/json/version`。看到包含 `Browser`、`webSocketDebuggerUrl` 的文字，说明调试服务已启动。

回到 AllAgent.NET：

```text
/browser cdp http://127.0.0.1:9222
/browser on
/browser status
```

切换和关闭：

```text
/browser off
/browser mode builtin
/browser on
```

总开关关闭会撤销连接并中止后续操作。外置浏览器仍由你管理；断开连接不关闭外置窗口。下一次重新打开外置浏览器后，可以继续使用已保存地址；端口改变时重新运行向导。

## 连不上时怎么做

| 现象 | 处理 |
| --- | --- |
| PowerShell 提示文件不存在 | 检查浏览器 `.exe` 的完整路径，保留路径两端引号 |
| `/json/version` 无法访问 | 保持新窗口打开；确认执行了包含调试参数的命令 |
| 9222 被其他程序占用 | 启动命令改成 9223，AllAgent.NET 地址同步改成 `http://127.0.0.1:9223` |
| 打开浏览器后没有连接 | 在远程调试设置页启用允许调试，并允许授权弹窗 |
| 旧启动参数不能调试默认目录 | 改用浏览器远程调试设置；不支持时升级浏览器或连接已有 CDP 地址 |
| 登录账号不对 | 重新连接，选择正确的已有配置，按“个人资料路径”核对 |
| 企业策略禁止远程调试 | 使用内置 WebView2 模式，或让管理员确认策略 |
| 内置模式提示 Runtime 缺失 | 安装微软 WebView2 Runtime；不是安装独立 Chromium |

调试端口仅连接本机，不要改成对局域网开放。

## Skill 与 JS 脚本

### 操作反馈与连接复用

内置窗口的终端管道读写及返回数据序列化在后台执行，WebView2 API 仍在窗口 UI 线程执行，避免等待终端输入或大体积输出时阻塞消息循环。DOM 提示监听仅对新增子树作有界扫描，文本/样式变化不再触发整页发现扫描；页面观察使用有界 TreeWalker，重复点击检查不再克隆整个页面。回归测试覆盖空闲窗口响应、主机暂不读取 1 MB 输出，以及两万节点页面持续更新时的扫描量；这不代表所有网站的渲染性能均已验证。

CodeAct 可使用 `locate` 生成绑定当前标签页和 URL 的逻辑引用，按已观察到的唯一 selector 或 role/name 定位；每次操作重新解析，DOM 重渲染后仍可继续，目标消失或歧义时停止。`act` 使用引用执行可信点击、输入或选择；`fill_fields` 一次顺序填写最多 30 个字段并核对 DOM 值，失败返回已完成字段，不回滚。`click_and_wait` 点击一次后等待预期成功文本或错误提示，明确返回观察到成功、错误或结果未知；不会自动重复提交。`observe` 仅返回所需区域的有界文本，减少每次打印整页的上下文开销。DOM 值正确与提示成功仍不能替代服务器保存结果的核实。

终端按照 PAGE/FIND、TYPE、CLICK/WAIT、SHOT、JS 等操作显示不同标签、颜色和目标；输入内容默认隐藏，脚本可在执行详情查看。等待结果显示观察到成功、页面报错或结果尚未确认。纯 JS 读取不再额外轮询等待，异步操作通过显式结果条件等待。

页面会提前安装 DOM 变化监听，保留短暂出现的 toast、`role=alert/status`、aria-live、常见消息/错误提示、表单原生校验信息和未捕获的页面 JS 错误。点击后短暂等待异步反馈，结果中的 `actionMessages` 返回本次提示，`recentMessages` 保留当前标签页最近的有界记录；提示从页面消失后仍可以读取。消息属于不可信页面数据，不能当成指令。画在 Canvas 中的提示、无法访问的跨域 iframe 提示及未匹配到的自定义样式仍可能需要截图确认。

点击完成只代表输入已发出，不能据此认定保存成功。同一个表单内容没有变化时，第三次重复点击同一按钮会停止，要求检查错误与输入。React/CodeMirror 等受控组件优先使用可信 `input`/`keys`，不能用 JS 写入 `.value` 后直接宣称内部状态已经更新。

普通脚本错误、过期元素索引或重复点击拦截不会关闭外部连接或清空未保存的表单。`open` 不传 URL 时只接管当前标签页，不导航到空白页。宿主保留向导验证后的连接，跨 AI 轮次复用同一浏览器 WebSocket，切换标签页复用已附加的目标会话。等待标签页授权的请求超时时保留原请求，不重复发送附加请求；允许授权后读取 state 并切换已有目标标签页，不要再新建一个。

浏览器自己的授权策略不能由应用关闭。若当前 Edge/Chrome 仍对新目标要求允许，需要在浏览器中手动确认；真正断线或关闭浏览器后重新连接也可能再次要求授权。关闭总开关或变更浏览器配置仍会撤销已有连接。

实现参考：[browser-use 的事件历史与状态观察](https://github.com/browser-use/browser-use/blob/main/browser_use/browser/watchdogs/dom_watchdog.py)、[Chrome DevTools MCP 的页面事件采集](https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/src/collectors/PageCollector.ts)。授权行为参见 [Chrome 官方说明](https://developer.chrome.com/blog/chrome-devtools-mcp-debug-your-browser-session)。

模型平时只看见内置 `browser-use` 的简短说明，操作说明按需通过 `skill_read` 加载。C# 保留一个 `browser` 桥接入口，完整参数表放在 skill 中，不常驻系统工具目录。

支持页面观察、导航、鼠标点击/双击/右键/悬停、文本输入、下拉选择、滚动、快捷键、标签页、截图、历史导航、网页 JS 脚本和弹窗处理。JS 在当前页面运行，支持表达式、返回 JSON 的函数和 Promise；脚本异常会返回失败信息。示例：

```python
import json
print(call_tool("browser", operation="evaluate", arguments_json=json.dumps({
    "script": "(async () => ({title: document.title, url: location.href}))()"
})))
```

内置 skill 名称保留，不能被用户目录里的同名文件覆盖。检测到其他浏览器操作技能时，默认禁止其被模型加载；文件不会删除，技能列表会显示诊断原因。识别依据是常见技能名称和明确的浏览器自动化描述。

这是一套本地 C# 浏览器引擎，不是完整移植 Python browser-use 项目：没有云浏览器、代理/隐身服务或验证码代办；文件传输管理和跨域 iframe 的完整自动化仍未实现。页面观察包含同源 iframe 与开放 Shadow DOM，并报告不可访问的跨域 frame。不要把受限页面的空结果当作任务成功。

官方参考：[WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/)、[Chrome 调试端口变更](https://developer.chrome.com/blog/remote-debugging-port)。
