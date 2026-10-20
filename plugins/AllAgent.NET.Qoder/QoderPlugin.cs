using AllAgent.NET.Core.Extensions;

namespace AllAgent.NET.Qoder;

/// <summary>Built-in Qoder account: device login, live model catalog, streaming chat and credits.</summary>
public sealed class QoderPlugin : IAllAgentPlugin
{
    public string Id => "allagent.qoder";
    public string Name => "Qoder";
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };

    public void Register(PluginContext context)
    {
        var qoder = new QoderAccount(context.DataDirectory, http);
        context.AddAuthentication(qoder);
        context.AddProvider(qoder.ModelProvider);
        context.AddModelFooter(new("qoder-quota",
            model => string.Equals(model.ProviderId, "qoder", StringComparison.OrdinalIgnoreCase)
                || string.Equals(model.AuthenticationProviderId, "qoder", StringComparison.OrdinalIgnoreCase),
            async (_, ct) =>
            {
                try
                {
                    var quota = await qoder.QuotaAsync(ct);
                    return new ModelFooterItem[] { new("Qoder", quota.ShortSummary) };
                }
                catch (OperationCanceledException) { throw; }
                catch { return Array.Empty<ModelFooterItem>(); }
            }));
        context.AddCommand(new("qoder-status", "查看 Qoder 登录状态与模型连接", async (_, ct) =>
        {
            var status = await qoder.GetStatusAsync(ct);
            return status.LoggedIn
                ? $"Qoder · 已登录 · {status.Account}\n{status.Message}"
                : $"Qoder · 未登录\n{status.Message}";
        }));
        context.AddCommand(new("qoder-models", "列出当前 Qoder 账号可用的模型", async (_, ct) =>
        {
            var catalog = await qoder.Models.GetAsync(ct);
            var title = catalog.Live ? "Qoder 实时模型目录" : "Qoder 离线回退目录（模型接口暂不可用）";
            return title + "：\n" + string.Join("\n", catalog.Models.Select(model =>
                "  " + model.Id + " · " + model.DisplayName + (model.ContextTokens is { } tokens ? $" · {tokens} tokens" : "")));
        }));
        context.AddCommand(new("qoder-quota", "查询 Qoder 额度、套餐与到期时间", async (_, ct) =>
            (await qoder.QuotaAsync(true, ct)).Summary));
        context.AddCommand(new("qoder-activities", "查看 Qoder 官方活动与领取状态", async (_, ct) =>
            FormatActivities(await qoder.ActivitiesAsync(true, ct))));
        context.AddCommand(new("qoder-checkin", "领取当前账号可领取的 Qoder 官方活动（Qoder 无固定每日签到）", (_, ct) =>
            qoder.CheckInAsync(ct)));
        context.AddCommand(new("qoder-claim", "手动领取指定 Qoder 活动：/ext qoder-claim <activityId>", async (args, ct) =>
        {
            var id = args.FirstOrDefault()
                ?? throw new ArgumentException("用法：/ext qoder-claim <activityId>；先用 /ext qoder-activities 查看活动 ID。");
            return "✓ " + id + "：" + await qoder.ClaimActivityAsync(id, ct);
        }));
    }

    private static string FormatActivities(IReadOnlyList<QoderActivity> activities)
    {
        if (activities.Count == 0) return "当前没有 Qoder 活动。";
        var lines = activities
            .OrderByDescending(activity => activity.CanClaim)
            .ThenBy(activity => activity.Claimed)
            .ThenBy(activity => activity.EndAt ?? DateTimeOffset.MaxValue)
            .Take(50)
            .Select(activity => activity.Display);
        return "Qoder 活动：\n" + string.Join("\n", lines) +
            "\n\n/ext qoder-checkin 领取所有可领取活动；/ext qoder-claim <activityId> 手动领取。\n" +
            "Qoder 没有固定每日签到；这里只领取官方活动接口对当前账号标记为可领取的奖励。";
    }

    public ValueTask DisposeAsync()
    {
        http.Dispose();
        return ValueTask.CompletedTask;
    }
}
