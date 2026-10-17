using AllAgent.NET.Core.Data;

namespace AllAgent.NET.Terminal;

internal static class CachePreference
{
    internal const string TtlKey = "cache:ttl";
    internal const string RetentionKey = "cache:retention";
    internal static readonly string[] TtlLevels = ["default", "1h"];
    internal static readonly string[] RetentionLevels = ["default", "24h"];
    private const string Usage = "用法 /cache [status|ttl default|1h|retention default|24h]";

    internal static string CurrentTtl(DataStore store) => store.GetSetting(TtlKey) == "1h" ? "1h" : "default";
    internal static string CurrentRetention(DataStore store) => store.GetSetting(RetentionKey) == "24h" ? "24h" : "default";

    internal static string TtlLabel(string level) => level == "1h"
        ? "1h · Anthropic 缓存跨 5 分钟停顿仍可命中（写入按 2x 计费）"
        : "default · 提供商默认（Anthropic 5 分钟）";

    internal static string RetentionLabel(string level) => level == "24h"
        ? "24h · OpenAI 官方端点显式延长保留（仅 gpt-4.1 / gpt-5 系列旧机型）"
        : "default · 提供商默认（非 ZDR 账号通常已是 24h）";

    internal static void Execute(DataStore store, TerminalUi ui, IReadOnlyList<string> args)
    {
        if (args.Count > 2) throw new ArgumentException(Usage);
        var scope = args.FirstOrDefault()?.ToLowerInvariant();
        if (scope is null or "status")
        {
            if (scope == null && ui.Interactive && !ui.Json)
            {
                var currentTtl = CurrentTtl(store);
                var nextTtl = ManagementUi.Choose("缓存 TTL · 当前 " + currentTtl, TtlLevels,
                    value => value + " · " + TtlLabel(value) + (value == currentTtl ? "（当前）" : ""));
                var currentRetention = CurrentRetention(store);
                var nextRetention = ManagementUi.Choose("缓存保留 · 当前 " + currentRetention, RetentionLevels,
                    value => value + " · " + RetentionLabel(value) + (value == currentRetention ? "（当前）" : ""));
                store.SetSetting(TtlKey, nextTtl);
                store.SetSetting(RetentionKey, nextRetention);
                ManagementUi.Success(ui, "缓存策略已保存：TTL " + nextTtl + " · 保留 " + nextRetention);
                return;
            }
            var ttl = CurrentTtl(store);
            var retention = CurrentRetention(store);
            ManagementUi.Output(ui, new { ttl, retention, ttlDescription = TtlLabel(ttl), retentionDescription = RetentionLabel(retention) },
                () => TerminalOutput.Write(new Spectre.Console.Text("缓存 TTL：" + TtlLabel(ttl) + "\n缓存保留：" + RetentionLabel(retention) + "\n" + Usage + "\n")));
            return;
        }
        var value = args.ElementAtOrDefault(1)?.ToLowerInvariant();
        switch (scope)
        {
            case "ttl":
                value = value switch { null => null, "default" or "5m" => "default", "1h" => "1h", _ => value };
                if (value is null || !TtlLevels.Contains(value)) throw new ArgumentException(Usage);
                store.SetSetting(TtlKey, value);
                ManagementUi.Success(ui, "缓存 TTL 已保存：" + TtlLabel(value));
                return;
            case "retention":
                value ??= "";
                if (!RetentionLevels.Contains(value)) throw new ArgumentException(Usage);
                store.SetSetting(RetentionKey, value);
                ManagementUi.Success(ui, "缓存保留已保存：" + RetentionLabel(value));
                return;
            default:
                throw new ArgumentException(Usage);
        }
    }
}
