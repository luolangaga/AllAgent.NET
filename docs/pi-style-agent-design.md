# Pi 风格的 Agent 提示词与执行展示

2026-10-16 源码对照：Pi `99fe1489707f9f6accbef4377be79ef0544c42f2`，参考 `utils/image-process.ts`、`utils/image-resize-core.ts`、`core/tools/read.ts`、`core/agent-session.ts`、`packages/agent/src/agent-loop.ts` 与 `examples/extensions/subagent/index.ts`。本地参考检出位于 `.artifacts/pi-reference`。

文件图片读取现在通过真实解码拒绝损坏数据，将长边缩至 2000 像素以内并限制 Base64 预算；缩放后返回原始/实际尺寸和坐标换算。已有文件输入 4 MB 上限仍保留。历史消息和直接构造的附件仍只执行原来的格式校验，因此这不是所有供应商图片 400 的通用修复。

委派前先取得足以确定独立任务、输入、预期结果和编辑范围的上下文；小任务与关键依赖路径由主代理处理。并行工具读取无需启动子代理。保留 AllAgent.NET 现有六任务并发限制，Pi 示例为四任务。

普通取消仅停止当前回合与委派等待；已经启动或进入后台队列的子代理继续执行，完成后通知父会话。尚未派发的并行项和后续链步骤不再启动。直接进入运行中的子会话不会自动取消旧 worker，而是提示先等待或显式使用 /tasks cancel ID；主会话可继续输入。显式停止同会话任务的接口保留五秒等待上限，超时不假报 worker 已退出。Pi 扩展可终止独立子进程；当前 AllAgent.NET worker 在宿主进程内执行，不能安全强杀托管任务，进程隔离与强制回收仍待实现。鼠标 SGR 报告在输入取消后继续吞掉剩余协议字节，避免污染下一条输入。

设计参考：Pi 的 [system-prompt.ts](https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/src/core/system-prompt.ts) 和 [README](https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/README.md)。Pi 使用简短核心提示词，按实际可用工具补充规则，再加载项目指令和技能；子代理是可选扩展。

AllAgent.NET 的核心提示词只描述身份、直接完成任务、保留用户改动、必要验证和简洁沟通。文件、命令、检索和委派说明仅在相应工具可用时加入。CodeAct 的 Python 调用契约、Hyperlight 隔离和宿主权限继续由实际运行时定义。

主代理默认自己调查、修改、验证和处理后续修复。角色列表是一组可选能力，不构成 investigate → modify → review → verify 流程。只有用户要求委派，或足够大的独立任务明显受益于上下文隔离或并行时，才使用子代理。

前台子代理的最终回复通过 `subagent` 的 `data.results` 返回主代理，包括每项任务的 `taskId`、`output`、`error`、`exitCode` 和 `stopReason`。输出遵守现有传输大小限制；截断会标记。后台完成通知保留原有机制。

任务创建时记录 `ParentToolCallId`，因此不必等整个调用结束才能查看执行记录。并行子代理各自显示真实状态及最新一步预览，点击对应卡片打开其消息，再点击其中的工具操作查看具体参数、结果或变更。Esc 返回上一层，Ctrl+E 保留调度代码入口。完成状态优先于历史步骤文案；父代理接收返回值后显示“处理子代理结果”。

执行期间支持命令补全，以及 `/tasks`、`/tasks show|approve|answer|deny|cancel ID`、`/status`、`/help`、`/thinking`、`/details`、`/queue` 和 `/steer`。运行中的 `/tasks` 默认列出任务。授权、回答和取消不会阻塞 Agent 事件泵。切换会话、环境或配置等命令仍需先停止当前任务。
