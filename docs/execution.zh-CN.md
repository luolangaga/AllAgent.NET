# Python 执行过程与权限模式

每次 `execute_code` 默认显示带行号的 Python 方框，内容来自实际提交的源码。绿色下划线标识 `call_tool(...)` 调用；字符串和注释中的同名文字不会被误标为工具。

鼠标点击绿色调用打开详情浮窗；不支持鼠标时按 **Ctrl+E** 查看最近执行。浮窗展示已解码的中文入参、结构化结果和状态。滚轮或 ↑↓ 阅读，←→ 切换循环产生的多次实际调用，Esc 或 Enter 关闭。运行期间也可查看，结果返回后更新；关闭后保留草稿和阅读位置。失败状态和错误显示红色，尚未到达的调用显示“尚未执行”。

执行结果随会话保存，恢复后仍可查看。每次 Python 执行的历史详情最多保存 100 次工具调用及 128000 字符；超长入参展示明确的截断预览。详情数据不额外加入模型上下文。

Python 源码与它产生的工具结果位于同一个外框中。下方每个工具使用独立内框，标出执行顺序、命令或文件路径、状态、对应源码行号和输出摘要；失败内框为红色。点击代码或工具内框均可打开详情。小窗口可滚动阅读整个执行组。

CodeAct 指令要求模型先简述程序用途，再用 Python 组织多个步骤、检查 `ok`、处理部分失败、筛选和汇总结果。简单任务可以使用短程序；不会为了复杂而复杂。模型仍只看到 `execute_code`，系统操作经 `call_tool` 转交 C#。

## 选择权限模式

输入 `/permissions` 选择，也可直接使用命令：

| 命令 | 行为 |
|---|---|
| `/permissions ask` | 默认逐次授权；有效的外部路径先询问目录访问，文件写入、未信任的 Shell 程序、MCP 和插件执行操作仍单独审核 |
| `/permissions full` | 完全访问；C# 工具不再逐次审核，可访问工作区外的普通文件并执行已安装程序或指定绝对路径的程序 |
| `/permissions plan` | 只读规划；拒绝写入、Shell 和外部执行操作 |

底栏和 `/status` 显示当前模式。模式作用于当前终端进程，新终端默认逐次授权；切换前需结束或取消后台任务。完全访问也应用于此后创建的后台任务和子 Agent。

## 外部目录与聊天内授权

内置文件工具和 `run_shell` 的工作目录可以使用绝对路径或 `..`。普通模式先解析实际路径，遇到工作区外目录时，在聊天区插入授权气泡，提供“允许这一次 / 拒绝 / 此会话记住目录”。默认选中“允许这一次”，Enter 确认；Y 允许一次，A 记住目录，N/Esc 拒绝。一次授权只覆盖当前工具操作，记住目录覆盖该目录及其子路径，并只对当前会话、当前进程生效。不同会话和子 Agent 不继承目录授权。

`/permissions directories` 查看本会话记住的目录，`/permissions directories clear` 撤销。目录访问授权不会代替写文件或执行程序的授权；完全访问模式免审核，规划模式仍只允许读取。非法设备路径、数据流、歧义路径和普通模式下的链接仍在授权前拒绝。外部文件修改不会纳入工作区文件快照，不能通过 `/undo` 自动恢复。

流程参考 [OpenCode permissions](https://opencode.ai/docs/permissions/) 的 external_directory 与 once/always/reject；目前实现会话目录授权，不包含完整的通配符 allow/ask/deny 规则配置。

## 输出和滚动性能

终端按消息缓存已排版行；滚动或状态刷新复用已有排版，只重画视窗需要的行。窗口宽度变化或执行卡状态更新时，受影响的消息会重新排版。

新建模型配置的回答 Token 上限默认 16,384，可在模型管理中修改；已保存配置继续使用各自的 Token 上限。工具 stdout/stderr 和单个文本结果默认最多 20,000 字符，完整工具 JSON 最多 64,000 字符。超过预算时保留 `truncated` 标记；文件读取可设置 `offset` 与 `limit` 分页。作为参照，OpenCode 配置文档的工具结果预览阈值为 2,000 行或 50 KB，超出后可从落盘结果查看完整内容。

授权、问题、命令菜单、模型选择和填写设置均显示为聊天区内的交互气泡，底部草稿和上下文状态保留。↑↓ 选择、Enter 确认；大段授权预览支持滚轮或 Ctrl+↑↓ 查看，Tab 在紧凑和展开预览之间切换。执行详情检查器仍可以用 Ctrl+E 打开。

独立命令行显式开启：

```powershell
lanagent chat --full-access
lanagent run '检查项目并修复构建' --full-access --json
```

完全访问下，缺少 Python 或 PowerShell 7.6 时不再弹出安装审核；通过 winget 安装并校验后才启动 Agent。Python AST 限制和实际 Hyperlight microVM 仍有效；完整访问权限由 C# 主机工具提供。撤销覆盖工作区内可快照的文件工具修改；外部路径、Shell/MCP 操作标记为外部行为，不承诺自动撤销。

## Shell 与 npm

普通模式自动发现并信任已安装的 `git`、`rg`、`dotnet`、`node`、`npm`、`npx`、`pnpm`、`yarn`。这些程序无需逐次确认。Windows 包管理器优先使用 `.ps1` 启动器，参数通过 PowerShell 7.6 数组传递。

白名单只决定哪些程序可以免确认，不再阻止其他命令。`run_shell` 可以按 PATH 中的名称或绝对路径解析其他已安装程序；主机先验证程序、参数和工作目录，再在聊天气泡中显示真实程序路径、工作目录和逐项参数，用户允许本次后才执行。`pwsh` 若不在信任列表中也会走这条确认流程。找不到的程序会明确报“未安装/无法解析”，不会伪装成白名单拒绝。

其他程序可明确配置：

```powershell
lanagent chat --allow-shell mytool=C:\Tools\mytool.exe
```

检查 npm 版本使用 `exe='npm', args=['--version']`；`['--', 'version']` 是另一组参数。未知程序允许一次只授权当前调用，不会悄悄加入长期信任列表。需要让某程序以后免确认时，显式使用 `--allow-shell`；完全访问模式则跳过所有逐次确认。

Agent 指令以 Microsoft CodeAct 的单一 `execute_code` / 沙箱内 `call_tool(...)` 模式为基础，并参考 OpenCode 的 Agent 指令组织方式和权限分层：可信别名免确认（allow），其他可解析程序逐次询问（ask），规划模式拒绝执行（deny）；无法解析的程序作为环境错误在审批前退出。提示词要求先检查工具结果、只陈述已观察事实、失败后不得编造原因或绕过授权。参考 [Microsoft CodeAct](https://learn.microsoft.com/en-us/agent-framework/agents/code-act?pivots=programming-language-csharp)、[OpenCode permissions](https://opencode.ai/v2/docs/permissions/) 和 [OpenCode agents](https://docs.opencode.ai/docs/agents/)。

## 持续执行

界面底栏固定显示 **上下文 32k / 200k**。提供商返回 usage 时使用其输入与输出 Token 总和（Anthropic 包含缓存输入）；未返回时显示 **≈**，采用字符 / 4 的粗略估算。它表示当前请求上下文占用，不是整个账号的累计消费。

输入 `/context-size` 或打开 F1 的“设置模型上下文容量”，选择 **100k、200k、400k、1M、自定义**。也可直接 `/context-size 200k`、`/context-size 131072`，启动参数为 `--context-window 400k`。配置按提供商和模型 ID 分别保存；自定义支持 1024–10000000 Token。这是主机预算设置，不会扩大服务端模型的实际能力；请按所选模型的真实容量填写。

最大轮次功能和 `--max-iterations` 已删除，界面不显示轮次。默认没有总任务时长或固定工具次数限制；Agent 持续执行到模型完成、用户停止或实际错误。

**Esc / Ctrl+C** 停止任务。需要主动限制时，显式设置 `--timeout 秒` 或 `--max-tools 次数`。`--code-timeout 秒` 仍控制单次 Python 的预算，Shell 单次超时仍为 1–120 秒。

- 模型返回 `length`、`max_tokens`、`MAX_TOKENS` 或 `max_output_tokens` 时，显示“自动继续”并续接任务；截断响应中的所有代码调用均不执行，让模型重新生成较小的完整程序。
- 限流、服务暂不可用、连接提前结束和请求超时自动重试，最多 5 次，等待 2、4、8、16、30 秒；可随时取消。重试仅作用于尚未执行工具的模型请求，不重放已完成的 C# 操作。认证失败等永久错误直接说明原因。
- 历史达到字符预算的 85% 时，自动总结较早内容，保留任务目标、约束、已完成操作、待办和最近完整的调用/结果对。总结请求不提供工具。原始会话和工具详情不删除；摘要及边界持久化，恢复会话后可继续使用。
- 整理后立即再次耗尽、总结被截断、模型空回答或异常结束会明确报错并保留历史，避免无进展地循环。超大单条消息、服务端模型上下文限制仍可能要求缩小输入。字符预算是估算，并非各厂商精确 Token 容量。

处理方式参考 [OpenCode 请求重试](https://github.com/anomalyco/opencode/blob/dev/packages/opencode/src/session/retry.ts)、[OpenCode 会话处理](https://github.com/anomalyco/opencode/blob/dev/packages/opencode/src/session/processor.ts) 与 [Claude Code 上下文管理](https://code.claude.com/docs/en/how-claude-code-works)。LanAgent 使用 C# 实现，并保留 CodeAct/Hyperlight 的执行边界。用量读取遵循 [OpenAI SSE usage](https://developers.openai.com/api/reference/resources/chat/subresources/completions/streaming-events)、[Anthropic 缓存输入计数](https://platform.claude.com/docs/en/build-with-claude/prompt-caching) 和 [Gemini usageMetadata](https://ai.google.dev/gemini-api/docs/tokens)。
