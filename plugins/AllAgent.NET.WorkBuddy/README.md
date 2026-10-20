# WorkBuddy 插件

无需额外 Python、Node、Docker 或 NuGet 包。依赖现有 AllAgent.NET.Core 宿主 SDK 和 .NET 10。
Windows 使用宿主 TokenVault（DPAPI）加密保存凭据。

构建：`dotnet pack plugins/AllAgent.NET.WorkBuddy -c Release -o artifacts/plugins`
在终端安装：`/plugins import artifacts/plugins/AllAgent.NET.WorkBuddy.1.1.0.nupkg`，确认信任 DLL。

插件也会随宿主放在 `builtin-plugins/AllAgent.NET.WorkBuddy/`，首次启动自动安装，无需手动导入。
若此前手动导入过旧版 WorkBuddy 插件，宿主会保留已安装版本；先在 `/plugins` 停用并删除旧版，再重启宿主即可换成内置版本。

## 登录与查询

- `/login workbuddy`：国内版浏览器授权，自动刷新凭据并获取模型供选择。
- `/login workbuddy-intl`：国际版。
- 模型目录：显式声明 `contextLength` / `maxOutputTokens` / `reasoningEfforts` 时按声明保存；默认上下文为 300k，可在「模型与 API」或输入框的「上下文」菜单按模型覆盖。
- 思考等级：`reasoning_effort`（`none` / `low` / `high` / `max`），只显示模型原生档位；接口没有宣告档位的模型不显示选择器。
- `/ext workbuddy-quota`、`/ext workbuddy-intl-quota`：实时查询套餐剩余额度和已用额度。
- `/ext workbuddy-status`、`/ext workbuddy-intl-status`：仅查询对应 WorkBuddy 账号状态。
- `/logout workbuddy`：清除本地凭据并禁用关联模型。

## 自动化

以下扩展命令与自动化共用同一套引擎（参考 `ardeyouxipianyi/workbuddy2api-hub` 的排程与任务链路，MIT）：

- `/ext workbuddy-automation`：立即执行一轮国内版自动巡检。
- `/ext workbuddy-intl-automation`：立即执行一轮国际版自动巡检。
- `/ext workbuddy-checkin`：国内版每日签到 + 对话活跃上报。
- `/ext workbuddy-growth`：国内版成长任务（接取 / 事件点亮 / 领奖）、连登管家与猫猫旅行。
- `/ext workbuddy-history [条数]`：查看自动化活动历史（默认 20 条，最多 1000 条）。
- `/ext workbuddy-scheduler`、`/ext workbuddy-intl-scheduler`：查看后台排程、下次执行时间与最近日志。

后台调度默认启用，按本地时间执行（宿主进程需要保持运行；错过整点时会在下次启动补跑一轮）：

| 时间 | 动作 |
|---|---|
| 每日 09:00、21:00 | 国内版签到、对话活跃上报、猫猫旅行、连登管家/抽奖；国际版每日活跃对话 |
| 每日 22:00 | 全账号 Token 保活（剩余寿命不足 2 小时自动刷新） |
| 每日 01:00 | 夜猫子任务（仅在 23:00-08:00 窗口上报） |
| 宿主启动 10 秒后 | 启动初次巡检（可用 `runOnStartup` 关闭） |

国际版每日活跃默认同时走桌面会话与网页 agent 会话；只有网页会话跑完才记为成功（与上游 README 的口径一致）。可通过 `useWebDailyChat` 关闭网页通道。

成长任务与积分任务沿用参考实现的按需触发方式（`/ext workbuddy-growth`），后台排程只执行签到、对话活跃、猫猫旅行、连登管家/抽奖与夜猫子任务。

### 配置

首次启动后可在插件数据目录创建 `automation-settings.json`（UTF-8，缺省字段使用默认值）：

```json
{
  "enabled": true,
  "runOnStartup": true,
  "useWebDailyChat": true,
  "checkinHours": [9, 21],
  "keepaliveHours": [22],
  "nightHours": [1]
}
```

- `enabled`：总开关；关闭后整点排程不执行，手动命令仍可用。
- `runOnStartup`：宿主启动后是否补跑一轮。
- `useWebDailyChat`：国际版是否走网页 agent 会话打卡。
- `checkinHours` / `keepaliveHours` / `nightHours`：本地整点小时数组，非 0-23 的值会被忽略。

### 历史与状态

- `activity_history.jsonl`：每次真实尝试一行（时间 / uid / 区域 / 任务 / 来源 / 结果 / 说明），写入前会截断并脱敏 Bearer、token、JWT、`sk-` 等凭据形态。超过约 2MB 时保留最近 5000 行。
- `automation-state.json`：记录当日签到、活跃上报、国际版活跃的成功时间，避免同一天重复执行。
- 国内版企业账号没有个人成长体系，签到、成长任务和猫猫旅行会自动跳过。

### 行为边界

- 成长任务只伪造上游已验证可推进进度的事件；需要桌面真实操作的任务（资料库、Buddy 应用等）和不可伪造任务会如实跳过并在结果里说明。
- 账号间和事件上报之间默认保持 1 秒以上间隔，避免并发触发上游风控。
- 插件在进程内直接转发 OpenAI 聊天请求至 WorkBuddy v2 接口，复用宿主的文本、推理、工具调用和用量流解析。未开放独立 HTTP 服务端口。

模型来自账号实际返回的 v3 目录（网络失败回退 v2）；不伪造模型或额度。企业计费接口尚未适配。
首次真实授权和对话需要用户账号，自动测试不消耗账号额度。

协议参考：https://github.com/ardeyouxipianyi/workbuddy2api-hub （MIT）。
