# 多会话后台运行与跨工作区

一个主会话开始执行后，界面切走不再中断它。这份文档说明实现方式、行为边界和验证步骤。

## 行为

| 场景 | 结果 |
|---|---|
| 会话 A 运行中执行 `/new`、`/session <ID>`、`/parent`、`/children`、F3 | 视图切走，A 继续执行；切回 A 时重放本轮已产生的输出并继续实时渲染 |
| 会话 A 运行中切到会话 B 并发送新任务 | B 立即开始，A 同时继续；两个会话并行 |
| 给正在运行的会话发送文本 | 作为指导注入当前任务（等同 `/steer`），不另起一轮 |
| 后台会话需要授权/提问 | 挂起等待并在列表标记「需要授权 / 需要回答」，切回该会话立即弹出对话框继续；不会代替用户批准或拒绝 |
| 后台会话结束 | 状态行/侧栏提示一次（Web 为提示条），不自动跳转 |
| 运行中 `/workspace <路径>` | 旧工作区的会话与后台任务继续执行，终端只把屏幕移到新工作区 |
| 运行中 `/permissions`、`/plan`、`/simple` | 立即生效：权限立场是进程级的活状态，正在运行的任务从下一次工具调用起采用新模式 |
| 运行中 `/sandbox`、`/undo`、`/redo` | 需要先停止全部会话与后台任务（要重建沙箱运行时或改写历史） |

## 实现

**运行与视图分离（终端）**

- `SessionRuns` / `SessionRun`（`src/AllAgent.NET.Terminal/SessionRuns.cs`）：每个会话至多一个运行；运行自带取消令牌、完成信号、状态机（运行中 / 需要授权 / 需要回答 / 正在停止）与事件日志。
- 运行在自己的任务里泵事件：先写入 `BridgeStreamJournal`（沿用 Web 端的合并与截断策略），再推给当前挂载的视图（`Channel`）。没有视图时事件只进日志，不阻塞运行。
- `TerminalUi.AttachRunAsync`（`ViewAsync`）挂载视图：先用日志的 history 快照重建 transcript，再回放日志事件，最后消费实时事件。键盘循环（Esc 停止、回车指导、`/命令`、授权对话框、Ctrl+O 详情）保持不变。
- 离开视图只解除挂载（`DetachRequested`），不取消运行；停止运行只能通过 Esc/Ctrl+C、列表里的停止或退出进程。

**工作区常驻宿主（终端）**

- `TerminalHost` / `TerminalHosts`（`src/AllAgent.NET.Terminal/TerminalHost.cs`）：每个数据目录一份 store、后台任务、MCP 连接、插件、技能目录、检查点、沙箱运行时与 `SessionRuns`，进程级常驻。
- `/workspace` 仍通过 `WorkspaceChange` 重建界面，但不再释放旧宿主，因此旧工作区的运行、后台任务与沙箱继续存活；重进旧工作区时复用同一宿主。
- 沙箱运行时按角色缓存（`primary` 与各子代理角色各一份），切会话不再销毁别人正在使用的运行时。
- 空闲宿主在切换时按最近使用淘汰，保留 4 个；有运行或后台任务的宿主不淘汰。

**Web / 图形界面**

- `WebUiSession` 每个工作区一份 bundle（store、任务、检查点、技能、AgentService、桥接方法表）。切换工作区只把「当前 bundle」换掉，旧 bundle 继续跑；空闲且无运行的 bundle 才回收。
- `BridgeDispatcher.Install(slot, handlers)` 按槽位整体替换方法表，宿主级方法（审批、workspace 控制、`conversations.running`、`conversations.cancelRun`）不受影响。
- 运行状态广播：`AgentService.RunStateChanged` 与 `WebApprovals.Changed` → `runs.changed` 推送；`conversations.running` 返回所有已打开工作区的运行中会话及其状态。
- 前端：会话列表与工作区分组显示运行标记，右下角浮层（`RunDock`）列出其他运行中的会话（可跳转、可停止），后台完成时提示一次；切换会话不再要求先停止生成。

**并发会话共用的宿主设施**

- `WorkspaceCheckpoints` 的活跃回合按会话隔离，工具运行的归属由 `AgentExecutionScope.ConversationId` 决定；撤回/恢复仍要求没有任何回合在写。
- `BridgeStreamJournal` 的 history 改为 `Message`，`Snapshot` 仍输出原有 `MessageDto` 形状（前端零改动）。

## 边界与限制

- 进程内多工作区：其他 `allagent` 进程里的会话不纳入标记与挂载（跨进程仍靠多开终端 + 共享历史）。
- 终端切换工作区会停止 Web 工作台并在新工作区重启它，因此**浏览器侧**正在跑的会话会随该 Web 会话结束；浏览器自身切换工作区（界面内）不受影响。
- **集成终端是会话级的**：终端标签由 `WebUiSession` 持有（`WebTerminalManager`），切换页面、刷新浏览器或切换工作区都不会中断运行；只有关闭标签或宿主退出才结束进程树。文件浏览与 Git 面板则按工作区安装/移除（bundle 切换时自动重载）。
- 权限立场（restricted/ask/full/plan）与沙箱选择是进程级的：变更前需要没有会话在运行。

## 验证

1. Web：会话 A 发长任务 → 立即新建对话并发送 B 的任务 → 两者同时产出；侧栏 A 显示运行中；切回 A 内容连续。
2. Web 跨工作区：A 在 ws1 运行 → 切到 ws2（不报错）→ 右下角浮层仍显示 A → 切回 ws1 挂载 A。
3. Web 授权：后台会话触发写文件授权 → 列表显示「需要授权」且任务挂起 → 切过去弹框 → 允许后继续。
4. 终端：A 运行中 `/new` → 发送 B 任务 → 两个并行，侧栏右下角列出 A；`/session <A-ID>` 切回即恢复实时视图；给 A 发文本进入指导队列。
5. 终端跨工作区：A 在 ws1 运行 → `/workspace <ws2>` → A 继续（侧栏显示其他工作区运行中数量）→ 切回 ws1 用 `/session <ID>` 回到实时视图。
6. 自动化：`SessionRunsTests`（后台运行、去重、停止、后台授权挂起、日志重放）与 `ConcurrentSessionHostTests`（桥接方法表切换、并发检查点隔离、日志快照形状）。
