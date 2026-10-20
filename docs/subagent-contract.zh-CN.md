# 子代理派发和结果契约

子代理作为父代理的工具执行，父代理继续负责交付。角色保留 `general/explore/investigate/modify/review/verify`，允许任务所需的任意组合。父代理可主动派发，无需为一般派发重复询问用户。

## 输入

`subagent` 保留单任务、并行 `tasks` 和顺序 `chain` 三种模式，`task` 字符串仍可用；额外接收可选 `contract`：

```json
{
  "agent": "modify",
  "task": "修复指定解析器并报告证据",
  "background": true,
  "contract": {
    "objective": "修复空输入的解析行为",
    "context": "解析器路径、复现输入和现有行为；只提供必要事实",
    "constraints": ["保留无关改动", "不要提交或发布"],
    "editablePaths": ["src/Parser.cs", "tests/ParserTests.cs"],
    "acceptanceCriteria": ["空输入返回预期结果", "相关测试通过"],
    "requiredForCompletion": true
  }
}
```

模型通过 `execute_code` 内的 `call_tool('subagent', ...)` 使用这些参数，不能将 `subagent` 作为直接模型工具调用。

| 字段 | 语义 |
|---|---|
| objective | 必填，1–100,000 字符 |
| context | 最多 500,000 字符；引用数据，不扩展授权 |
| constraints / editablePaths / acceptanceCriteria | 每组最多 256 项，单项最多 4,096 字符 |
| editablePaths | 精确文件、以 `/` 或 `/**` 结尾的目录；不接受其他 glob |
| requiredForCompletion | 新合同默认 true；只有明确独立延续的工作设 false |

最终任务文本仍受原有 1,000,000 字符传输上限约束，不能同时用满所有字段的最大容量。

旧 `task` 转换为内部合同，保留原有前台/后台生命周期；旧后台任务默认可独立延续。没有提供修改范围不会自动获得文件写入范围，`modify` 角色在启动前要求范围；其他旧任务触及文件写入时会返回范围阻碍。继续旧子会话时保留父代理此前明确给出的合同。需要写文件的旧调用应补上 `contract.editablePaths`。

## 并发和权限

进程内全局最多六个活动子代理，另保留每个数据宿主的角色并发限制。并行 `tasks` 最多八项；超出的活动任务排队。先检查同一批的修改范围，再启动第一项；后台 single 启动也使用共同派发锁，防止范围检查与登记互相穿插。路径按工作区规范化，祖先目录与子文件视为重叠。

同文件、同 GUI 状态和同构建输出有依赖时，应顺序完成。合同能自动检查声明的文件范围，不能推断任意任务文本中隐藏的 GUI 或构建依赖，父代理仍须判断。

子代理的角色、权限和父工具允许表在派发时保存上限。运行中读取当前角色和权限来收紧；扩大父权限或修改角色配置不会自动扩大原始子授权。审批等待结束后再检查，旧批准不跨权限变化继续有效。

文件写入与编辑工具在网关检查范围，并拒绝含糊 Windows 路径和 reparse point。Shell/PowerShell 仍是经审批的宿主进程执行；`editablePaths` 不是操作系统进程隔离，也不把命令变成受限文件系统沙箱。角色和宿主审批仍然适用。

## 等待与生命周期

前台调用等待选定子任务，随父调用取消；后台任务拥有独立生命周期，并发送一次性完成通知。`sessionID` 用于继续已结束的子会话或 steer 运行中的后台子任务；steer 不自动放宽原合同。取消不回滚已经发生的外部副作用。

```python
result = call_tool("wait_agents", ids=[task_id_a, task_id_b], timeout_seconds=60)
if not result["ok"]:
    print(result["error"])
else:
    print(result["data"])
```

`wait_agents` 只接受当前父会话拥有的代理任务，1–8 个 ID，默认等待 60 秒，允许 0–60 秒。返回 `results` 和 `pending`。超时返回当前状态，`partial=true`、`exitCode=null` 代表尚未结束；不重复返回增长中的全文。代理任务不能交给 `shell_status`。

框架内核在最终完成前会等待仍活动的必需子任务，接纳结果后再请求模型整合；等待也受到父运行取消和超时约束。旧内核保持原有后台完成语义，便于迁移回退，不应据此测试新的必需结果保障。

## 顺序交接与结果证据

链式任务不再将前一步输出正文直接替换进下一步指令。`{previous}` 会变成明确的数据引用，上一结果作为独立 JSON `previousResult` 传入，合同 context 同样放在引用数据中。伪系统指令不能成为新的宿主权限或执行协议。前一步失败时停止后续链步骤，不重跑已成功兄弟任务。

结果保留文本输出、状态、退出码和 partial，并可增加：

```json
{
  "evidence": {
    "modifiedFiles": ["src/Parser.cs"],
    "verification": ["run_shell: ok=True, exitCode=0, trace=<宿主轨迹 ID>"],
    "findings": [],
    "blockers": [],
    "unverified": ["宿主工具证据不能替代父代理验收"]
  }
}
```

宿主自动采集类型化文件写入及进程结果。为避免复制密钥，通知只保留进程名、状态、退出码和原轨迹引用，命令参数保留在现有会话工具轨迹中。旧任务记录缺少 evidence 时仍可读取；数组为空不是“没有问题”的证明。父代理必须查看真实改动和验证结果，明确报告缺失证据与阻碍。
