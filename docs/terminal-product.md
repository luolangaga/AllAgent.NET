# AllAgent.NET 终端功能对照与验收

设计参考来自 [OpenCode TUI](https://opencode.ai/docs/tui/) 与 [Claude Code interactive mode](https://code.claude.com/docs/en/interactive-mode)。这是一份功能核对记录；参考产品的云服务、协议和完整插件生态并未全部复制。

## 已实现

| 功能 | 实现与入口 | 验证 |
|---|---|---|
| 全屏终端 | Spectre.Console 布局、固定输入、宽屏侧栏、窄屏隐藏；询问、授权与选择菜单插入聊天区 | 45×18、80×24、120×35 渲染测试；真实 PTY |
| 长消息滚动 | 按消息和终端宽度缓存排版行；只重新布局变化的执行卡，滚动仅渲染可视行 | ProductRelease 编译通过 |
| 输出容量 | 新模型默认 16,384 输出 Token；工具文本 20,000 字符、聚合 JSON 64,000 字符，截断可见且文件支持分页 | ProductRelease 编译通过 |
| 滚动与状态图标 | Windows 原生 / SGR 滚轮、分区滚动、同方向积压合并、阅读锚点、回到最新；实际 Status 与可配置 Spinner，默认 Mindblown，/spinner 或 /settings 更改 | 滚轮解析与阅读位置测试；真实 ConPTY 80 事件约 95ms；Status 准备与加载设置持久保存 |
| 后台任务 / 子 Agent | AI 按需派发，默认继承当前模型，也可单次指定或 `/subagents` 保存默认值；独立会话与 microVM、并发队列、结果持久化、取消、人工授权与回答 | 实际父子 microVM；队列、取消、等待和重启中断测试；模型选择隔离测试 |
| 联网搜索 / 网页抓取 | 内置 C# `web_search`，默认 DuckDuckGo → Bing → 百度失败回退；明确指定多引擎时并行合并去重，最多 10 条；无需 Node/npm、搜索 Key 或守护进程。`web_fetch` 读取公开 HTTP(S) 文本，限制输出、验证 DNS 和重定向，内容按不可信资料处理；设置 `ALLAGENT_SEARCH_URL` 可选旧服务 | typed dispatch、错误、截断、私网拦截测试；内置引擎解析与回退验证；真实联网结果受搜索引擎可用性影响 |
| 模型缓存 | 固定指导与项目指令分块、工具固定排序、Anthropic 首/末 system 与历史缓存断点；上下文整理保留全部 system 指令 | 四种协议请求结构、跨项目缓存前缀、上下文总结与恢复测试；实际命中依赖模型与提供商 |
| DLL 插件 | NuGet AllAgent.NET 标签发现、依赖安装、本地包、启停、卸载、工具 / 模型 / 命令 / 服务扩展 | 实际 NuGet 包和 DLL 加载、禁用、重新启用、路径拒绝测试 |
| Hooks | 五个生命周期阶段，执行前否决，5 秒回调预算，持久开关、错误诊断 | 否决无副作用、超时和 After 失败观察测试 |
| 账号登录 | `/login` 内置 ChatGPT 公共客户端与 Responses、插件登录入口 | 本地回调、JWT 伪造拒绝、账号绑定、DPAPI、刷新与 SSE 中断测试 |
| OAuth | 公共客户端 PKCE / 设备码、state、刷新、DPAPI，认证插件绑定模型 / HTTP MCP | 本地授权回调、错误 state、刷新、设备等待与认证 MCP 实测 |
| 命令发现 | `/` 默认展示 10 个常用操作，前缀展开高级子命令；Tab 补全、Ctrl+P 搜索全部、`/help all`、简化 F1 菜单；直接聊天执行任务，移除 `/agent` | 键盘状态测试、常用与高级菜单筛选测试、真实菜单返回保留草稿 |
| 输入 | 中文/emoji/组合字符、多行、鼠标拖选、Shift 扩选、全选、复制/剪切/替换粘贴、撤销/重做、历史搜索 | 字素选区、失败剪贴板保留、粘贴不提交、实际渲染坐标点击、光标视口测试 |
| 分块复制与图片粘贴 | 消息拖选限于起始块、表格限于起始列；右键有选区复制，否则在输入框粘贴文字/图片；PNG/DIB、Ctrl+V、空粘贴兼容 | 分块与列边界测试；真实原生剪贴板 + ConPTY + SSE + Hyperlight 三张图片与选区内容验收 |
| 任务生命周期 | 流式文本/思考、阶段/计时、取消、下一条输入排队；无最大轮次和默认总时长限制 | 110 次连续执行回归；本地 SSE 端到端 |
| CodeAct 协议修复 | SDK 指令注入；仅 execute_code；旧原生历史兼容；违规未执行、限次纠正；业务失败可在 Python 中处理 | 三种协议实际 HTTP 请求、真实 microVM 失败分支 / 拒绝写入 / 快照恢复 |
| 思考与错误显示 | 固定单行滚动、逻辑行数、红色失败与未执行标记 | 中文、emoji、组合字符、24/80/120 列渲染测试 |
| Python 执行过程 | 默认带行号的源码方框、绿色工具调用、鼠标浮窗 / Ctrl+E、循环逐次结果、会话恢复 | 80/120 列实际命中、中文结果、草稿保留与恢复回归 |
| 限制模式 | 默认；工作区内文件读写与可信 Shell 别名免审，工作区外路径、未信任程序与 MCP / 桌面 / 插件工具逐次授权 | 工作区内写入免授权、外部目录一次或会话授权、未信任程序逐次审核 |
| 完全访问 | `/permissions full` / `--full-access`；不逐次审核，可访问工作区外路径和已安装程序 | 外部路径读写、拒绝回调不被调用；限制模式仍校验边界 |
| 授权 | 聊天气泡、解码参数、文件差异、未信任 Shell 的真实路径与参数；外部目录一次/会话授权，独立写入审核 | 小窗口草稿与上下文保留、并发隔离、会话隔离、真实 microVM 回调 |
| 用户问题 | `call_tool('ask_user', question=..., choices=[...], multiple=True)`；实际 SelectionPrompt / MultiSelectionPrompt，空格多选、自由回答、其他回答、取消 | 等待与拒绝测试；真实 AI 多选两个答案继续执行；终端独占避免 Live 并发 |
| 多提供商 | 16 个预设、自定义协议/Base URL/Key、自动/手动模型列表 | 本地 HTTP 验证三种发现协议和认证 |
| 思考参数 | OpenAI、DeepSeek、Qwen、OpenRouter、Anthropic、Gemini 协议；强度/预算 | 请求字段测试；Anthropic 签名往返 |
| Skills | 项目/用户目录、YAML frontmatter、完整包导入导出、启停、模型按需加载、参考文件 | 实际包导入、优先级、禁用和参考路径验证 |
| MCP | stdio/HTTP/SSE、参数化启动、环境/Headers、连接诊断、工具 Schema、资源和提示模板 | 实际 stdio MCP 与本地 Streamable HTTP |
| 会话 | 恢复、搜索、命名、分支、历史、JSON 导出 | 持久化与深复制测试 |
| 上下文 | 底栏 xk/xk，API usage / 明确估算，按模型保存容量预设和自由输入；自动总结保留原历史，手动压缩归档，AGENTS.md | 三种厂商实际用量解析、缓存输入、容量持久化、总结恢复 |
| 文件引用 | `@path` 或 `@"path with spaces"`；后台索引补全、缓存刷新保留候选、独立滚轮与点击；有界预览自动加入任务 | 越界/不存在拒绝、4000 字符限制；索引复用测试和真实候选滚轮/点击验收 |
| 计划模式 | Shift+Tab、`/plan`、`/permissions plan`；只读调研，`plan_propose` 提交 Markdown 计划；TUI 用 Markdown 审批，Web 用 HTML 审批卡片；批准后切换完全访问并继续执行 | 主机审批边界；计划持久化、刷新重放与同回合执行测试 |
| 撤销/重做 | `/diff`、`/checkpoints`、`/undo`、`/redo`；文件工具和对话快照 | 跨重启、原始 BOM/换行、多次写入、冲突预检、中断测试 |
| 主题 | `/themes night|warm|light`，持久化选择 | 相同真实布局组件生成的 HTML 渲染预览 |
| 沙箱与审计 | 实际 Hyperlight；源生成器 Schema；AST 限制；结构化结果和审计 | 实际 microVM 回调、恢复、取消、超时、输出上限 |
| 等待与预算 | 主机回调等待不计入 Python 预算；启动独立超时；总任务超时仍有效 | 实际 microVM 等待超过执行预算后继续，死循环仍被终止 |

HTML 预览在 `artifacts/tui-preview/`；是用真实 Spectre 组件渲染的测试场景，不是运行截图。`tools/tui_smoke_server.py` 提供无密钥、无外网的交互验收服务，依次生成用户问题、文件写入授权和完成回复。

## 尚未实现的范围

| 范围 | 现状 |
|---|---|
| 常驻任务 / Agent teams | 已有进程内独立任务与子 Agent；尚无常驻服务、多机团队或独立 worktree 管理 |
| 任意 MCP 自动 OAuth | 已支持专用认证插件；旧手动端点配置作为迁移兼容读取；未实现通用自动发现与动态注册 |
| LSP/ACP/IDE 集成 | 没有内置协议服务或 IDE 插件 |
| 图像/语音等附件 | 当前是文本终端；没有多模态输入流程 |
| 外部编辑器与菜单点击 | 滚轮、主输入区点击定位 / 拖选已实现；弹窗键盘选区可用，尚无外部编辑器或菜单点击 |
| 云分享/远程控制/账号体系 | 没有对应后端服务；会话仅本地导出 |
| Git/worktree/PR 产品流程 | Web 工作台提供 Git 管理浮窗（暂存、提交、AI 生成提交信息、彩色提交图、分支/贮藏、推送拉取），终端内仍可用受控 `git` Shell 别名；PR 流程由插件承担，无独立 worktree 管理 |
| 自动 Token 计费 | 上下文为字符估算，支持主动和自动摘要整理；没有跨厂商统一计费 |
| 原生安装器与签名/升级 | 本地可发布 Windows 运行目录；尚无签名安装器、自动更新或商业发布验收 |

不能以菜单数量、预设数量或测试数量宣称“完整商业产品”或完整功能对等。付费模型接口尚未逐个实测；跨文件撤销也不保证外部程序并发写入下的事务隔离。

## 重现检查

```powershell
dotnet build AllAgent.NET.slnx -c Release -p:SkipWebBuild=true
dotnet test tests/AllAgent.NET.Tests -c Release
cd src/AllAgent.NET.Web
npm run build
```

预览导出：设置 `ALLAGENT_TUI_SNAPSHOTS` 为输出目录，再运行 `FullScreenTests`。
