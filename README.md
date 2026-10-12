# LanAgent

Windows Agent with a Spectre.Console terminal, a reusable .NET 8 engine, and Hyperlight microVM CodeAct execution. The existing Avalonia/Vue desktop remains available and uses the same engine.

## 终端使用

角色配置还可设置 `permissions: [{"action":"read","resource":"*.env","effect":"ask"}]`。规则按顺序匹配，最后一条生效；支持 `*`、`?`，Windows 不区分大小写并统一路径分隔符。工具名称可用宿主名称，也可用 `read/write/edit/list/bash/webfetch/websearch/skill/task` 对应名称。文件资源以规范化绝对路径匹配，因此可使用 `*/src/*` 或 `*.env`；Shell 资源是程序与字面参数的展示文本。未提供 permissions 保持已有宿主授权行为；提供数组后未匹配操作要求确认，空数组表示全部逐次确认。`allow` 仍受父代理权限与宿主检查约束，`deny` 直接拒绝，`ask` 在完全访问模式下也要求实际确认；非交互 JSON 模式拒绝无法确认的调用。每角色最多 256 条规则；插件工具可按注册名称控制，未映射工具的资源为 `*`。当前角色规则授权仅支持本次允许/拒绝，尚无“始终允许”记忆规则。

终端支持打包为 .NET Tool，安装后的命令为 `lanagent`。本地打包、安装及 NuGet.org 发布步骤见 [.NET Tool 说明](docs/nuget-tool.zh-CN.md)。

在项目根目录运行 `./agent.ps1`，按 **F2** 配置或选择模型（也可以 `/login` 登录），然后直接输入想完成的任务。Agent 会自动使用工具，并按需派发子代理；不需要 `/agent` 或切换工作模式。默认打开全屏聊天，`--inline` 使用普通终端滚动模式。

输入 `/` 或按 **F1** 只展示常用操作；需要高级功能时按 **Ctrl+P** 搜索所有命令，或输入 `/help all`。键入 `/mcp`、`/skills` 等前缀仍可补全相应子命令。`/new` 开始新任务并保存旧会话，`/resume` 选择历史会话，双击 Esc 确认停止正在执行的任务，`/settings` 集中设置主题、思考内容和工具详情。旧版 `/clear`、`/quit`、`/rewind` 等别名仍可使用，但不再重复展示。

例如可以直接说：“搜索这个库的用法，安排一个子代理检查兼容性，然后在当前项目实现”。子代理默认继承当前模型和提供商；AI 可在单次派发时指定 `model` / `provider`。用 `/subagents 配置名 [模型 ID]` 保存子代理默认配置，用 `/subagents inherit` 恢复跟随当前模型。启动时 `--subagent-provider 配置名 --subagent-model 模型ID` 可临时覆盖默认值。`/tasks` 查看子代理进度、结果和待处理授权；需要手动把完整任务放后台时用 `/background 任务描述`。

任务运行中输入文字并回车，会将下一条任务保存到当前会话的收件箱，当前任务结束后依次处理；重启后仍可恢复，图片保存排队时的内容。`/steer 说明` 在模型步骤边界更新当前任务，不打断已经执行的工具。按 **Ctrl+X 然后 Q** 或输入 `/queue` 打开待处理输入面板：方向键选择，Enter 转为 steer，Ctrl+D/Delete 删除，Ctrl+U 恢复到现有草稿，Tab 移回队列。命令方式可用 `/queue list`、`/queue steer|queue|edit|delete ID`；启动环境失败时输入仍保留，修复后 `/queue run` 重试。每个会话最多保留 100 条待处理输入，尚未实现收件箱内的压缩和工作区移动控制项。

空闲时按 **Ctrl+X 然后 G** 或输入 `/timeline` 打开用户消息时间线，输入文字搜索、方向键选择并定位，Enter 进入跳转、复制、恢复草稿、历史分支或撤回操作。`/timeline list` 列出消息 ID，`/timeline jump|copy|restore|fork|revert 消息ID` 可直接操作。`/fork` 选择完整会话或历史消息边界，`/fork --before 消息ID [标题]` 复制该用户消息之前的历史，并将所选输入及图片恢复为未提交草稿；`/branch [标题]` 仍复制完整会话。分支会重新生成消息 ID 并保留有效的压缩摘要边界。旧记录没有原始草稿字段时使用已有正文，可能包含旧版文件引用展开内容。

`/timeline revert 消息ID` 从选定任务边界撤回连续多轮宿主文件工具修改，`/undo` 或 `/rewind` 撤回最近可见任务。确认前会检查文件是否被外部修改；撤回后原历史继续保存，所选输入恢复为未提交草稿，状态可跨重启恢复。`/redo` 恢复原文件和历史；提交有效新任务才丢弃被撤回的历史并禁用旧检查点重做。撤回期间暂停自动输入与通知续跑，不能压缩会话。有活动后台任务时拒绝撤回。文件事务使用持久恢复日志；启动时根据已保存的会话状态完成或回滚，检测冲突后保留日志并拒绝覆盖。当前只支持具有完整任务检查点的消息边界，运行中 steer 插入点、Shell/MCP 外部副作用及运行中的时间线仍未实现精确撤回。

自定义子代理角色：`/agents list` 列出角色，`/agents show 名称` 查看完整配置，`/agents import 路径.json` 导入数组并按名称合并，`/agents remove 名称` 删除自定义角色或重置内置覆盖；运行中的角色不能删除。独立命令 `lanagent agents ... --json` 同样可用，无需启动沙箱。示例见 [agent-roles.example.json](docs/agent-roles.example.json)。字段为 `name`、`description`、`instructions`、`readOnly`、可选 `model`/`provider`；provider 可填写已启用提供商的配置 ID 或唯一名称。新建或切换角色时应用角色默认模型，显式 model/provider 优先，继续相同角色时保留已选模型。配置保存在当前数据目录并跨重启保留；Explore 的只读约束不能通过配置取消。当前最多 64 个配置角色，名称使用小写字母、数字、下划线或连字符，仍固定一层子代理深度。

终端的 CodeAct 宿主工具新增 `subagent(agent, description, prompt, sessionID?, background?, model?, provider?)`：`general` 执行通用任务，`explore` 仅搜索分析，并由宿主拒绝写入、Shell 和 MCP 外部操作。默认前台等待最终回复，`background=true` 立即返回任务 ID 与子会话 ID。传回 `sessionID` 可以继续同一父会话下已经结束的子代理，保留历史和已选模型；对子会话的运行中调用会将文本指令持久入箱为 steer，并加入原任务，下一模型步骤边界接收，不打断正在执行的工具。完成事件后的收尾边界也检查追加输入，避免已接收指令无人续跑。取消中的任务拒绝追加；等待授权或回答时追加指令不会自动授权或回答，通过 `/tasks` 处理后继续。运行中指定 model/provider 会与追加输入一起保存，当前响应保持原配置，下一模型步骤使用新配置、认证及上下文容量；父会话不受影响。响应原生状态按配置来源标记，切换后及重载历史时过滤不匹配状态。运行中更换 general/explore 会与追加输入一起保存，并实时更新宿主工具限制、下一步骤系统指令。explore 即使在父代理完全访问模式下也不能写入或执行外部操作；审批等待后执行前仍会重查角色。父代理只读模式不能被切回 general 绕过，已经开始的工具不会强制中断；程序化 subagent 的新任务和 steering 均在接收前异步展开工作区 @文件引用，图片与文本预览按当时内容保存；缺失或损坏附件拒绝接收，不创建子会话或任务。图片支持 PNG/JPEG/GIF/WebP，沿用 8 个引用、单图 4MB、图片合计 12MB 的限制；引用路径仍受工作区规则限制。旧 `start_agent` 等工具保留兼容。后台完成、失败或取消后会将结果持久投递给父会话并自动继续处理；每次运行独立去重。ANSI 终端等待输入时可被结果唤醒并保留草稿；非 ANSI 输入等待暂需用户输入后处理。

### 图片输入（终端）

输入 `看这张图 @截图.png`，或 `分析 @"C:\Pictures\截图.png"`。PNG、JPEG、GIF、WebP 作为真实图片内容发送给模型，不会作为 UTF-8 文件读取。单图最多 4 MB，每条消息最多 8 张、总量最多 12 MB；按文件头校验类型。图片按提交时的内容保存在会话中，原文件之后改变或删除不会影响已保存的记录。JSON 和 Markdown 导出包含图片数据。

Windows 输入框右键、Ctrl+V / Shift+Insert 优先读取剪贴板图片，支持注册 PNG 及普通位图/DIB，异步转换并暂存附件；回车才提交。有输入文字或消息选区时，右键复制选区。终端发出空粘贴事件时也会尝试读取剪贴板图片。粘贴独立图片路径、多条带引号路径或 file:// 图片地址时，会暂存图片内容并转换为附件引用；普通说明文字、混合无效路径保持原文。草稿显示图片数量和名称，Alt+Backspace 移除最后一张，Ctrl+Z 恢复。截图和路径暂存分别位于系统临时目录的 `LanAgent-clipboard`、`LanAgent-attachments`，目前没有自动清理。原生 PNG/DIB 剪贴板、真实 ConPTY 交互及发送图片到模型请求均已通过本地验收。

OpenAI Chat、Responses、Anthropic 和 Gemini 请求均接入图片内容块。模型本身仍须支持视觉；文本模型可能由服务端拒绝，不能据预设名称保证支持。付费视觉模型尚未逐个实测。自动整理和 /compact 会将历史图片分批发送给总结模型并合并视觉结论；原始图片保留在完整历史或压缩前归档中。会话显示图片附件名称、格式和大小。文件拖入和附件选择列表仍需补齐。

### 联网搜索与网页读取

`web_search` 已内置到 C# 主机，参考 [open-webSearch](https://github.com/Aas-ee/open-webSearch) 的多引擎搜索方式。启动 Agent 后直接描述要搜索的内容即可，搜索无需安装 Node.js / npm、填写搜索 API Key 或运行守护进程。默认先用 DuckDuckGo，失败或无结果时依次尝试 Bing、百度；AI 也可以明确选择一个或多个引擎，多引擎并行搜索后合并、去重，最多返回 10 条结果。

`web_fetch` 同样开箱即用，读取公开 HTTP/HTTPS 文本网页，不执行 JavaScript，私有网络目标与重定向会被拦截。工具结果作为网页资料处理，不会覆盖用户或项目指令。内置搜索和网页读取仍需要互联网连接，搜索引擎拒绝请求或暂不可用时会返回错误信息。

旧版 open-websearch 本地服务仍可选用：手动运行 `./tools/start-web-search.ps1`，再在启动 Agent 的终端设置 `$env:LANAGENT_SEARCH_URL='http://127.0.0.1:3210'`；嵌入宿主可设置 `AgentOptions.WebSearchBaseUrl`。此可选路径需要 Node.js / npm，脚本复用已安装的固定版本 2.2.0。清除 `LANAGENT_SEARCH_URL` 后恢复内置搜索；`./agent.ps1`、发布版 exe 和桌面程序默认均使用内置搜索，不会自动启动外部服务。

模型请求把稳定指导、按固定顺序排列的工具契约放在提示词前部，项目与会话上下文后置；Anthropic 请求还包含系统与历史消息的缓存断点。缓存命中仍由模型支持、最小长度和提供商策略决定，可从真实返回的缓存 Token 用量观察，不能保证固定命中率。


消息历史按宽度与内容版本缓存；滚动保留阅读位置，并限制在实际内容范围内。输入框保留多行、撤销和选择功能，绘制、鼠标定位与上下移动共用终端字符宽度布局，支持中文、表情和自动折行。Spectre.Console 的 TextPrompt 用于单行配置；聊天编辑器是基于 Spectre `IRenderable` 的多行组件。

消息拖选限制在起始消息块，表格限制在起始列，跨行复制排除其他消息、侧栏和表格边框。文件 `@` 候选通过后台目录索引补全，缓存刷新时保留旧候选；候选区滚轮选择、鼠标点击补全，消息区滚轮滚动历史。同方向滚轮事件合并处理，连续输入不再逐事件等待 33 毫秒。

加载指示默认 **Mindblown**。用 `/spinner` 或 `/settings` → 加载指示选择样式，`/spinner list` 查看所有样式，`/spinner Dots` 直接设置；设置跨重启保存。模型/MCP 连接、沙箱启动与检查使用实际 Spectre `Status`；需要接受安装授权或 Esc 取消的等待共用全屏加载状态，避免并发占用终端输出。

AI 的 `ask_user` 选项问题使用实际 Spectre `SelectionPrompt` / `MultiSelectionPrompt`，方向键选择、空格勾选、Enter 确认、Esc 拒绝，也支持输入其他回答。`multiple=true` 启用多选，返回 JSON 字符串数组；问题展示期间暂停 Live，完成后恢复聊天和草稿。没有选项时仍使用聊天内自由回答。

已验证的 `artifacts/native-search-win-x64/lanagent.exe` 及其 `sandbox` 子目录自带 .NET 运行时与内置搜索依赖；启动用 `./artifacts/native-search-win-x64/lanagent.exe chat --workspace .`。保留整个发布目录，不能只复制单个 exe。搜索不需要额外安装；Agent 执行所需的 Python、PowerShell 7.6 和 Windows 虚拟化仍需通过环境检查。

| 操作 | 快捷键 / 命令 |
|---|---|
| 搜索操作 | F1 菜单、Ctrl+P 命令面板、输入 `/` 自动提示 |
| 模型 / 提供商 | F2、`/model`、`/providers`、`/models` |
| 思考强度 | F6、Ctrl+T 循环级别、`/effort` |
| 历史会话 | F3、`/resume`、`/branch`、`/rename` |
| Skills | F4、`/skills`；用户和项目级技能，支持导入完整 SKILL.md 包 |
| MCP | F5、`/mcp`；连接状态、诊断、工具 Schema、资源与提示模板 |
| 输入编辑 | Ctrl+J / Shift+Enter / Alt+Enter 换行；↑↓ 多行移动；Ctrl+↑↓ 历史；Ctrl+R 搜索历史 |
| 选择与剪贴板 | 主输入框鼠标拖选；Shift+方向 / Home / End 选区；Ctrl+A 全选；Ctrl+C/X/V 复制/剪切/粘贴；Ctrl+Z/Y 撤销/重做 |
| 文件引用 | `@路径` 自动补全；含空格用 `@"路径"`；每文件预览最多 4000 字符 |
| 历史滚动 | 鼠标滚轮、PgUp/PgDn；新消息保持阅读位置；Ctrl+End 回底部 |
| 子代理 / 后台任务 | AI 按需派发；`/tasks` 查看结果 / 授权 / 回答 / 取消；`/subagents` 设置默认模型 |
| 通用 DLL 插件 | F1 → 插件；`/plugins search` 搜索 NuGet 的 LanAgent 标签、安装 / 禁用 / 卸载 |
| Hooks / 账号登录 | `/hooks` 生命周期回调；`/login` 选择 Codex 或认证插件；`/logout` 退出 |
| 思考、工具和主题显示 | `/settings` 集中设置；运行中 Ctrl+O 切换详情 |
| 任务控制 | 双击 Esc 确认 / Ctrl+C 停止；运行时输入下一条任务并回车可排队 |
| 文件变更 | `/diff`、`/checkpoints`、`/undo`、`/redo` |
| 主题 | `/themes`：夜色、暖色、浅色；选择持久保存 |
| 加载指示 | `/spinner`、`/spinner list`、`/spinner Mindblown`；默认 Mindblown，可在 `/settings` 中修改 |
| 规划和上下文 | Tab 切换只读规划、`/plan`、`/context`、`/compact` |
| Token 统计 | `/stats` 按提供商和模型查看累计输入、输出、总 Token 和请求数 |

`/stats` 从启用此功能后的请求开始累计，以接口返回的真实用量为准，包含多轮调用、后台任务 / 子 Agent、自动上下文整理和 `/compact`。统计保存在数据目录的 `lanagent.json`，跨重启保留，分支、清空对话、撤销和删除会话不影响累计值。旧历史无法补算；接口未返回用量的请求会单独标明，不会使用字符估算补入 Token 合计。独立命令 `./agent.ps1 stats --json` 输出各模型统计和总计。

授权和自由回答问题以聊天内交互气泡显示，保留执行记录、底部草稿与上下文状态；有选项的问题使用上文的 Spectre 选择组件。写文件授权展示中文路径、差异和行数；未信任的 Shell 程序授权逐项展示真实路径和参数。默认拒绝，Y 允许一次，N/Esc 拒绝，Tab 展开，滚轮或 Ctrl+↑↓ 查看预览。工作区外的有效路径先询问目录访问，A 可记住本会话目录；文件写入和未信任的 Shell 程序需要单独审核。`/permissions directories` 查看、追加 `clear` 撤销。Agent 可通过隐藏的 C# `ask_user` 工具请求选项或自由回答；用户等待不占用 Python 执行预算。

输入框的选中文字显示蓝色背景，输入或粘贴会替换选区；中文、emoji 和组合字符按完整字素选择。Ctrl+C 在有选区时复制，没有选区时清空输入 / 取消运行；模型运行时也可编辑并复制下一条草稿，不会因复制而中断任务。Ctrl+Home/End 移到文本首尾，Ctrl+Shift+Home/End 扩选；Ctrl+Insert / Shift+Insert 也可复制 / 粘贴。长文本自动显示光标所在部分，最多 32000 字符，多行粘贴不会自动提交。管理弹窗支持相同键盘编辑；密码字段仍隐藏内容并禁用复制。当前系统剪贴板接入针对 Windows。

提供商有 16 个可编辑预设：OpenAI、Codex / ChatGPT、Anthropic、Gemini、DeepSeek、百炼、Moonshot、智谱、SiliconFlow、OpenRouter、Groq、Mistral、xAI、Ollama、LM Studio、自定义。自定义配置支持 Base URL、OpenAI/Responses/Anthropic/Gemini 协议、Key 或环境变量、自动拉取与手动维护模型列表。思考参数按协议分别生成，具体模型支持的级别仍由服务端决定；预设不代表每个模型都经过付费接口实测。

检查点保存在数据目录的 `checkpoints` 中，按工作区隔离。每轮记录文件工具写入前后的字节及对话，可跨重启撤销/重做；还原前检查全部文件内容，遇到用户后续修改会拒绝还原。每轮最多 128 个文件、每文件 1 MB、总备份约 20 MB。Shell/MCP 的外部效果不在还原范围内；还原不是跨文件事务，外部并发修改或磁盘故障仍需人工处理。检查点包含文件与对话内容，按普通本地用户数据保存。

账号登录、设备码、ChatGPT 模型自动连接及当前接入边界见 [中文登录说明](docs/login.zh-CN.md)。

功能实现和剩余范围见 [终端功能对照表](docs/terminal-product.md)。

会话操作：`/children [ID]` 选择当前父会话下的子代理，`/parent` 返回父会话；运行中的子会话可查看历史，继续输入需先完成或取消其后台任务。Explore 子会话继续执行仍受宿主只读权限限制。`/copy` 复制会话 Markdown，`/copy last` 复制最后回复，`/copy id` 复制会话 ID。`/export [路径.md|路径.json] [--no-thinking] [--no-tools]` 导出当前会话；省略路径时生成临时 Markdown 文件并显示完整路径，已有文件不会被覆盖。JSON 为完整本地记录，思考/工具隐藏选项仅作用于 Markdown。

插件使用、SDK、强类型工具、模型和命令扩展、Hooks、后台任务服务、OAuth 及打包示例见 [中文插件开发文档](docs/plugins.zh-CN.md)。示例和 SDK 的本地 NuGet 包在 `artifacts/packages/`；本次没有向公共 NuGet 发布。后台任务在当前应用进程内运行，退出时取消并保存结果；子 Agent 共用工作区，待授权/回答通过任务面板处理。

```text
User → LLM → execute_code(Python) → call_tool(...) → typed C# tools → system
```

## Start

Requires a .NET 8 runtime (SDK 9+ to build the `.slnx` solution), x64 Windows, hardware virtualization, and Windows Hypervisor Platform. Python 3.10–3.14 and **PowerShell 7.6.x** are checked before activating the agent. Host Python only parses the code; model code executes exclusively in Hyperlight's bundled Python guest.

```powershell
dotnet run -c Release --project src/LanAgent.Terminal -- doctor
dotnet run -c Release --project src/LanAgent.Terminal -- providers
dotnet run -c Release --project src/LanAgent.Terminal -- chat --workspace C:\Projects\MyProject
```

`doctor --install` asks before installing missing Python or PowerShell through winget, then rechecks both. Interactive chat offers the same setup. Failed checks or missing microVM support block agent execution; there is no host-Python fallback. WHP/BIOS changes are not made automatically.

Configure OpenAI-compatible, Anthropic, Gemini or Ollama endpoints through `config`, or use environment variables without storing the key:

```powershell
$env:LANAGENT_PROVIDER = 'openai'
$env:LANAGENT_BASE_URL = 'https://api.openai.com/v1'
$env:LANAGENT_MODEL = '<your-model-id>'
$env:LANAGENT_API_KEY = '<your-api-key>'
dotnet run --project src/LanAgent.Terminal -- run 'List the files in this workspace' --json
```

Interactive commands: `/new`, `/sessions`, `/resume ID`, `/model`, `/history`, `/export path.json`, `/help`, `/exit`. Ctrl+C cancels the current run. `run --json` emits one JSON event per line and meaningful exit codes (0 success, 1 failure/limit, 130 cancellation). Ordinary redirected/noninteractive execution refuses write, shell and MCP approvals; explicit `--full-access` permits them without prompts.

Other commands:

```powershell
dotnet run --project src/LanAgent.Terminal -- tools --json
dotnet run --project src/LanAgent.Terminal -- sessions
dotnet run --project src/LanAgent.Terminal -- sessions export <session-id> --output session.json
dotnet run --project src/LanAgent.Terminal -- run '<task>' --resume <session-id> --max-tools 50 --timeout 300 --code-timeout 30
dotnet run --project src/LanAgent.Terminal -- chat --full-access
```

Use `--config` with a saved configuration ID/name, `--data-dir` for separate storage, and `--python`/`--pwsh` to select absolute installed runtime paths. `--worker`/`--guest` override the trusted packaged sandbox worker and guest. Treat these paths as executable host configuration, never as model-supplied values.

Background commands and agents start without a concurrency limit by default. Set `--background-concurrency N` to opt into a queue with N concurrent jobs; `0` means unlimited. Independent jobs can run together; dependent operations within one agent session remain ordered.

## Tool and isolation policy

手动接法、系统提示、历史兼容与审批边界的中文说明见 [CodeAct 与 Hyperlight](docs/codeact.zh-CN.md)。

Python 代码块、绿色工具调用、鼠标执行详情、`/permissions full` 完全访问和持续执行说明见 [执行过程与权限模式](docs/execution.zh-CN.md)。最大轮次已删除，任务默认不限制总时长。

- The LLM receives **only `execute_code`**. `CodeActProvider` supplies descriptions and generated schemas for hidden C# tools.
- Before the first model request, the engine prepares the actual Hyperlight SDK guidance with `BuildInstructions(toolsVisibleToModel: false)`. Legacy direct/native tool history is projected onto CodeAct without rewriting audit records. Direct model calls are rejected as unexecuted, with one correction attempt before stopping.
- Project instructions in `AGENTS.md` and `.lanagent/AGENT.md` each support up to 100,000 characters and are loaded in full. `/memory set` uses the same limit.
- A trusted host parser validates Python's AST. Literals, assignments, loops, conditions, comprehensions, selected pure builtins/methods, `print`, literal-name `call_tool` calls and custom helper functions pass. Function bodies retain the same checks; imports, eval/exec, reflection/private attributes, decorators, reserved-name definitions, class definitions and direct file/network I/O are rejected. The model prompt still instructs the agent to avoid custom functions/classes.
- `Microsoft.Agents.AI.Hyperlight` owns the Python microVM and clean warm snapshot/restore before each execution. No host mounts or outbound domains are configured. Guest stdout is bounded before IPC.
- The dedicated worker has a deadline and can be killed with its process tree on cancellation. The worker inherits a small runtime environment, without provider credentials. Host tool calls use the same cancellation token.
- New model configurations default to a 16,384-token answer allowance. The cap remains adjustable in model settings; a particular provider/model may advertise a smaller hard limit.
- Ordinary file tools restrict paths to the selected workspace and reject traversal, UNC paths and reparse points. Explicit full-access mode permits external host paths; Windows device/ambiguous names and alternate streams remain invalid. Reads/listings paginate, writes avoid silent overwrites, and edits require one exact match. Concurrent external filesystem changes can still cause TOCTOU races.
- Ordinary writes, Shell and external MCP invocations require **per-operation approval** in the C# registry. Refusal remains a structured value inside Python. An embedding host without an approver is read-only unless it explicitly sets `AgentOptions.FullAccess = true`.
- `run_shell(exe, args[])` is supplementary. Trusted aliases run without prompting; any other installed executable found on `PATH` or supplied by absolute path is shown with its resolved path and literal arguments for one-time approval. `--allow-shell alias=absolute-path` adds a trusted alias. A constant PowerShell 7.6 launcher passes arguments as data, with separate stdout/stderr draining, a 1–120 second deadline and a 20000-character cap per stream. An approved CLI still runs with the host user's authority; this is command approval, not an OS sandbox.
- Built-in returns have `ok`, `data`, `error`, `truncated`. Text output is capped at 20000 characters per stream and aggregate tool results at 64000 serialized characters. Truncated results remain marked and file reads can page with `offset` / `limit`. Audit JSONL records tool identity, argument SHA-256 and completion status, without raw arguments/content. Guest reset and cancellation **do not roll back earlier host-side writes**.
- Default trusted aliases are installed `git`, `rg`, `dotnet`, `node`, `npm`, `npx`, `pnpm`, `yarn` (PowerShell launchers for Windows package managers). The allowlist controls which executable names run without a prompt; it does not block other installed commands. Executable, argument and working-directory preflight runs before approval. Add trusted tools with `--allow-shell alias=C:\absolute\tool.exe`. Ordinary mode asks once for other executables; explicit full-access mode skips approval and permits external host paths and installed executables.
- Enabled MCP servers are connected by the host and their tools registered under `mcp_<server-hash>_<name>`. External tools stay hidden behind CodeAct and retain per-call approval. Configuring an MCP server grants its executable host access; guest isolation does not isolate that server process.

Example model code:

```python
r = call_tool("list_directory", path=".", limit=20)
if r["ok"]:
    print([entry["name"] for entry in r["data"]["entries"]])
else:
    print(r["error"])
```

## Embed as a library

`LanAgent.Core` remains a class library. Providers, persistence, tools, CodeAct and engine are UI independent. A host ships the `sandbox` folder built/published with the terminal/desktop, or supplies `SandboxWorkerPath` explicitly.

```csharp
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Runtime;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;

var options = new AgentOptions
{
    WorkspaceRoot = @"C:\Projects\MyProject",
    // Optional: approve writes/executions using your application's own UI.
    // Approver = async (request, ct) => await MyApprovalUI(request, ct)
};
await using var runtime = await AgentRuntime.CreateAsync(options);
var probe = await runtime.ProbeSandboxAsync();
if (!probe.Ok) throw new InvalidOperationException(probe.Error);
using var store = new DataStore();
var session = new Conversation();
store.AddConversation(session);
var config = new ApiConfig
{
    ProviderId = "openai", BaseUrl = "https://api.openai.com/v1",
    ApiKey = Environment.GetEnvironmentVariable("LANAGENT_API_KEY") ?? "",
    DefaultModel = Environment.GetEnvironmentVariable("LANAGENT_MODEL") ?? ""
};
var engine = new AgentEngine(store, new AiProviderFactory().GetProvider(config), runtime.CodeAct, options);
await foreach (var evt in engine.RunAsync(session.Id, "List files", config))
    Console.WriteLine(evt);
```

To add tools, reference `LanAgent.ToolGenerator` as an analyzer in the host's project. Write a public partial class with public instance methods returning `Task<ToolResult>`. Annotate each method with `[AgentTool("name", Risk = ToolRisk.Write)]` and `[Description(...)]`; annotate each model parameter with `[Description(...)]`. Supported parameter types: `string`, `int`, `bool`, `string[]`; `CancellationToken` is host-injected. The generator emits JSON schemas and strongly typed dispatch implementing `IAgentToolSet`; unsupported contracts fail compilation (`LA001`). Register with `runtime.Tools.Register(new MyTools(...))` before the first sandbox execution. Keep tool sets fixed during a run. Between turns, use Tools.Replace<T> to replace a provider-owned set; schema changes rebuild the worker on the next execution.

`AgentOptions` controls run/code deadlines, iteration/tool/context budgets, workspace, allowlist and approval callbacks. `InstallationApprover` optionally allows embedding hosts to ask about winget installs. `AgentEngine` streams progress, text, thinking, host-tool events and terminal reasons. It saves tool pairs even on interruption, repairs older incomplete histories, prunes complete old turns and prevents concurrent runs on one engine. `AgentService` retains the old desktop streaming API and protects conversations against concurrent runs within that service.

## Compatibility and validation

Data remains in `%APPDATA%\LanAgent\lanagent.json` with its backup. Use `--data-dir` for independent instances; the JSON store is not a multi-process database. Existing imported shell templates/`shell_exec` stay in saved data for reference but **cannot execute**. Migrate them to typed host tools or the approved `run_shell` contract. Desktop uses native approval dialogs and packages the same worker.

```powershell
dotnet build LanAgent.slnx -p:SkipWebBuild=true
dotnet test tests/LanAgent.Tests
dotnet publish src/LanAgent.Terminal -c Release -r win-x64 --self-contained true -o artifacts/native-search-win-x64
```

Keep the published `sandbox` subtree intact. Hyperlight and the Microsoft connector are pinned preview dependencies; upgrading requires repeating actual-VM tests. Tests cover schema generation, Python restrictions, file boundaries/approval, Shell injection/timeout/output limits, agent history/cancellation/budgets, and actual Windows Hyperlight callbacks/snapshot reset/restart.

Official integration documentation: [Microsoft Hyperlight CodeAct](https://learn.microsoft.com/en-us/agent-framework/integrations/hyperlight), [Hyperlight .NET SDK](https://github.com/hyperlight-dev/hyperlight-sandbox/blob/main/src/sdk/dotnet/README.md).

## License

LanAgent is licensed under GPL-3.0-only. See [LICENSE](LICENSE). Third-party notices are preserved in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
