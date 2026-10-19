# CodeAct 与交付验收检查（2026-10-13）

## 官方实现对照

参考微软 [CodeAct 文档](https://learn.microsoft.com/en-us/agent-framework/agents/code-act) 与 [.NET 设计](https://github.com/microsoft/agent-framework/blob/main/docs/features/code_act/dotnet-implementation.md)。这些是框架文档及设计，不是交付质量认证。

AllAgent.NET 的普通和 full-access 模式都通过 `HyperlightProcessSandbox` 启动 worker。worker 实际使用微软 `HyperlightExecuteCodeFunction`，取 SDK 的 `BuildInstructions`，使用干净快照，每次执行不保留 Python 变量。模型只看到 `execute_code`，宿主工具通过 provider 的工具目录和 `call_tool` 调用。这些与官方核心结构一致。

差异包括：自定义 AST 限制、自定义 IPC 分片、`ok/data/error` 返回包装及逐次宿主工具审批。微软 .NET 设计描述的审批方式是基于工具目录为整个 `execute_code` 决定审批，AllAgent.NET 的 ask 模式在宿主调用时分别审批。因此不能宣称完整实现了微软公共 API 或全部设计要求。

full-access 只改变宿主工具的审批和路径权限，不能取消 microVM；缺少 PowerShell 或可运行的 Python guest/worker 必须报错。宿主 Python 供共享 pip 库使用，检查可执行性和 3.10–3.14 版本范围；缺少或版本不符时提示通过 winget 安装 Python 3.13。Python 与 PowerShell 7.6 安装成功后要求重启软件。默认限制模式（工作区内免审、工作区外授权），`--ask` 逐次授权，plan 保留只读约束。

## 提示词参考和本次修改

参考了 Aider 的 [编辑提示词](https://github.com/Aider-AI/aider/blob/main/aider/coders/editblock_prompts.py) 中遵循现有代码约定的要求，以及 [运行指导](https://github.com/Aider-AI/aider/blob/main/aider/coders/shell.py) 中修改后打开页面或运行 CLI 查看效果的做法。没有逐字复制；Aider 的编辑协议和人工执行流程不适合直接移植到 CodeAct。尝试读取 OpenCode 旧提示词路径返回 404，未将其冒充为已核实来源。

新增共用验收指导：逐项落实用户需求、按启动说明验证真实流程、检查真实接口类型与参数、检查实际控件使用、关注旧生成配置遮蔽、使用能识别错误行为的断言，并如实说明无法执行的验收。

新增 `data.hostCallSummary`，包含宿主调用总数、失败数和最多 20 个失败工具名称。即使 Python 没打印失败结果，模型也能看到失败摘要。`ok` 保留 Python 程序完成语义；被程序处理的工具失败不会自动改成程序失败。摘要不判断失败是否已经恢复，也不能检测模型自行隐藏的子进程退出码。

## 验收边界

这次改善提示词与执行证据，不构成宿主强制的全栈验收门禁。模型仍可能违反指导。不能只根据回归测试声称实际模型交付质量提升。应以相同任务、模型、预算和环境重复比较，记录真实启动、接口、浏览器交互、需求完成度及失败后恢复结果，再评估收益。

## 后续：参考 Pi 与 Gemini CLI 重写系统提示词

2026-10-13 直接读取了以下上游源码；分支链接会随上游更新，本记录描述本次读到的实现：

| 项目及源码 | 已核实的做法 | AllAgent.NET 的应用 |
| --- | --- | --- |
| [Pi system-prompt.ts](https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/src/core/system-prompt.ts) | 分段组装提示词；规则按可用工具贡献；去重；项目指令、技能、工作目录分别组织 | 将通用指导与工具指导拆开，只为实际注册的文件、Shell、研究、委派能力添加对应段落 |
| [Pi read.ts](https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/src/core/tools/read.ts) 和 [edit.ts](https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/src/core/tools/edit.ts) | 文件工具自带简短行为指导；精确修改、匹配唯一、分页读取 | file_edit 使用 Pi 风格的 edits 数组批量协议（条目为 old_string/new_string）；基于同一原文件匹配、拒绝歧义和重叠、保留 BOM 与换行、同文件修改排队 |
| [Gemini CLI promptProvider.ts](https://github.com/google-gemini/gemini-cli/blob/main/packages/core/src/prompts/promptProvider.ts) 和 [snippets.ts](https://github.com/google-gemini/gemini-cli/blob/main/packages/core/src/prompts/snippets.ts) | 按模式、实际工具和上下文组装段落；强调工程约定、诊断、验证和上下文效率 | 明确理解→实现→验证→交付流程；按工具提供能力指导；区分权限、运行隔离和交付证据 |

这里借鉴组织方式和行为原则，提示词正文为 AllAgent.NET 原创改写，没有移植项目身份、工具名或专用编辑协议。没有采纳上游无条件要求加测试、为所有不明确请求停止实施等规则；继续遵循用户授权、项目要求和任务规模。

具体变化：正文移至 `CodeActInstructions.cs`；`CodeActProvider` 负责根据当前工具目录组装提示词，稳定规则在前，工具目录和环境在后；合并原先重复的验收段落；移除过时的宿主 Python 运行表述；突出 `ok`、后台任务状态和实际完成之间的区别。源码限制和微软 SDK 的真实指导继续保留。

OpenCode、OpenHands 旧路径再次返回 404，GitHub 树 API 遇到限流；未引用这些未读取的内容。没有进行模型质量 A/B 实验，集成测试仅验证新提示词在真实调用链中可用以及协议、权限行为不回归。
