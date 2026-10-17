using System.Globalization;
using System.Text.Json;

namespace AllAgent.NET.Core.Extensions;

/// <summary>Parses the official Codex usage response without inventing missing quota values.</summary>
public static class CodexQuota
{
    public static IReadOnlyList<ModelFooterItem> Parse(JsonElement payload, string modelId)
    {
        var items = new List<ModelFooterItem>();
        JsonElement limits = default;
        if (payload.TryGetProperty("rate_limit", out var shared)) limits = shared;
        // New payloads identify the exact model owning an additional quota bucket.
        if (payload.TryGetProperty("additional_rate_limits", out var additional) && additional.ValueKind == JsonValueKind.Array)
            foreach (var entry in additional.EnumerateArray())
                if (entry.TryGetProperty("normal_model_slug", out var model) && model.GetString() == modelId
                    && entry.TryGetProperty("rate_limit", out var specific)) { limits = specific; break; }
        AddWindow(items, limits, "primary_window");
        AddWindow(items, limits, "secondary_window");
        return items.Count == 0 ? [new("Codex 额度", "接口未返回额度窗口")] : items;
    }
    private static void AddWindow(List<ModelFooterItem> items, JsonElement limits, string name)
    {
        if (limits.ValueKind != JsonValueKind.Object || !limits.TryGetProperty(name, out var window)
            || window.ValueKind != JsonValueKind.Object) return;
        if (!window.TryGetProperty("limit_window_seconds", out var duration) || !duration.TryGetInt64(out var seconds) || seconds <= 0) return;
        var label = seconds switch { 18000 => "五小时剩余", 604800 => "每周剩余", _ => $"{seconds / 3600.0:0.#}小时剩余" };
        if (!window.TryGetProperty("used_percent", out var percent) || !percent.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0 || used > 100)
        { items.Add(new(label, "未知")); return; }
        var value = (100 - used).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        if (window.TryGetProperty("reset_at", out var reset) && reset.TryGetInt64(out var timestamp))
        {
            try { value += " · " + DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("MM-dd HH:mm") + " 重置"; }
            catch (ArgumentOutOfRangeException) { }
        }
        items.Add(new(label, value));
    }
}
