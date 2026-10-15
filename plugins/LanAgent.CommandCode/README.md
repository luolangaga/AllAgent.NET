# CommandCode 插件（纯 C#）

沿用 WorkBuddy 的登录、模型发现、模型选择和退出流程。仅依赖 LanAgent 已有的 .NET 8 宿主，无需 Node.js、npm、Python 或 Docker，不启动子进程、不监听本地端口。

协议移植参考 https://github.com/MAXeaglet/commandcode-proxy 的 972828aff37f07c9cb12959bc6a1f4d44aaa005a 版本（MIT），许可证随包分发。固定兼容 CLI 1.53.1 协议，实现指纹和生命周期初始化、消息、图片、思考历史、工具调用及结果转换、NDJSON/SSE 流解析与用量。采用稳定合成设备档案，不读取或上传真实硬件、用户名和项目目录。模型目录直接查询官方接口，无硬编码回退。

仅提供 LanAgent 进程内上游适配，不提供原项目的独立 OpenAI/Anthropic/Responses HTTP 服务。上游协议改变时需更新插件；失败或截断响应会报错，不自动重放已输出的对话。

构建：`dotnet pack plugins/LanAgent.CommandCode -c Release -o artifacts/plugins`

安装：`/plugins import artifacts/plugins/LanAgent.CommandCode.1.1.0.nupkg`。已安装旧版时先卸载旧插件，避免注册冲突。

启动 LanAgent 前在 PowerShell 中设置 `$env:COMMANDCODE_API_KEY='user_你的密钥'`，然后在软件输入 `/login commandcode`，验证密钥并用 Windows TokenVault（DPAPI）加密保存，按提示选择模型。不要把密钥放进聊天或命令历史。导入后可删除环境变量，下次启动使用保存的凭据。

`/ext commandcode-status` 查看状态；`/logout commandcode` 清除凭据并由宿主禁用关联模型。暂未实现额度查询或浏览器自动授权。

自动测试使用模拟上游，不消耗账号额度；真实授权和对话仍需有效 CommandCode 密钥验证。
