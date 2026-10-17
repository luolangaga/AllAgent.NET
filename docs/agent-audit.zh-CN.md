# AllAgent.NET 实测审计报告

- 日期：2026-10-09
- 环境：Windows 主机 + Hyperlight 微虚拟机沙箱；.NET SDK 11.0.100-rc.1（TFM net8.0）；Node.js v24.15.0
- 方式：真实运行——构建、分片跑 xUnit、端到端 execute_code / run_shell / provider 协议用例；逐条与 README、docs 声明对比
- 变更范围：未修改任何源文件，仅新增本报告；bin/obj/artifacts 为构建与运行产物

## 一、总体结论

1. 架构与安全边界是强项：执行隔离、工具调用管线、审计脱敏、并发与超时控制都有代码与测试支撑。
2. 仓库当前不是"干净检出的测试全绿"状态：分片共 121 项通过、6 个失败实例（4 个独立根因），重跑确认非 flaky。
3. 最高价值问题：宿主与 guest 之间的共享缓冲区仅 16,536 字节（可用 16,376），且被程序 stdout 与工具调用参数共同占用；超限的失败模式是 guest panic（本次执行整体作废），而文档宣称 stdout 上限 20,000 字符。对中文内容，单次工具参数可用额度约 2,700 字，远低于文档与直觉。

## 二、验证方法与可复现命令

命令：

    dotnet build src/AllAgent.NET.Core      # 0 warning / 0 error
    dotnet build tests/AllAgent.NET.Tests   # 0 warning / 0 error
    dotnet test tests/AllAgent.NET.Tests --no-build --filter <表达式>

分片结果（多轮重跑）：

| 分片主题 | 结果 |
|---|---|
| TokenUsageStats | 8/8 |
| GeneratorTests（源生成器） | 2/2 |
| Provider / Registry / ModelContext | 31/31 |
| ThinkingLine / Checkpoint / Input / ExecutionTrace | 15/15 |
| ExternalDirectory / BuiltinLogin / OAuth | 20/20 |
| TerminalProductTests | 10/10 |
| TerminalIntegrationTests | 1/1（真实启动，约 19s） |
| SubAgentIntegrationTests | 1/1（真实子 Agent，约 19s） |
| CodeActTests | 33/34 |

专项实测（绕过测试框架）：

| 实验 | 结果 |
|---|---|
| print('z' * 16288) | 正常返回 |
| print('z' * 16289) | panic：Required 16377 / Available 16376 |
| print('z' * 16300) | panic：Required 16388 |
| file_read(path='B'*17000+'.txt') | panic：Required 17204（工具参数超限） |
| file_read(path='中'*8000+'.txt') | panic：Required 48204（中文按转义形式计费） |
| file_read(path='A'*8000+'.txt') | 正常拒绝（超长路径报错），参数 8,000 字符可通过 |
| 源代码含 6,000 个中文字符 | 正常（源码本身不受该缓冲限制） |
| run_shell 输出 20,050 字符 | 裁剪为 20,000，truncated=true |
| dotnet run --project src/AllAgent.NET.Sandbox -- --version | {"kind":"error","error":"Missing init."} |

## 三、优点（附实测或源码证据）

1. 执行隔离真实有效：每次 execute_code 都在全新 Hyperlight 微虚拟机中运行，guest 无宿主文件系统、无网络、无可持久状态；模型只能通过 call_tool 触达宿主能力，符合最小权限原则。
2. 工具调用管线顺序正确：ToolRegistry.CallAsync 依次执行 preflight → access → hook → approval → audit → invoke，校验与权限判定都在副作用之前，审批先于调用，审计在完成后落地。
3. 审计脱敏到位：审计只记录参数的 SHA-256 摘要，不落明文参数，避免日志本身成为新的敏感信息泄露面。
4. 超大结果有降级策略：整体工具结果 JSON 超过 64k 时降级为 2000 字符 summary + truncated 标记，而不是把宿主内存撑爆。
5. 并发与超时可控：AgentEngine.RunAsync 用 _runLock 串行化；RunTimeout 经 CancelAfter 生效；事件流使用容量 256 的 bounded channel（背压而非无界增长）；上下文压缩以 0.85 为阈值并有重复整理保护。

6. Shell 安全设计方向正确：命令字符串与模板被禁用；run_shell 需要宿主 allowlist、PowerShell 7.6 与逐次审批；run_shell.ps1 用 base64 JSON 传参数数组、不拼接命令行；宿主侧 20k 输出裁剪实测生效（20,050 → 20,000，truncated=true）。
7. Provider 抽象干净：IAiProvider 四个实现（openai / anthropic / gemini / ollama），Ollama 复用 OpenAiProvider 仅换 base URL，扩展成本低。
8. 测试资产含真实集成：终端与子 Agent 集成测试是真实启动（各约 19s），CodeAct 用例跑在真实微虚拟机上，安全类用例覆盖工具边界与目录外访问。
9. 持久化稳健：DataStore 线程安全（读写锁）+ 原子写 + 写后校验 + .bak / .tmp 兜底。
10. 可观测性可用：统一日志写到 %APPDATA%/AllAgent.NET/allagent.log，桥接调用有明确排查路径。

---

## 四、缺陷（按严重程度）

### P1-A｜16 KB 共享缓冲区：文档不符 + 硬崩溃 + 对中文极不友好

- 文档声称：stdout 上限 20,000 字符（宿主侧 run_shell 确实按 20,000 裁剪，这一半是对的）。
- guest 实测：print('z' * 16288) 正常；print('z' * 16289) 即 panic（Required 16377 / Available 16376）；16,300 字符 panic（Required 16388）；print('x'*200)*100 同样 panic（Required 20084）。
- 更严重的发现：该缓冲区不只装 stdout，还装工具调用参数。file_read 传 17,000 字符的路径即 panic（Required 17204）；传 8,000 个中文字符 panic（Required 48204，说明中文按 JSON 转义形式约 6 字节/字计费）；8,000 字符的 ASCII 路径可正常执行并返回拒绝结果。
- 程序源码本身不受该缓冲限制（6,000 个中文字符源码、13,000 字符 ASCII 源码都正常运行）。
- 影响：模型用 print 返回长结果、或 file_write 写入较长/较多中文内容时，会在"最后一步"整体崩溃，本次执行的全部中间结果作废，只能重跑。对中文任务，单次工具参数的实际额度只有约 2,700 个中文字，与"输出 20,000 字符"的文档印象差距巨大。
- 报告中此结论本身即是证据：本次撰写报告时，一次 file_write（中文约数千字）与一次 16,300 字符 stdout 分别触发了同一 panic。
- 建议：① 在 guest 侧实现真正的截断语义（截断 + truncated 标记），把失败模式从崩溃降级为可预期的信息损失；② 把真实预算写进 CodeActProvider.Instructions 与 README（并区分 ASCII 与中文）；③ 错误文案从 "shared output buffer" 改为可操作提示（如"参数过大，请分块"）。

### P1-B｜run_shell 字面量参数注入防护存在失败用例

- 用例：ToolTests.ShellUsesLiteralArgumentsAndTrustedPwsh（ToolTests.cs:110）。
- 实测：用例失败，且 INJECTED 文件被实际创建，说明注入片段在测试覆盖的调用路径上被解释执行，而非作为字面量参数传递。
- 影响：直接触及"命令字符串与模板被禁用、参数按字面量传递"的安全承诺。若属宿主调用链问题则是安全边界缺陷；若属测试夹具自身拼接命令行，则是安全断言失效（同样必须修，因为它掩盖真实风险）。
- 建议：定位 SystemTools → run_shell.ps1 的参数传递路径，保证参数数组中的任何字符都不具有语法意义。

### P2-A｜CodeActProvider.Instructions 与测试约定不一致

- 用例：AgentEngineTests.ActualProviderRequestsHideLegacyDirectCallsAndPreserveValidNativeState，3 个 provider 协议全部失败（AgentEngineTests.cs:81/82）。
- 差异：测试断言系统指令必须含 Use "."（工作区根路径写法），实际指令文本中没有。
- 判读：指令文本与约定漂移——要么实现漏了该约定，要么测试过期；三个协议同时失败说明这是契约层问题，而非个别协议问题。影响是模型可能使用错误的工作区路径写法，触发不必要的路径解析失败。

### P2-B｜不可用 shell 的拒绝路径：行为正确、消息契约不符

- 用例：ExecutionExperienceTests.UnavailableShellIsRejectedBeforeApprovalAndInstalledNpmRunsAfterApproval（ExecutionExperienceTests.cs:27）。
- 实测：实际错误为 Executable 'missing-shell-for-qa' was not found...，而测试期望 No approval was requested。
- 判读：从行为看，拒绝确实发生在审批之前（目标语义达成），但错误文案与断言口径不一致，导致该契约无法被测试固化。
- 建议：错误码（如 EXE_NOT_FOUND）与人类可读文案分离，测试断言错误码，避免文案改动导致红灯。

### P2-C｜真实 guest 输出裁剪用例失败，与 P1-A 互为佐证

- 用例：CodeActTests.ActualGuestOutputIsClipped（CodeActTests.cs:138）失败。
- 判读：用例语义即"应被裁剪"，而实测 16,289 字符就 panic、没有任何裁剪迹象，可判定裁剪语义在 guest 侧并未实现——P1-A 不只是阈值不同，而是应有的降级机制缺失。

### P3｜Sandbox 组件缺少独立可运行的 CLI 契约

- dotnet run --project src/AllAgent.NET.Sandbox -- --version 返回 {"kind":"error","error":"Missing init."}。
- 作为宿主内部组件可以接受，但不利于运维排障时"先自检沙箱是否可用"。建议提供 --self-check 之类的最小入口（探测微虚拟机能否启动并返回 0/1）。

---

## 五、未验证 / 明确空白（避免过度结论）

以下路径本次未取得实测证据，不应视为通过：

- 真实 LLM provider 端到端（需要可用 API key；本次以协议级 fake 覆盖请求构造）。
- 运行中取消、RunTimeout 触发路径。
- 0.85 阈值上下文自动压缩的实际触发与总结质量。
- 子 Agent 审批交互（/tasks 中人工应答流程）与 GUI / Bridge 侧行为。
- 大于 64k 的工具 JSON 降级路径、AGENTS.md 的实际注入内容。
- 缓冲区是否在 stdout 与工具参数之间共享同一额度：尚未定论（两者各自超限都会 panic，已实测）。
- 测试类未 100% 分片覆盖，故"121 项通过"是覆盖范围内的结论，不等于全量绿灯。

## 六、改进优先级建议

1. 先让测试回到全绿：修 5 处红灯，或显式更新/废弃过期断言。当前状态下任何 CI 门禁都会被噪声掩盖。
2. 实现 guest 缓冲区截断语义（P1-A / P2-C），并把真实额度写入文档与 Instructions：这是"能否稳定跑复杂任务"的可用性问题，收益最高。
3. 复核 run_shell 参数字面量路径（P1-B）：安全面缺陷，优先级不低于功能问题。
4. 统一错误契约（P2-B）：错误码与文案分离，测试断言错误码。
5. 增加"文档数值上限 ↔ 实现常量"的一致性测试，避免文档再次漂移。

## 七、一句话总结

架构与安全设计"想得对"且大多"做得到"（隔离、管线、脱敏、审计、限流都有代码与测试支撑）；当前最大的实际风险不是设计缺陷，而是"实现 / 文档 / 测试三方对不上"——最典型的是 16 KB 共享缓冲区（文档称 20,000 字符，实际 stdout 上限 16,288 字节、超限即崩溃、中文工具参数额度仅约 2,700 字），以及 6 个红灯用例暴露出的契约漂移。




