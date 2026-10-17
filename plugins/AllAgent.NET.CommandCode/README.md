# CommandCode 插件（纯 C#）

沿用 WorkBuddy 的登录、模型发现、模型选择和退出流程。仅依赖 AllAgent.NET 已有的 .NET 8 宿主，无需 Node.js、npm、Python 或 Docker，不启动子进程、不监听本地端口。

协议移植参考 https://github.com/MAXeaglet/commandcode-proxy 的 972828aff37f07c9cb12959bc6a1f4d44aaa005a 版本（MIT），许可证随包分发。固定兼容 CLI 1.53.1 协议，实现指纹和生命周期初始化、消息、图片、思考历史、工具调用及结果转换、NDJSON/SSE 流解析与用量。采用稳定合成设备档案，不读取或上传真实硬件、用户名和项目目录。模型目录直接查询官方接口，无硬编码回退。

仅提供 AllAgent.NET 进程内上游适配，不提供原项目的独立 OpenAI/Anthropic/Responses HTTP 服务。上游协议改变时需更新插件；失败或截断响应会报错，不自动重放已输出的对话。

构建：`dotnet pack plugins/AllAgent.NET.CommandCode -c Release -o artifacts/plugins`

安装：`/plugins import artifacts/plugins/AllAgent.NET.CommandCode.1.1.0.nupkg`。已安装旧版时先卸载旧插件，避免注册冲突。

登录：在软件输入 `/login commandcode`，选择 CommandCode 后**直接在输入框粘贴 `user_` 开头的 API Key**（官网账号页复制），验证通过后用 Windows TokenVault（DPAPI）加密保存，并按提示选择模型。也可在启动前设置 `$env:COMMANDCODE_API_KEY='user_你的密钥'`（PowerShell）后执行 `/login commandcode`。不要把密钥放进聊天或命令历史。导入后可删除环境变量，下次启动使用保存的凭据。

旧版 LanAgent 插件包（`lanagent-plugin.json`）与当前宿主不兼容：插件列表会明确提示重新构建并安装新版包。

`/ext commandcode-status` 查看状态与额度；模型页脚会显示"CommandCode 额度 剩余 X（月付 · 购买 · 免费）"与计划/重置时间（来自官方 `/alpha/billing/credits`、`/alpha/billing/subscriptions`、`/alpha/whoami` 接口，60 秒缓存，与官方 CLI 的 /usage 视图一致）。密钥校验、max_tokens 默认值（未设置时 64000）、模型目录视图（`?view=client`）均对齐 commandcode-proxy / 官方 CLI 行为。暂未实现 OAuth 浏览器自动授权。

自动测试使用模拟上游，不消耗账号额度；真实授权和对话仍需有效 CommandCode 密钥验证。
