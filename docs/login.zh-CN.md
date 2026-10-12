# 账号登录与模型连接

在聊天界面输入 `/login`，或者按 F1 选择“账号登录”。选择提供商，登录、查看状态或退出。浏览器或设备码等待期间，Ctrl+C 可取消；全屏界面也支持 Esc，关闭浏览器不会自动视为授权成功。插件通过相同面板提供自己的入口。

原 `/auth` 及添加 OAuth 端点的用户向导已移除；旧配置文件只用于迁移兼容。

| 入口 | 登录方式 | 当前模型能力 |
|---|---|---|
| `/login codex` | 官方 Continue with ChatGPT 浏览器授权；不用填写 Client ID 或 Key | 自动获取账号模型目录，选择模型并建立 Responses 配置；实际推理由首次请求确认 |
| `/login 插件ID` | 对应 .NET 认证插件实现 | 由插件的认证和模型提供商共同决定 |

`/login status` 查看全部入口，`/logout 提供商ID` 退出。独立命令行也支持 `lanagent login codex`、`lanagent login status --json` 和 `lanagent logout codex`。Copilot 和 Claude Code 的内置认证登录入口已移除；已有 Anthropic API Key 可继续通过 `/providers` 配置。

## Codex / ChatGPT

该入口按官方 Sign in with ChatGPT 本地客户端规范实现。首次授权使用 `dynamic_agent_client`，返回后必须取得自己的已签发 Client ID。后续登录复用该注册及稳定主机标识；回调监听只绑定 `127.0.0.1`，路径固定为 `/auth/callback`，本次授权与兑换使用相同端口。校验随机 state、PKCE S256、nonce、公钥签名、issuer、audience、有效期和账号身份；缺少 `chatgpt.tokens.use.direct` 权限不会保存为可推理账号。

多账号或工作区使用 `/login codex new` 添加；交互面板可选择已保存账号。每个注册保留独立凭据与账号映射，不能把一个账号的令牌和另一个账号的 Client ID 混用。切换后重新获取账号模型目录，失败时禁用旧目录。一个账号的加密思考内容不会被重放给另一个账号。

登录完成后显示当前账号允许展示的模型，保留服务端顺序并使用真实模型 ID。选择模型后使用 `/model` 切换，也可通过 `/effort` 调整模型支持的思考级别。系统仍只向模型提供命名空间内的 `execute_code`；所有系统操作经 Python `call_tool`、C# 审批和实际 Hyperlight 执行。

推理走公开 `https://api.openai.com/v1/responses`，不使用 ChatGPT 私有 backend-api。设置 `store:false`、`stream:true`，每次发送完整必要历史。函数调用在 `response.completed` 后才交给引擎执行；失败、额度不足和流中断不视为成功。令牌刷新在账号锁内完成，旋转的访问与刷新令牌一起原子保存。

注销尝试撤销服务器的可刷新会话，并清除本地访问、刷新和 ID token；保留注册映射与主机标识，供以后重新登录。若网络失败，显示“本地已退出，服务端撤销未确认”，可在 ChatGPT 设置中断开应用。

这是本地应用的接入实现。商业分发或远程托管需要遵守 OpenAI 对对应接入类别的要求；不能把本地流程测试等同于取得商业服务批准。当前没有替用户登录真实账号，真实账号授权、目录和推理仍需在本机完成。[官方注册规范](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)、[模型规范](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)、[接入范围](https://developers.openai.com/siwc/token-sharing-open-source)

## 认证插件

安装带 `LanAgent` 标签的 .NET 插件后，插件的 `IAuthenticationProvider` 自动进入 `/login` 面板。服务专用认证应在插件代码中封装，用户不填写 OAuth 端点。内置 ID `codex` 为宿主保留；其他 ID 请使用插件前缀。模型与 HTTP MCP 可引用该认证 ID，凭据按提供商的目标来源策略处理。接口、示例、NuGet 打包与信任边界见 [插件开发文档](plugins.zh-CN.md)。
