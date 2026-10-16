# 模型底栏扩展

终端底部展示当前轮次所有模型请求累计的输入、输出 Token。每轮开始清零；重复的累计 usage 快照按增量计数，重试和工具循环的请求分别累计。提供商未上报时显示“等待模型上报”，不估算实际消耗。

插件在 `Register` 中注册任意多个纯文本信息项：

```csharp
context.AddModelFooter(new ModelFooterProvider(
    "my-provider-quota",
    model => model.AuthenticationProviderId == "my-provider",
    async (model, ct) => new ModelFooterItem[]
    {
        new("五小时剩余", await account.GetFiveHourRemainingAsync(ct)),
        new("每周剩余", await account.GetWeeklyRemainingAsync(ct))
    }));
```

上面的账号方法由插件实现，必须使用真实上游数据。筛选上下文包含 `ProviderId`、`ModelId` 和 `AuthenticationProviderId`，不包含 API Key。使用认证 ID 筛选可区分名称相同但来源不同的模型。注册项随插件禁用/卸载移除。

宿主在后台查询，每 60 秒刷新，整轮查询最长等待 10 秒；渲染读取缓存。切换模型立即清除旧内容，旧请求返回时不会覆盖当前模型。回调应遵守 CancellationToken；异常显示该项暂不可用。输出按纯文本处理，移除控制字符，限制每项长度及项目数。不要输出凭据或敏感信息。

WorkBuddy 国内版与国际版已注册各自的现有额度查询，展示剩余、已用额度和套餐数。Codex 内置登录已通过同一注册接口接入官方客户端使用的 `https://chatgpt.com/backend-api/wham/usage`，使用 LanAgent 自己通过 Codex 原生 PKCE OAuth 登录取得的凭据，并携带同一工作区的 `ChatGPT-Account-Id`，每分钟查询真实剩余百分比及重置时间。按窗口秒数识别五小时和每周额度，支持 `normal_model_slug` 精确匹配的模型专属额度。旧的 direct-plan OAuth 凭据保留用于原模型请求，但不会再发送给额度接口。升级后运行 `/login codex` 完成一次新授权；无需先退出。新授权使用 `/oauth/authorize`、`/oauth/token`，回调端口为 1455（被占用时用 1457）。模型请求同步路由到 `https://chatgpt.com/backend-api/codex`，按账号与工作区隔离保存凭据；续期、退出登录使用相应原生端点。登录后立即清除底栏旧缓存。401/403 显示“当前授权无法读取”，缺失窗口不推算额度。

实现依据：官方 Codex 仓库 `codex-rs/backend-client/src/client/rate_limit_resets.rs` 中的 `rate_limit_status_url`（https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/client/rate_limit_resets.rs）。该端点属于客户端后端接口，可能变化。
