# OpenCode 源码移植验收记录

目标：保留 LanAgent 的 CodeAct / Hyperlight 执行边界，移植 OpenCode 的终端功能和交互。用户明确要求目前不开发桌面端，Vue/Avalonia 不在当前开发范围。此记录不代表已经实现完整功能对等。

## 固定参考

- 上游：https://github.com/anomalyco/opencode
- commit：`e31136bdbdb43e934d8bbd81e75a2065a2625699`
- 本地只读参考：`tools/opencode-reference`，不作为 LanAgent 的构建依赖。
- 审阅日期：2026-10-11。后续核对必须引用此版本，不能混用在线最新版行为。
- 许可证：上游 LICENSE 为 MIT；复制或改编部分随发行保留完整许可和版权声明，见 THIRD-PARTY-NOTICES.md。

## 源码对应与差异

| 范围 | 上游源码（相对参考目录） | LanAgent 现状 / 待办 |
|---|---|---|
| 子代理输入与执行 | packages/core/src/tool/plugin/subagent.ts | 终端新增 subagent：agent/description/prompt、前台默认运行、后台返回、继续空闲子会话、父子归属校验、同一任务内运行中 steering 及收尾续跑；新任务与 steering 支持图片/文本快照、运行中角色和模型切换、实时宿主权限与下一步骤指令；自定义角色与深度待补齐 |
| 子代理最后回复 | packages/core/src/session/subagent-completion.ts | 已增加独立 FinalOutput；完成时向父代理返回最后轮次文本；过程文本仍留在任务记录 |
| 子代理完成通知 | packages/core/src/session/subagent-job.ts | 已实现宿主持久任务通知、按运行 ID 去重、父模型轮次边界消费与空闲终端唤醒；真实终端草稿/唤醒交互验收待补充 |
| 子代理角色 | packages/core/src/plugin/agent.ts | 终端支持 general/explore 与持久自定义角色、说明、指令、只读属性及默认模型/提供商；默认模型应用优先级与实时切换已接入，仍固定一层深度 |
| 命令注册 | packages/core/src/command.ts | 现有 CommandCatalog 和插件命令；待逐项核对动态注册和会话命令分派 |
| 会话交互 | packages/tui/src/routes/session/index.tsx | 已有分支/压缩/撤销；新增 /copy [last|id]、Markdown/JSON 导出及思考/工具开关、/parent 与 /children、排队输入选择/撤回/删除/steer；时间线、历史分支与任务边界多轮撤回已接入，steer 插入点撤回和完整可配置快捷键待补齐 |
| 历史消息操作 | packages/tui/src/routes/session/dialog-timeline.tsx、dialog-message.tsx、dialog-fork.tsx、packages/core/src/session/revert.ts | 用户消息倒序、搜索、真实行定位、复制、图片草稿恢复、历史分支和任务边界跨多轮文件撤回已实现；支持暂存、恢复与新输入提交；运行中时间线与 steer 插入点精确撤回待实现 |
| 输入收件箱 | packages/core/src/session/inbox.ts | 新增持久用户输入，queue/steer 可互换；模型步骤边界只提升全部 steer，空闲边界优先 steer，否则提升一条 queue；提升与消息写入使用同一次原子保存。compaction/move 控制项和 synthetic 通知统一入箱尚待对齐 |
| 子代理界面 | packages/tui/src/routes/session/composer/subagents-tab.tsx | 现有 /tasks；待实现子会话选择和导航，对齐真实状态显示 |
| 权限 | packages/core/src/permission.ts、packages/schema/src/permission.ts、packages/core/src/util/wildcard.ts、packages/tui/src/routes/session/permission.tsx | 角色有序 action/resource/effect 规则、通配符与 allow/deny/ask 已接入；实际工具执行前验证，完全访问不跳过 ask；记忆规则和完整权限生命周期待补齐 |
| 上下文压缩 | packages/core/src/session/compaction.ts | 现有摘要；待对齐自动/溢出/手动三类触发、最近消息保留和厂商原生压缩 |
| 桌面/Web | packages/app、packages/desktop、packages/session-ui | 用户要求目前不开发；不作为当前终端交付范围 |
| 商业发行 | 上游发布脚本、桌面打包、依赖许可 | 待审阅；LanAgent 尚缺签名安装器、升级、回滚与发行验收 |
| 图片输入 | packages/tui/src/component/prompt/local-attachment.ts、clipboard.ts、prompt/attachment.ts | 已接入 @图片路径、四协议真实图片内容块、图片字节持久化、剪贴板截图引用、图片附件信息面板、分批多模态摘要、路径粘贴暂存和草稿移除/撤销；剪贴板真实交互、附件选择列表、文件拖入尚待补齐 |

## 本批验证

`dotnet test tests/LanAgent.Tests -c Release --filter "FullyQualifiedName~SubAgentToolsTests|FullyQualifiedName~BackgroundTaskTests|FullyQualifiedName~ExecutionPresentationTests"`

14 项通过。包括多轮最终回复、持久化重载、没有完成事件时判失败、原有等待授权与取消流程。没有完成事件的异步流不再被当作成功。

第二批：`dotnet test tests/LanAgent.Tests -c Release --filter "FullyQualifiedName~SubAgent|FullyQualifiedName~BackgroundTaskTests|FullyQualifiedName~ExecutionPresentationTests"`，19 项通过（59 秒），含现有真实 Hyperlight 子代理测试。新增测试覆盖角色校验、父子归属、拒绝继续活跃子会话、typed subagent 前后台返回。终端 Explore 宿主策略已实现，但其专门的端到端权限测试仍需补充；本批没有完成全部终端交互验收。

第三批：通知/子会话/工具/后台任务/输入/全屏相关筛选 27 项通过；随后新增父模型结束前消费通知的真实 Hyperlight 测试，SubAgentNotificationTests 共 4 项通过（9 秒）。通知验证包括快任务先完成后注册、持久化重启、不同父会话隔离、消息写入后尚未确认时的重复恢复、前台结果不重复投递。终端 Release 构建通过。ANSI 输入循环已实现完成事件唤醒并保留草稿；非 ANSI TextPrompt 等待中暂不能自动唤醒，结果会在下一次输入或恢复时处理。此限制尚未达到完整交互对等。

第四批：SessionTranscriptTests / TerminalProductTests / FullScreenTests 筛选 27 项通过；真实 ConPTY 导航及导出验收通过，脚本 tools/session_navigation_smoke.py。验收独立数据目录 artifacts/qa/session-navigation/bab6e8de869b4a37930906f37857c390，保存父子终端文本及原始终端输出。确认进入子会话、返回父会话、仅导出当前父会话 Markdown。新增导出测试覆盖内部消息不显示、思考/工具可排除、嵌入反引号不破坏代码块、UTF-8 和拒绝覆盖文件。图片输入已列入下一批。

第五批：图片/提供商/剪贴板按键/正文导出筛选 23 项通过。四个提供商经本地 HTTP 服务验证实际收到原始 PNG Base64；图片引用与 DataStore 重载验证通过。剪贴板截图由仅在人类粘贴动作中运行的固定 Windows STA 辅助程序处理，临时 PNG 尚无自动清理。真实剪贴板截图和付费视觉模型验收未完成。

ImageEngineTests 真实 Hyperlight 测试通过（8 秒）：第一轮图片被保存，第二轮 Agent 请求中仍保留完全相同的图片字节，模型工具列表仍只有 execute_code。

第六批：图片/总结/Token统计 19 项通过；之前包含全屏布局的筛选 29 项通过。新增总结测试确认全部 9 张不同图片分两批传递、第二批携带前批结论、请求无工具；缺少完成事件和意外工具请求会拒绝摘要。本地 HTTP 手动压缩测试确认总结请求发送图片，成功后归档保留原始图片，中断响应时不修改历史。手动压缩复用当前宿主提供商，包含认证和插件适配。真实 ConPTY 验收确认恢复会话显示图片附件名称/格式并保持父子导航和导出可用，证据目录 artifacts/qa/session-navigation/dd8532a1609f45d8a5a0a056dfa0ab6a。实际视觉摘要质量仍依赖模型，尚无付费模型逐一验收。

第七批：图片草稿/协议/全屏/输入选择筛选 43 项通过。路径粘贴支持含空格路径、file://、多条带引号或多行路径；提交前暂存字节，原文件修改后内容仍一致；混合普通文字、缺失或损坏图片不会部分添加。草稿显示数量/名称，Alt+Backspace 移除末张，Ctrl+Z/Y 撤销/恢复。Windows VT 输入现转换无 ConsoleKey 的字符事件并解析 CSI/SS3 快捷键；测试覆盖方向键修饰符、F1/F12、Shift+Tab、Enter、Ctrl+Z、Shift+Insert、两种粘贴标记。真实 ConPTY 图片草稿验收通过，含粘贴不提交、移除/撤销、方向编辑、F1/Esc，证据目录 artifacts/qa/image-draft/99c1e5d29bb24ccebd71d5ec734d3fe6；父子导航和 Markdown 导出回归通过，证据 artifacts/qa/session-navigation/3e182b75f16648039109adaaeeed1ca9。测试脚本修复了宽字符占位和 Markdown 下划线导致的误判。暂存文件自动清理、原生剪贴板截图、文件拖入尚未验收；没有开发桌面端。

Windows VT 模式参考：[SetConsoleMode](https://learn.microsoft.com/en-us/windows/console/setconsolemode)。图片协议参考：[OpenAI 图片输入](https://developers.openai.com/api/docs/guides/images-vision)、[Anthropic Vision](https://platform.claude.com/docs/en/build-with-claude/vision)、[Gemini 图片输入](https://ai.google.dev/gemini-api/docs/image-understanding)。

第八批：新增 Core 持久输入收件箱及终端队列管理，排队图片直接保留原字节；支持 queue/steer 切换、按会话隔离、撤回和删除，已经提升的输入拒绝再次修改。任务步骤结束时提升 steer，普通 queue 保留到空闲边界；steer 不打断正在执行的工具。终端新增 /queue 面板、Ctrl+X,Q、Enter steer、Ctrl+D/Delete 删除、Ctrl+U 恢复到原草稿（保留已有文字）、Tab 移回 queue，以及命令式等价操作。保留原有剪切/撤销别名，完整自定义键位系统尚未实现。运行环境失败时输入保留并停止自动重试，以 /queue run 重试；暂停标记目前只在进程内按会话保存。

第八批验证：图片/输入/全屏/终端产品/收件箱/子代理通知筛选 65 项通过（23 秒），含真实 Hyperlight。检查重启后图片和 ID 保留、不同会话隔离、并发提升无重复、编辑失败不丢输入、删除原图后恢复快照、模型 stop 前收到 steer 会继续下一轮、普通队列等待新 run、无效模型配置不消费输入。真实 ConPTY + 本地 HTTP SSE + Hyperlight 验收通过：队列面板方向选择、Ctrl+X,Q 打开、Ctrl+U 追加回原草稿、Enter steer、查看/删除、删除源图后的实际请求字节、管理命令和未提交草稿不进入模型。证据 artifacts/qa/inbox/073f767febd94b5ea941fb682a1f5152，脚本 tools/inbox_smoke.py。图片草稿回归 artifacts/qa/image-draft/41a1d62634514c2e9f3076768a822f78、父子导航与导出回归 artifacts/qa/session-navigation/2a7373ea73444105b80ac9555fcd7d8b 均通过。Release 构建通过。子代理工具对运行中 sessionID 的程序化 steering 仍未实现；此次实现的是人类会话输入的 steering，不代表全部子代理生命周期对等。

## 后续实施顺序

第九批：终端新增 /timeline 与 Ctrl+X,G（空闲输入时），消息选择按用户输入倒序并排除内部通知，支持搜索、定位和跳转/复制/恢复草稿/分支操作。定位直接使用宽度下的真实渲染行数，涵盖宽字符、自动折行与执行块。新增 Message.InputText 保存引用展开之前的人类草稿，队列提升也保留该字段；显示及 Markdown 导出优先使用人类输入，提供商继续接收原有展开内容。旧记录回退到 Content。/fork 选择完整会话或用户消息前边界，所选消息及后续结果不进入新分支，图片以保存的字节恢复到未提交草稿。完整分支和历史分支均重新映射压缩摘要边界 ID，边界不在分支内时清除该摘要。

第九批验证：时间线/会话导出/全屏/图片引擎/收件箱/产品/图片草稿/检查点筛选 66 项通过（16 秒），随后真实 Hyperlight 分支摘要测试通过：新分支模型请求包含有效摘要、不会重发已总结的旧消息，工具仍仅 execute_code。真实 ConPTY 时间线搜索、Ctrl+X,G、跳转、已有草稿保留、图片恢复、/fork --before 和原始历史不变验收通过，证据 artifacts/qa/timeline/be891e76598a41fd8f046fb35e26befc；图片草稿回归 artifacts/qa/image-draft/c1eb51d4e409409b828a0a21d05c00aa、父子会话与导出回归 artifacts/qa/session-navigation/76521a1ff1474bbea9ddaf289592af5b 均通过。复制入口沿用已有剪贴板实现，此批未做系统剪贴板实测。时间线 Revert 尚未实现；它需要跨多轮文件状态预检查、文件与会话提交的恢复日志，以及对 steer 插入点更细的检查点，不能只删消息假装撤销文件。完整目标仍未验收。

第十批：阅读 `packages/core/src/session/revert.ts` 的 stage/clear/commit 语义，新增持久 SessionRevertState，支持任务边界跨多轮撤回及向前/向后调整撤回边界。历史在暂存阶段完整保留，可见历史、时间线、Markdown、最后回复复制和分支遵循撤回边界；JSON 保留完整记录。/redo 恢复原始文件与历史；有效新用户任务在 CodeAct 环境准备后提交撤回，旧检查点失效，缺少模型配置和内部通知不能提交。原始输入与图片恢复为未提交草稿，启动恢复暂存草稿；后台任务未结束时拒绝操作，暂存期间暂停自动续跑，并拒绝破坏恢复边界的手动压缩。

文件与会话元数据使用先落盘的事务日志：检查所有文件预期状态后写入，失败时按磁盘上的会话元数据决定回滚或完成；重启同样执行恢复。外部文件修改、历史哈希变化或未知状态阻止覆盖并保留日志。此机制只涵盖检查点记录的宿主文件操作；Shell/MCP 外部副作用不会被逆转。运行中 steer 没有独立任务起始快照时明确拒绝精确撤回，未宣称任意消息对等。

第十批验证：撤回/检查点/时间线/导出/终端产品/全屏/收件箱/通知筛选 50 项通过（25 秒），含真实 Hyperlight 新任务测试。覆盖多轮边界移动、文件删除与重建、外部冲突无部分写入、Windows 只读文件引发写入失败后的回滚、持久部分事务的启动回滚与完成、无效输入不提交、真实模型请求排除隐藏历史、工具仍仅 execute_code，以及撤回状态禁止压缩。持久部分事务通过落盘后重新创建服务验证，并非进程强杀验收。真实 ConPTY 撤回确认、文件变化、历史隐藏但持久保留、重启草稿恢复及 /redo 通过，证据 artifacts/qa/revert/1475896aee0e4183bdb7fc6acb9296c0。时间线与图片分支回归 artifacts/qa/timeline/e69415ae52dc4b05addb40016d3d2d73、真实 ConPTY + 本地 SSE + Hyperlight 收件箱回归 artifacts/qa/inbox/32e9310cce8f4f3bab9c70f004305e91 均通过。终端 Release 构建零警告、零错误。没有开发桌面端；完整终端功能与商业发行仍未验收。

第十一批：对照 `packages/core/src/tool/plugin/subagent.ts` 的 prompt admission 和 join running child，终端向运行中 sessionID 调用 subagent 时持久保存文本 steer 并返回原任务 ID。后台调用立即返回，前台加入原任务等待最终结果；执行中的工具和待处理授权/问题不会被指令自动中断或解决。BackgroundTasks 新增与最终完成共用锁的 TryJoin，并在一次 Agent 流正常结束后检查追加输入，必要时在原任务内再次执行；已完成后不接收、取消中拒绝、跨父会话拒绝，未支持续跑的嵌入宿主明确拒绝 join。异常和取消不会保留上一轮成功的 FinalOutput。

第十一批验证：子代理/后台任务/收件箱/撤回引擎筛选 24 项通过（86 秒），含真实 Hyperlight。新增边界测试先让真实 Agent 流发出 stop，再加入修正，确认同一任务续跑、输入一次提升、只生成一个最终父通知，并验证待人类回答时指令不消除等待、取消不接收新指令、跨父归属拒绝和运行中角色切换拒绝。真实终端宿主 run --json + 本地 HTTP SSE + 父子 Hyperlight 验收通过，脚本 tools/subagent_steering_smoke.py；fixture 等待子代理真实响应开始后，父代理经 execute_code 调用 subagent 追加修正，确认同一 task/session、最终修正回复、父历史没有该用户指令以及工具协议仍仅 execute_code。证据 artifacts/qa/subagent-steering/a064b9b01ffb4a0d8322eb4dfb11388e。运行中模型/提供商/角色切换、steering 的图片与文件引用展开仍未实现；此批不代表子代理完整生命周期对等，也没有开发桌面端。

第十二批：子代理 typed subagent 新任务与运行中 steering 新增异步附件准备。SubAgentTools 保留旧同步嵌入回调并新增带 CancellationToken 的异步接收回调；终端在创建子会话、任务或入箱之前展开工作区 @引用，保存原始草稿、文本预览和图片字节。新任务首次执行复用接收时快照，steering 将同一快照持久入箱，后续执行不重新读取原文件。图片协议与 CodeAct 执行契约不变；路径与大小仍遵循已有工作区规则。旧 start_agent 兼容入口继续沿用原处理流程，未声称全部入口已统一。

第十二批验证：子代理会话/steering/图片输入/图片引擎/图片粘贴/收件箱筛选 33 项通过（30 秒），包括异步准备等待、准备中取消不启动任务、现有四提供商真实 HTTP 图片字节测试和真实 Hyperlight。真实终端宿主 + 本地 SSE + 父子 Hyperlight 脚本新增缺失图片拒绝测试，以及运行中同时引用 PNG 和文本文件后删除原件的验证：子会话保留原草稿、文本预览和相同 PNG Base64，实际模型 HTTP 请求含正确 image_url 数据，任务和子会话各只有一份。证据 artifacts/qa/subagent-steering/b16ff30bb19b4bb1a958d40a04d9bb6e；没有使用付费模型或开发桌面端。运行中角色/模型切换、更多附件类型与完整商业发行仍待实现和验收。

第十三批：对照上游已有子会话 switchModel 逻辑，终端允许在运行中 subagent 调用里显式指定 model/provider。DataStore 在同次保存中写入追加输入与子会话选定配置；模型步骤边界解析配置快照、重算上下文容量，当前响应及其重试仍使用原配置。RequestRoutingAiProvider 每个请求通过现有宿主工厂选择提供商与认证，不改变父会话。仅覆盖 model 且省略 provider 时沿用该子会话保存的提供商，避免回退到父提供商。

新增 Message.ProviderContextKey 保存原生响应状态的配置来源哈希（配置 ID、提供商、协议、模型、地址和认证提供商标识，不保存 API key）；切换时过滤旧状态，重载历史时只保留来源匹配的状态。动态配置会话中没有来源标记的旧原生状态不复用；普通固定配置旧记录保留兼容行为。此来源标记不代表认证账户轮换已经验收，跨厂商真实付费模型与账号轮换仍需专项验证。

第十三批验证：模型切换/steering/收件箱/缓存/撤回等筛选 18 项通过（31 秒）；扩大至 Responses 提供商、各引擎、CodeAct 协议及缓存后 34 项通过（40 秒）。真实 Hyperlight 测试确认下一步骤提供商路由、保留正文与修正、旧原生状态排除、重开 DataStore 后当前原生状态保留、无效配置不入箱、容量变化和唯一 execute_code 协议。旧引擎测试的 Answer fixture 补齐真实协议使用的 stop 结束原因，使摘要完整性检查保持原有严格要求。真实终端宿主 + 双 HTTP SSE 服务 + 父子 Hyperlight 验收通过，确认切换后请求发送至第二服务、使用第二认证头及模型、父配置不变，且图片/文本删除后的快照仍正确。证据 artifacts/qa/subagent-steering/a4c3b144b0494db2b5de3151dbcf1893。终端 Release 构建零警告、零错误；运行中角色与权限切换仍待实现，没有开发桌面端，完整复刻和商业发行未完成。

第十四批：对照已有子会话 switchAgent 的语义，general/explore 角色与追加输入、模型配置在同次保存中提交；接收检查不提前改变运行中角色。终端将父代理启动时的权限模式与当前子角色分开：切到 explore 时实时只读，即使父代理完全访问也不允许写入、Shell 或风险外部调用；切回 general 恢复父代理所授模式，父代理只读不能被角色切换提升。SubAgentRolePolicy 允许工作区读取与该子会话之前明确授权的外部目录读取，拒绝未授权外部读取。

ToolRegistry 新增可选 InvocationPolicy，审批之前及真正调用宿主工具之前各核对一次，防止审批等待过程中角色变化后仍执行旧授权；已开始的工具不强制中断。新限制与既有宿主策略共同生效，默认无策略的嵌入行为不变。AgentEngine 每一步刷新独立项目/角色系统指令，保留稳定的 CodeAct 契约系统消息；角色变化影响下一模型请求，不伪装成一次新子任务。

第十四批验证：角色/steering/模型/工具/注册替换/外部目录/引擎/收件箱筛选 82 项通过（68 秒）。新增真实 Hyperlight 测试：原先生成的写入在角色改变后被拒绝、工作区读取成功、下一请求含新角色、切回 general 后真实写入成功，且协议仍仅 execute_code。另测审批已通过但执行前撤销权限、父只读约束、完全访问不能绕过未授权外部读取、明确授权目录继续可读。真实终端 run --json --full-access + 双 HTTP SSE + 父子 Hyperlight 验收通过：同一子任务从 general 切到 explore 后尝试写入失败、实际文件不存在、读取成功、模型收到 Explore 指令，并同时保持模型切换和图片快照功能。证据 artifacts/qa/subagent-steering/0f461608c1a1486ca12d4476f5c40103。终端 Release 构建零警告、零错误。自定义角色、角色默认模型配置、嵌套深度及完整商业发行仍未完成；桌面端未开发。

第十五批：阅读上游 Agent editor/list/resolve、插件角色定义及 subagent 的角色模型优先级，新增持久 AgentRoleCatalog，支持内置覆盖和自定义 name/description/instructions/readOnly/model/provider。终端提供 /agents 与独立 agents 的 list/show/import/remove，JSON 导入按名称合并、整批验证后保存，内置角色 remove 恢复默认，活动角色拒绝删除。角色说明列入父模型系统指令，自定义名字可通过 typed subagent 使用；新建或切换角色时使用角色默认模型，显式模型/提供商覆盖优先，同角色继续时保留已选配置。手动进入自定义子会话继续任务同样使用该角色指令和宿主限制。

配置样例 docs/agent-roles.example.json；配置存于当前 DataStore 的 agents:roles 设置，支持跨进程重载，最多 64 个配置角色。Explore 内置只读属性不能取消；自定义角色权限不能越过父代理授予的模式。此批尚未实现上游全部 mode/hidden/color/工具资源规则字段，也没有开放嵌套深度。

第十五批验证：角色配置/子会话/角色切换/模型切换/收件箱/终端产品/引擎筛选 46 项通过（31 秒）。新增配置重载、内置覆盖重置、非法整批不改配置、活动角色不能删除测试。真实终端宿主 + 双 HTTP SSE + 父子 Hyperlight 验收通过：agents import 导入 reviewer，另一个独立 agents show 进程读取相同指令与默认模型，管理期间没有模型请求；运行中的 general 子代理切为 reviewer 后，配置中的默认模型/提供商实际用于第二 HTTP 服务、指令进入模型请求、只读写入拒绝而读取成功，并保持图片/文本删除后的快照、单一任务与 execute_code 协议。证据 artifacts/qa/subagent-steering/c348ec620d454792bebbc174cc5f0ca7。Release 构建零警告、零错误，没有开发桌面端；完整复刻和商业化尚未完成。

第十六批：对照上游 permission.evaluate 的最后匹配优先、未匹配 ask 和 wildcard 的路径斜杠、Windows 大小写、星号/问号、末尾可选参数语义，新增角色 permissions 配置。规则可按宿主名称或兼容名称控制工具，文件资源使用规范化绝对路径，Shell、Web、skill、task 和 MCP 资源有对应资源值，未映射工具仍可按注册名限制。未配置 permissions 保留原宿主行为；配置空数组则逐次询问所有工具。配置整批校验、不可变缓存，最多 256 条规则。

工具调用保留父权限、宿主 preflight 和原审批限制，另外新增实时 InvocationAuthorization，即使完全访问模式和 Read 风险也执行角色 ask。后台请求进入 /tasks 审批，手动恢复子会话使用终端权限窗口；JSON 非交互拒绝需要确认的调用。审批期间角色配置改变会使旧授权失效；授权拒绝或规则 deny 不执行工具、不向模型返回受保护内容。授权预览增加读取/目录语义，任务审批保留角色来源。只允许/拒绝本次；上游 always/save 记忆规则、全部资源映射、mode/hidden/color、可配置嵌套仍待补齐。当前只读角色与父只读仍为额外不可提升约束。

第十六批验证：角色权限/配置/切换/模型/收件箱/子会话/终端产品/引擎筛选 51 项通过（42 秒）；权限专项包含 canonical 路径、通配符、最后匹配、空规则 ask、完全访问仍要求确认、等待时配置变更撤销、取消与父授权不可提升。真实终端 run --json --full-access + 双 SSE + 父子 Hyperlight 通过，确认运行中切换和重启后手动恢复均拒绝规则禁止读取及未经确认读取，允许读取仍正常；图片快照、角色默认模型切换、唯一 execute_code 协议继续有效。证据 artifacts/qa/subagent-steering/cd7910d1920f4a318e4f66ab99afab33。真实 ConPTY + Hyperlight 通过 Y 允许读取、N 拒绝读取、deny 无弹窗、受拒内容未送模型，证据 artifacts/qa/agent-permissions/0a071e12dd884dca8d9bbe27f777967c。完整复刻和商业发行仍未完成，桌面端未开发。

第十六批最终复验：补充工具/扩展/外部目录筛选 61 项通过（14 秒），最新权限与角色切换专项 8 项通过（10 秒）。标准 Release 输出曾因正在运行的 lanagent.exe 占用失败，未终止用户会话；改用 build/permission-verification/terminal 独立输出后构建零警告、零错误。使用该最终产物重新执行两套真实验收均通过，最新证据为 artifacts/qa/subagent-steering/f709b91879ad4480afca38d3649f936a 与 artifacts/qa/agent-permissions/518e721d277a46e9a344c1538c3eb250。QA 脚本支持 LANAGENT_QA_EXECUTABLE 指定独立产物，默认仍使用标准 Release 路径。

第十七批（终端交互修复）：参考 OpenCode 输入粘贴与分区选择方式，输入框右键支持原生剪贴板文字/图片，有输入或消息选区时优先复制。PNG 注册格式直接读取，普通 Windows 位图/DIB 经固定 STA helper 转换；Ctrl+V/Shift+Insert 与空 bracketed-paste 事件均进入相同图片暂存逻辑，读取在后台完成。消息选区按渲染块限制，表格按起始列限制，跨行高亮和复制使用同一边界，排除邻接消息、侧栏与布局边框。

文件候选改为后台目录索引，最多缓存 8 个目录并周期刷新，刷新时保留旧候选；补全区支持滚轮选择和点击补全。消息区滚轮只作用于消息区，同方向同位置的积压事件合并，连续输入不再逐事件等待 33 毫秒，空闲输入轮询为 8 毫秒。不同方向/区域事件保留顺序。修复 Windows VT 空格键映射和 Escape 后普通字符的事件顺序。

AI ask_user 新增可选 multiple=true，使用实际 Spectre MultiSelectionPrompt，单选使用 SelectionPrompt，无选项仍自由输入；多选返回 JSON 字符串数组，支持自定义回答和取消。Live 在组件获得终端所有权时停止，结束后恢复；后台问题仍经任务面板进入相同入口。连接和沙箱准备使用实际 Spectre Status；允许 Esc 或需要交互安装授权的等待保留共享全屏状态。加载样式统一为可持久配置的 Spinner.Known，默认 Mindblown，/spinner、/spinner list、/spinner 名称及 /settings → 加载指示均可操作。

第十七批验证：终端交互/输入选择/全屏/Spectre 迁移/图片草稿/执行体验 56 项通过（2 秒），工具/角色权限/后台任务/子代理/终端产品回归 67 项通过（3 秒）。独立 Release build/interaction-verification/terminal 构建零警告、零错误。真实 Windows 原生 PNG/DIB 剪贴板 + ConPTY + 本地 SSE + Hyperlight 最终验收通过：消息拖选跨入侧栏和后续消息仍只复制起始块，右键文字粘贴与选区复制、文件候选滚轮和点击、右键 PNG/Ctrl+V 位图/空粘贴图片、三张真实 HTTP 图片、AI 实际多选两个答案、Status 沙箱准备、加载设置持久保存。80 个滚轮事件在本机约 95 毫秒完成，此值为本机 fixture 结果，不代表所有机器性能。证据 artifacts/qa/terminal-interaction/b6c7953c7dc946ed9d79783388bea709，脚本 tools/terminal_interaction_smoke.py；脚本结束恢复剪贴板，不将用户原剪贴板写入证据。运行中的旧终端需退出后重启加载新代码；未开发桌面端，完整 OpenCode 对等和商业发行仍未完成。

组件参考：[MultiSelectionPrompt](https://spectreconsole.net/console/prompts/multi-selection-prompt/)、[Status](https://spectreconsole.net/console/live/status/)、[Spinner](https://spectreconsole.net/console/reference/spinner-reference/)。

1. 子代理：继续补齐权限记忆规则、可配置深度和嵌套权限继承、取消传播。
2. Agent：输入队列与 steering、取消、重试、压缩、恢复、工具/问题/授权状态机。
3. 命令与终端：按源码列出完整清单，逐项实现操作及真实交互验证。
4. 终端会话导航：父子会话切换、时间线、排队输入管理和真实终端验收；不开发 Vue/Avalonia。
5. 工具与集成：LSP、附件、MCP OAuth、Git/worktree 等，审阅后补充具体对应文件。
6. 商业发行：依赖许可清单、数据迁移/恢复、安装升级/回滚、签名及真实发行环境验证。

每项只有在实现、回归和交互验证完成后才标为通过。远程分享等依赖后端的能力必须有可部署服务，不能以占位按钮验收。真实付费模型、签名证书等尚无验证证据时保持未验收。
