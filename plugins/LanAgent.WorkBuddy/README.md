# WorkBuddy 插件

无需额外 Python、Node、Docker 或 NuGet 包。依赖现有 LanAgent.Core 宿主 SDK 和 .NET 8。
Windows 使用宿主 TokenVault（DPAPI）加密保存凭据。

构建：`dotnet pack plugins/LanAgent.WorkBuddy -c Release -o artifacts/plugins`
在终端安装：`/plugins import artifacts/plugins/LanAgent.WorkBuddy.1.0.0.nupkg`，确认信任 DLL。

- `/login workbuddy`：国内版浏览器授权，自动刷新凭据并获取模型供选择。
- `/login workbuddy-intl`：国际版。
- `/ext workbuddy-quota`、`/ext workbuddy-intl-quota`：实时查询套餐剩余额度和已用额度。
- `/ext workbuddy-status`、`/ext workbuddy-intl-status`：仅查询对应 WorkBuddy 账号状态。
- `/logout workbuddy`：清除本地凭据并禁用关联模型。

插件在进程内直接转发 OpenAI 聊天请求至 WorkBuddy v2 接口，复用宿主的文本、推理、工具调用和用量流解析。未开放独立 HTTP 服务端口。
模型来自账号实际返回的 v3 目录（网络失败回退 v2）；不伪造模型或额度。企业计费接口尚未适配。
首次真实授权和对话需要用户账号，自动测试不消耗账号额度。

协议参考：https://github.com/ardeyouxipianyi/workbuddy2api-hub （MIT）。
