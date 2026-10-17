# 终端版本缺陷修复记录（2026-10-17）

按用户最新要求，本轮只处理 Terminal、终端使用的 Core/ToolGenerator 以及对应测试。原审计中桌面桥接和 React 状态管理的问题不在本轮修复范围。

## 已实施的业务修复

| 原审计项 | 终端修复 |
|---|---|
| B01 旧数据兼容 | 新项目库不存在时复用 `.lanagent/data`；共享目录支持 `AllAgent`/`LanAgent` 回退；同目录支持旧 `lanagent.json` 主备清单。首次正常提交写新清单，保留旧清单及其引用的不可变会话文件。新旧目录都有数据时不自动合并，新目录优先。路径在单次启动内保持稳定。 |
| B06 插件版本校验 | 从宿主真实 SDK 信息版本读取 NuGet 版本，验证插件上下界，去掉固定的 1.0.0。 |
| B07 Responses 重试 | StreamChunk 保留结构化 HTTP 状态码；Engine 用状态码识别 429/5xx，400/401 不自动重试。 |
| B10 中断回答保存 | 网络断流、取消和异常 finish reason 下保存带 `IsInterrupted` 的部分文本/思考；不保存可执行工具调用和未验证的原生状态。重放、导出及上下文整理能识别中断，保存内容遵守输出预算。 |
| B12 生成器冲突 | 生成文件名使用完整类型名，支持不同命名空间的同名工具类；同时修复全局命名空间的生成语法。 |

兼容处理还保留了旧项目索引、项目指令和项目技能的发现路径；新指令/同名新技能优先。明确指定 `--data-dir` 时尊重指定目录。旧数据库损坏时报告错误，避免静默变成空库；当前数据库损坏时也不会偷偷切换到旧快照。

## 终端交互与诊断修复

- 表格拖选按实际终端单元格锁定起始列，不再把相邻 ID/模型列一起复制。
- 工具标题体现成功/失败状态；电脑操作和子代理任务的输出有明确标识。
- MCP 连接/初始化超时统一生成可识别的超时诊断，失败连接不会留下工具。
- 单次启动的共享数据路径传入 AgentOptions，环境检查和实际运行使用同一目录。
- 插件扩展和 Hook 的同步回调超时后仍在执行时，不提前释放其 CancellationTokenSource；等回调结束再释放并观察迟到异常，避免延迟调度导致 ObjectDisposedException 或丢失取消信号。两类回归测试均检查超时后 token 的 WaitHandle 仍然有效。

## 测试修复及新增覆盖

- 终端集成测试使用的 runtimeconfig 补齐测试产物实际依赖的共享框架，避免子进程进入业务前就缺少 Logging.Abstractions。
- 为 ConPTY 输出重建带光标位置的终端屏幕；测试验证编辑后的内容，而不是假设增量刷新会重复输出完整字符串。
- JSON 集成测试区分运行环境通知和有 type 的 Agent 事件，仍逐行检查有效 JSON、真实宿主调用及会话保存。
- 登录安全测试更新为当前 native PKCE 流程，继续验证错误 state、nonce、签名、audience、issuer、有效期、刷新身份、缺少续期凭据和工作区时不能覆盖原凭据；不访问真实账号。
- 更新默认完全访问、显式 `--ask`、显式允许输出后重试、指定 Hyperlight 时缺 worker 必须失败等契约；不放宽执行策略来迁就旧测试。
- Unicode 选区测试使用实际渲染位置；角色编辑测试明确重新进入子菜单，继续验证取消丢弃全部草稿。
- 电脑操作浮层测试直接验证浮层不占前台焦点、不命中鼠标，并保留原生 NoActivate/Transparent 样式校验；不再要求其他应用和人工操作期间全局焦点窗口保持不变。
- 保留 NativeWebSearch 组件的引擎回退覆盖，并增加宿主没有 Browser Use 时不能偷偷退回 HTTP 搜索的测试。
- 生成器契约测试核对当前工具名集合（包含已有的 everything_search）；Shell 超时日志测试给 SDK 启动和输出留出时间，再验证五分钟等待确实被取消及完整日志可恢复。
- 新增隔离回归覆盖旧库/备份/修订恢复、目录优先级与稳定性、旧索引/技能兼容、SDK 上下界、HTTP 重试分类、中断重放和整理、生成器同名类/全局类。

## 验证

第一批核心定向测试：83/83 通过。终端交互定向测试：50/50 通过。

终端相关全量测试及最后一轮增补验证正在执行，完成后在此补记最终结果。全量命令只排除两个明确命名为桌面场景的测试，不调整桌面代码：

```powershell
dotnet test tests/AllAgent.NET.Tests/AllAgent.NET.Tests.csproj --no-restore --filter "FullyQualifiedName!~SubAgentIntegrationTests.DesktopDelegation&FullyQualifiedName!~SubAgentIntegrationTests.CancellingDesktop" --logger "trx;LogFileName=terminal-full.trx" --results-directory artifacts/test-results/terminal-fixes
```

没有用真实模型调用或真实账号登录作为本轮验证依据；原生终端、工具与微虚拟机集成测试使用测试用例自己的临时目录和进程。
