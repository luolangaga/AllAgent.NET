# 提示词缓存排查

缓存命中取决于服务端实际渲染的上下文是否可复用。客户端修复只能提高复用机会，不能保证命中；本次没有发送付费模型请求，也没有测得线上提升百分比。

## 已处理

- `AgentEngine` 原本在每次迭代将当前 TODO 改写到第二条 system 中。现在仅在清单变化时追加内部用户消息并持久化；跨用户轮次复用相同快照，清空清单会追加空快照。测试验证历史前缀在更新和跨轮次时保持一致。
- 普通 OpenAI 请求原本没有 `prompt_cache_key`。Chat Completions 和 Responses 现在对 HTTPS 官方 `api.openai.com` 使用稳定的会话 ID；每轮的 `RoutingTurnId` 不参与缓存 key。第三方兼容接口不自动发送该字段。Codex 专用端点原有的 session-id 保持原行为。稳定 key 对不同模型的作用不同，不能保证命中，也不能修复内容本身变化。
- PowerShell 模块摘要进入第一个 system 块，但此前没有在摘要层规范排序。现在按模块名、版本排序，防止同一组模块仅因枚举顺序不同而改变提示词。

## 仍会影响复用的条件

| 条件 | 当前代码与影响 | 处理边界 |
|---|---|---|
| 项目规则、技能列表、子代理目录或角色变化 | `AgentEngine` 每步刷新 `InstructionsProvider`；`AgentService` 将技能和可用代理拼入其中，修改会影响后续历史前缀 | 不能冻结角色权限或忽略新项目规则；插件生成此内容时应避免时钟、随机值或无关运行状态 |
| 第一个 system 中的运行环境或能力变化 | `CodeActProvider.Instructions` 包含工具目录、沙箱说明、模块摘要、工作目录和运行限制 | 真实能力变化需要反映；跨工作区或跨宿主不保证共享完整 system 缓存 |
| 插件修改历史 | `ConfigureRequestAsync` 可以改写任意历史内容 | 插件应尽量追加新上下文，避免重复格式化或改写已有消息；当前未对插件做缓存命中保证 |
| 上下文压缩、撤回或图片恢复 | 摘要替换历史、撤回裁剪历史、删除被拒图片都会改变请求前缀 | 这些是必要行为，缓存降低不能直接判断为缺陷 |
| 模型、账号或推理配置切换 | 不同模型和账号不能假定共享缓存；部分配置还会影响提示词渲染和 native state 重放 | 不为命中率保留不兼容的签名或加密状态 |
| Anthropic 断点和回溯 | 当前使用最多两个 system 断点及末尾两个可缓存消息断点；大量新增内容块可能使之前写入的位置落在回溯窗口之外 | 当前末尾两个消息并不等于精确保留上一次请求边界；需要真实长块请求数据再调整策略 |
| Anthropic 旧模型的思考缓存 | 追加非 tool_result 用户内容可能触发部分旧模型移除思考块；TODO 快照也属于这种内容 | TODO 改动保留客户端前缀，不代表所有 Anthropic 模型都保留服务端思考缓存 |
| 缓存长度门槛、过期、驱逐和路由 | 即使输入相同，也可能没有可复用的服务端缓存 | 以提供商返回的 cached tokens 为准；未知值不应当作零 |

## 已核查，不是当前已确认的缺陷

- `ToolRegistry.Definitions` 已按名称使用 ordinal 排序；`ModelTools` 虽未再次排序，但来自此有序集合。不能仅看调用点认定工具顺序随机。
- 三种原生协议保留 provider state 重放；`CodeActHistory` 会校验状态，只在不兼容时清除。没有证据表明正常历史每轮随机生成工具 ID。
- Codex 已使用会话级 `session-id`，`x-codex-turn-state` 是单轮路由状态；二者用途不同，不能简单将后者无限跨轮保留。

## 官方参考

- [OpenAI Prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching)：模型之间策略不同；稳定 key、服务端路由、过期及完全相同的可复用前缀共同影响命中。
- [Anthropic Prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching)：最多四个显式断点、20 个位置的回溯窗口、思考配置与模型相关的失效条件。
