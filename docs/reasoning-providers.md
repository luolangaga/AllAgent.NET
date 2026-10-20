# 思考强度与模型变体

核对日期：2026-10-15。终端 `/effort`、F6、模型切换恢复、底部状态和各 HTTP 提供商共享 `ReasoningSettings` / `ReasoningPolicy`。`default` 保留服务端默认行为，不等同于关闭思考。

| 接口 | 控制方式 |
|---|---|
| OpenAI Chat Completions | `reasoning_effort`；档位按 GPT / o 系列的具体型号区分 |
| OpenAI Responses / Codex | `reasoning.effort`、`reasoning.summary=auto`；账号目录返回的档位优先 |
| DeepSeek 原生 | Chat: `thinking.type` + `reasoning_effort`；Anthropic: `thinking.type` + `output_config.effort`；Responses: `reasoning.effort` |
| OpenRouter | 网关 `reasoning` 对象，不透传上游厂商的专有开关；目录档位优先 |
| Claude 旧版 extended thinking | `thinking.type=enabled` + `budget_tokens`，预算必须小于实际输出上限 |
| Claude adaptive | `thinking.type=adaptive` + `output_config.effort`；新型号请求 summarized 展示 |
| Gemini 2.5 | `generationConfig.thinkingConfig.thinkingBudget`；Pro 不提供关闭，Flash 可关闭 |
| Gemini 3 / 4 | `thinkingLevel`；按 Pro / Flash 等型号区分，并启用 `includeThoughts` |
| 阿里云百炼 | `enable_thinking` 开关与 `thinking_budget`；支持原生 effort 的型号单独处理，二者不同时发送 |
| Kimi / GLM 原生兼容接口 | 支持的混合思考型号使用 `thinking.type`；不把开关伪装成多档强度 |
| WorkBuddy 网关 | 标准 OpenAI `reasoning_effort`（`none` 关闭）；档位按模型族，接口声明的 `reasoningEfforts` 优先，未知模型不显示选择器 |
| CommandCode | 官方模型目录的 `reasoningEfforts`（`off` 映射为 `none`）；目录缺失或尚未刷新时按厂商族回退，`reasoning` 历史配置升级为 `commandcode` |
| xAI | Grok 3 Mini 使用 low/high；固定思考型号保留默认 |

不支持思考的目录条目只展示 default。未知代理不会仅因模型名称含有某个厂商名称就收到该厂商专有字段；可显式配置 `ReasoningProtocol`。模型目录已声明的 reasoning 档位覆盖内置族规则，空列表表示不提供可调档位。账号目录完全没有该模型的档位元数据时，桥接层会先做一次限频 10 分钟的目录刷新，再用刷新结果计算选项；刷新失败则保留厂商族回退。

强度和自定义预算分别存于 `ModelReasoningEfforts`、`ModelThinkingBudgets`，按配置内的模型 ID 保存。非法设置先验证再提交，切换子代理模型也会恢复对应模型设置。旧的 `ThinkingEnabled` / `ThinkingBudget` 输入继续在模型能力允许的范围内兼容。

参考实现与原生接口：

- [OpenCode 模型变体](https://docs.opencode.ai/docs/models/)
- [OpenCode provider transform 源码](https://github.com/anomalyco/opencode/blob/dev/packages/opencode/src/provider/transform.ts)（检索到的 2026-10 快照；当前原始文件链接返回 404，未依赖未读取的源码）
- [DeepSeek 思考模式](https://api-docs.deepseek.com/guides/thinking_mode/)
- [Claude effort](https://platform.claude.com/docs/en/build-with-claude/effort)
- [Gemini Generate Content 思考参数](https://ai.google.dev/gemini-api/docs/generate-content/thinking)
- [百炼深度思考](https://www.alibabacloud.com/help/en/model-studio/deep-thinking)
- [OpenAI reasoning](https://developers.openai.com/api/docs/guides/reasoning)

验证使用本地模拟服务和请求契约断言；不使用用户密钥调用付费接口。新模型发布后应优先刷新提供商目录，再核对其原生文档并更新对应族规则。
