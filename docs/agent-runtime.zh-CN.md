# 代理运行设计

AllAgent.NET 自己的 C# AgentEngine 负责模型循环、工具调用、输入队列、重试、会话恢复和上下文整理。模型只看到 execute_code；文件、搜索、Shell、技能和委派通过 call_tool 调用 C# 宿主工具。CodeAct 的 Hyperlight 隔离和现有界面保持不变。

项目指令由 C# 宿主加载工作区 AGENTS.md 与 .allagent/AGENT.md；桌面端和嵌入式运行时未提供自定义回调时也会加载这些指令。Node.js 仅用于前端源码构建，代理执行不需要 Node.js。

## 委派

subagent 支持单任务（agent + task）、并行（tasks，最多 8 个任务、4 个活动任务）和串行（chain，{previous} 注入上一步结果）。结果包含 taskId、sessionId、output、error、exitCode 和 stopReason；串行遇失败停止。大结果保留任务标识并显式标记截断。

角色包括调查 investigate、修改 modify、审查 review、验证 verify，以及 general、explore。调查/审查只读；修改按派发范围工作；验证可运行构建测试但无源文件写入工具。子代理继承派发模型与权限，拥有独立会话，不能再委派或向用户提问，缺信息或权限时把阻塞原因返回主代理。

停止先进入 Cancelling，撤销所有相关子任务并等待实际操作收尾；停止完成后允许继续输入。重启时旧活动任务标记 Interrupted，不假装仍在执行。

## 会话与恢复

终端默认使用工作区 `.allagent/data`；`allagent.json` 保存清单与配置，会话历史分开保存在 `conversations/`。程序目录 `allagent-projects.json` 登记项目及其数据路径。旧 AppData 数据按工作区导入，原库保留；用户技能、插件、登录凭据和 Python 环境共享，避免重复安装。不可变会话版本先落盘，再原子提交清单，备份清单引用的版本也保留；失败时可恢复完整的前一提交。恢复不会重复插入旧用户输入；撤回和分支继续由 DataStore 管理。共享配置缺失时先短暂重试并读取 `.bak`；只有旧版清单确有用户配置时才播种，空工作区不会创建或覆盖共享清单，损坏文件保留并报错。模型切换过滤不属于新配置的原生响应状态。模型请求暂时失败时使用有界退避，只重试模型请求；已完成工具不重放。重试前清除失败尝试的部分输出，耗尽后返回一次最终错误。

/compact 保存续接摘要和历史边界，保留完整原始消息。自动整理保留最近消息和完整调用/结果对。当前容量不足或总结失败时不丢弃历史。

## 工具契约

file_read：offset 为从 1 开始的行号，limit 为行数，支持图片附件和超长单行分段；file_write：默认拒绝覆盖；file_edit：edits 数组批量替换，每个 {old_string,new_string} 基于同一原文件唯一匹配；歧义或重叠时整批拒绝，支持换行及有限空白/Unicode 归一化，保留 BOM 和换行格式，同文件修改排队。file_search：正文搜索、行号、上下文、文件过滤和分页，使用 rg 并尊重 .gitignore，默认跳过构建/依赖和常见代理缓存，include_ignored 显式扩大范围；names_only=true 时改为按文件名/通配符列出文件，不读内容、遵守 .gitignore、只列文件不列目录。run_shell 使用 exe 和独立字面参数 args，遵循宿主权限；保留输出末尾，截断时完整输出落盘，超时保留诊断。详见 [宿主工具](host-tools.zh-CN.md)。

## 参考范围

设计参考：https://github.com/earendil-works/pi，参考提交 f13c00fb3a4d77ad3faa815057e31d614d42b3f1；尤其参考其 subagent 扩展示例的单任务、并行、串行契约。实现保留 AllAgent.NET 自己的模型协议、C# 插件、技能、MCP、账户配置、权限、持久化和界面，不加载外部 SDK、.pi 扩展或 JSONL 会话运行时。许可证说明见 THIRD-PARTY-NOTICES.md。
