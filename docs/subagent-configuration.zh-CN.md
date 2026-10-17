# 子代理独立配置

## 运行与取消语义

模型只有一个委派工具 `subagent`(single / parallel / chain),另有 `cancel_agent`:

- **前台(默认)**:调用阻塞到所有子代理进入终态,结果内联返回;父 turn 被取消(终端 Esc、桌面取消流)时,本次调用启动的前台子代理连同沙箱一起取消(Queued 直接取消,运行中先进入 Cancelling)。前台结果不产生额外通知。
- **后台(`background=true`,仅 single;或终端 `/background`)**:立即返回 `taskId`,子代理独立生命周期,不受父 turn 取消影响;完成(或失败/取消/中断)时向父会话投递**一次**内部通知,终端与桌面/Web 都会在父会话空闲时自动唤醒并消化通知。
- `sessionID` 用于续聊已完成的子会话,或对运行中的后台子代理追加 steer(steer 立即返回,结果稍后通知)。取消进行中(Cancelling)不再接受 steer。
- 结果字段:`exitCode` 0=完成、130=取消、1=失败/中断,未完成时为 null;`partial=true` 表示子代理未完成,只能作为不完整证据使用,不得当作最终答案。
- 子代理不能再派发、不能向人提问;子权限 ⊆ 父权限 ∩ 角色权限。

桌面应用：设置 → 子代理独立配置 → 编辑角色 JSON → 保存角色配置。

终端：直接输入 `/agents` 打开交互配置面板，使用方向键选择、Enter 确认。支持新建、编辑、恢复或删除角色。也可以使用 `/agents edit review` 直接编辑指定角色，`/agents add` 新建角色。

编辑器按模型、推理、角色指令、工具与技能、上下文、预算、并发和失败策略分组，并展示当前草稿摘要。数值可输入 `inherit` 清除；工具或技能白名单可选择继承、全部禁止或指定允许项。修改只保存在草稿中，选择“保存并返回”才写入。保存时会校验参数；仍有任务运行的角色需要等任务结束后再保存。取消或中断不会写入草稿。

`/agents list` 查看角色，`/agents show review` 查看配置，`/agents import roles.json` 导入配置。导入按角色名合并；`/agents remove review` 恢复内置角色。未知字段和无效参数会被拒绝。非交互及 `--json` 模式下，`agents` 默认仍输出列表。

示例（provider 请替换为已经配置并启用的提供商配置 ID，model 请替换为该提供商的模型）：

```json
[
  {
    "name": "review",
    "description": "只读代码审查",
    "instructions": "检查正确性、回归和边界条件，返回有证据的问题。",
    "readOnly": true,
    "provider": "你的提供商配置 ID",
    "model": "你的模型 ID",
    "tools": ["file_read", "file_search", "everything_search", "list_directory", "skill_read", "skill_reference"],
    "execution": {
      "temperature": 0.2,
      "maxOutputTokens": 4096,
      "maxContextCharacters": 64000,
      "maxToolCalls": 30,
      "timeoutSeconds": 300,
      "maxTotalTokens": 100000,
      "maxConcurrent": 2,
      "maxDepth": 1,
      "includeProjectInstructions": true,
      "skills": ["review", "verify"],
      "failurePolicy": "retry",
      "maxRetries": 2
    }
  }
]
```

## 配置规则

- 模型选择：本次请求显式指定 → 角色 model/provider → 全局子代理默认 → 当前模型。终端继续已有子会话时优先保留其模型，除非显式切换或更换角色。
- `execution` 中未填的数值限制继承宿主；上下文、输出、时间、工具调用限制只能收紧。temperature 为 0–2。`reasoningEffort` 使用所选模型支持的档位；只有 `budget` 档位可填写 `thinkingBudget`，不支持的档位会明确报错。
- 子代理拥有独立会话，不自动复制父代理聊天。任务 prompt 是显式交接上下文。`includeProjectInstructions` 控制额外项目指令。系统工具协议和角色指令始终保留。
- `tools` 为工具白名单；`execution.skills` 为技能读取白名单，空数组表示全部禁止，省略表示继承。白名单不扩大宿主权限。也可使用既有 `permissions` 的 action/resource/effect 规则控制逐次审批。
- `maxConcurrent` 为同一 DataStore 中该角色的运行名额，范围 1–6，默认 6；同一 DataStore 下所有角色合计最多同时运行 6 个子代理。等待名额可取消；显式超时包括后台任务开始后的角色排队和沙箱启动。它不包含 BackgroundTasks 自身的全局调度队列。
- `maxDepth` 当前支持 0（禁止派发此角色）和 1（一层子代理）。宿主继续禁止子代理再次派发任务，暂不支持多层递归。
- 配置在新执行时应用；终端运行中的角色权限仍按既有机制实时检查。预算、并发、上下文等执行配置应在任务结束后调整再重启任务。

## Token 与费用预算

`maxTotalTokens` 限制单次子任务运行累计用量，重试和上下文整理也计入。没有设总预算时仍可用 `maxOutputTokens` 限制单次模型输出。

费用限制需要一起配置：

```json
{
  "maxCost": 0.5,
  "inputCostPerMillion": 2,
  "outputCostPerMillion": 8
}
```

单价和预算使用同一种货币。这是本地预算控制，不是供应商账单结算：当前取供应商报告与 UTF-8 字节保守估算中的较大值，会比实际 token 数更早停止；缓存折扣、额外推理费用和工具协议开销不保证与账单一致。备用模型也使用这组单价，请填写足以覆盖两者的价格。图片无法可靠本地计价，因此有总 Token/费用限制的角色遇到图片输入会停止并返回原因。流式中达到限制时停止消费并返回父任务，服务端已经发生的计费无法撤销。

预算按每次运行重新计算；继续子会话会开启新的运行预算，不是该会话终生预算。

## 失败处理

- `return`（默认）：错误交回父代理，不重试模型请求。
- `retry`：仅对执行引擎识别的暂时性模型错误重试，次数不超过 `maxRetries` 和宿主限制。已完成的工具不会整段重跑。
- `fallback`：配置 `fallbackProvider` 和/或 `fallbackModel`。模型在输出任何流式块前发生网络错误或返回错误块时，切换一次到备用配置；清除原模型的原生状态。已经开始输出的请求不会中途切换。备用配置仍应用输出限制和温度，推理参数使用备用模型自身配置。

最终失败、超时和预算耗尽沿用任务中心错误状态与父代理结果回传，不自动重放整个可能已经产生副作用的任务。
