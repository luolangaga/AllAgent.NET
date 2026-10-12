# CodeAct 与 Hyperlight 接入说明

终端默认展示实际 Python 代码，工具调用标绿，点击或 Ctrl+E 查看运行入参和结果。权限模式和持续执行的中文用户说明见 [执行过程与权限模式](execution.zh-CN.md)。最大轮次已删除；普通模式逐次授权，显式完全访问模式不逐次审核。

本实现采用微软支持的手动接法：在独立工作进程中构造 `HyperlightExecuteCodeFunction`，C# 主机使用自己的流式引擎连接各模型协议。它不是 `HyperlightCodeActProvider` 的子类，也没有把系统函数直接注册给模型。

依据：[CodeAct 概述](https://learn.microsoft.com/en-us/agent-framework/agents/code-act?pivots=programming-language-csharp)、[Hyperlight 的 C# 手动接入说明](https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/context-providers/hyperlight)。

## 实际调用链

```text
用户输入
  → AgentEngine 准备本轮 CodeAct 上下文
  → 模型：system 包含 CodeAct 指令；tools 只有 execute_code
  → execute_code({"code":"Python 源码"})
  → 主机只解析 AST，不执行模型 Python
  → Hyperlight 还原干净快照并运行代码
  → call_tool("名称", 参数=值)
  → C# ToolRegistry 校验 Schema、权限、审批、Hooks、审计
  → C# 系统 / Skill / MCP / 插件工具
  → Python 得到 ok/data/error/truncated 字典
  → print(...) 返回必要信息
  → 模型据结果继续或回答
```

## 指令与工具分离

沙箱启动时调用实际 SDK 的 `execute.BuildInstructions(toolsVisibleToModel: false)`，将指令和 `execute.Description` 通过 IPC 返回。引擎在发送第一条模型请求前执行 `CodeActProvider.PrepareAsync()`，获取当前工具集合对应的 SDK 上下文。沙箱启动失败就停止本轮，不联系模型，也不回退为主机 Python / Shell。

SDK 的 C# 描述主要列出函数名与说明；LanAgent 在系统消息中另外加入源生成器生成的完整参数 Schema、返回结构、Python 示例和工作区约定。工具目录是供 `call_tool` 查阅的数据，不是模型 `tools` 列表。插件、Skills、MCP 注册改变后，工作进程按工具集合指纹重建，下一轮重新准备上下文。

模型必须调用：

```json
{"code":"r = call_tool('list_directory', path='.')\nif r['ok']:\n    print(r['data'])\nelse:\n    print(r['error'])"}
```

这里 `.` 表示 C# 主机批准的 Windows 工作区。`src` 表示其子目录。空路径、`/`、`/input` 或猜测的磁盘根目录不能用来替代工作区根目录。本实现没有配置 guest 文件挂载或网络白名单。

所有调用使用明确的关键字参数，如 `call_tool('file_read', path='README.md')`。模型代码禁止 `**kwargs` 解包、动态函数名、import、eval、exec、open、反射和直接文件/网络访问。这些是 LanAgent 的额外限制，不是通用 Python CodeAct 的语言规范。纯逻辑、安全内置函数、循环和条件仍可用于合并多个操作。必须 `print(...)`，单独写最后一个表达式不会自动返回值。

## 错误、历史与界面

Hyperlight 原生桥会将顶层 `error` 结果提升为 Python 异常。LanAgent 的 C# 回调在 IPC 内使用 `result` 包装，可信 Python 前置代码解包，再把原有 `ok/data/error/truncated` 字典交给模型代码。因此读取不存在的文件、拒绝授权、路径校验失败时，Python 可以正常检查 `ok` 并处理错误，不会因为这类业务失败中断整个代码块。模型不能访问前置代码的私有名称。

真正的 Python 语法或运行错误仍导致本次执行失败，保留 stderr 与错误原因。新配置默认给模型 16,384 个回答 Token；Python stdout、Shell stdout/stderr 和单个工具文本结果最多 20,000 字符，结构化工具结果最多 64,000 字符，超出会设置截断标记。文件读取可通过 offset/limit 分页。每次执行还原快照，变量和前置代码状态不会跨执行保留。

Worker 通过仅可信前置代码可调用的私有分块接口传输工具结果和 stdout，避免原生 SDK 的 16 KiB 共享输出缓冲区截断长网页和中文内容。该接口不进入模型工具说明，普通工具仍通过 C# 注册器审批和审计。当前 SDK 对极长代码及单次工具输入的原生缓冲限制仍存在，应拆分长输入；输出分块不扩大执行权限。

恢复旧会话时，只向模型投影合法的 `execute_code` 调用与配对结果；旧的直接调用、坏参数 JSON 和不匹配的原生协议状态降为普通历史数据。原始持久记录保留用于审计。正常、匹配的 Anthropic / Gemini 原生状态原样保留，包括思考签名。

模型仍可能违反协议，不能仅靠提示词保证服从。引擎不会执行直接调用，也不会显示为正在执行的系统操作：界面明确标注“未执行”。允许一次纠正；再次违反就以 `protocol_error` 停止，避免反复无效调用。Python / C# 执行失败显示红色。模型提供的思考内容只在一行内滚动，右侧 `(N Line)` 统计本轮思考的逻辑行数。

## 授权边界

微软默认的审批集成围绕整个 `execute_code`；普通 Agent 的逐函数中间件不会自动覆盖内部 `call_tool`。LanAgent 的逐操作审批是**自行实现的 C# ToolRegistry 策略**，对写入、Shell 和 MCP 操作执行主机策略检查，不依赖 SDK 的嵌套中间件。内置文件工具和 Shell 工作目录在解析外部路径后先请求 `external_directory`，可以仅允许当前操作或记住本会话目录；随后按工具风险和 Shell 信任规则决定直接放行、请求一次确认或拒绝。没有回调时拒绝未信任的执行操作，拒绝返回结构化失败。目录授权不跨会话共享，不落盘。本实现没有宣称支持 FIDES。

工具始终在主机进程执行，microVM 不会替 C# 工具约束文件、网络或凭据。工作区策略、可信 Shell 程序、参数数组、超时、审计均由 C# 层实施。白名单程序免逐次提示；其他已安装程序可以在核对真实路径及参数后按次确认。还原 guest 快照不会撤销已经批准并完成的主机写入。

## 验证

回归覆盖三个协议的实际 HTTP 请求、旧直接调用与原生历史处理、有效思考签名、违规调用停止、沙箱不可用时不联系模型、真实 microVM 的失败结果处理、拒绝写入无副作用、连续主机回调、快照还原、工具集合变更、超时和取消。终端组件另验证窄屏单行、中文/emoji/组合字符滚动与失败颜色。

```powershell
dotnet test tests/LanAgent.Tests -c CodeActValidation
```

`CodeActValidation` 是本次验证使用的独立构建目录，避免正在运行的旧 Release 终端锁住 DLL。实际交付应保留整个发布目录及其 `sandbox/`，关闭旧版后启动新 exe。
