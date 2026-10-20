using System.Globalization;
using System.Text.Json;

namespace AllAgent.NET.Qoder;

/// <summary>The Qoder access token was rejected; the caller must log in again.</summary>
internal sealed class QoderAuthException() : Exception("Qoder 登录已过期，请重新 /login qoder。");

/// <summary>Normalized Qoder credits response from <c>/api/v2/quota/usage</c> plus the plan name.</summary>
internal sealed record QoderQuota(
    string? Plan,
    string? UserType,
    double? Used,
    double? Total,
    double? Remaining,
    double? Percentage,
    string Unit,
    bool Exceeded,
    DateTimeOffset? ExpiresAt,
    double? AddOnUsed,
    double? AddOnTotal,
    double? AddOnRemaining,
    string? AddOnUnit)
{
    internal string Summary
    {
        get
        {
            var parts = new List<string>();
            var plan = First(Plan, UserType);
            if (!string.IsNullOrWhiteSpace(plan)) parts.Add(plan);
            if (Total is { } total) parts.Add($"剩余 {Format(Remaining ?? 0)}/{Format(total)} {Unit}");
            else if (Remaining is { } remaining) parts.Add($"剩余 {Format(remaining)} {Unit}");
            if (Percentage is { } percentage) parts.Add($"{percentage.ToString("0.#", CultureInfo.InvariantCulture)}% 已用");
            if (Exceeded) parts.Add("额度已用尽");
            if (AddOnTotal is > 0) parts.Add($"附加额度 {Format(AddOnRemaining ?? 0)}/{Format(AddOnTotal.Value)} {AddOnUnit ?? Unit}");
            if (ExpiresAt is { } expires && expires.Year < 2100) parts.Add($"到期 {expires:yyyy-MM-dd}");
            return parts.Count == 0 ? "Qoder · 暂无额度信息" : "Qoder · " + string.Join(" · ", parts);
        }
    }

    internal string ShortSummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Plan)) parts.Add(Plan);
            if (Total is { } total) parts.Add($"剩余 {Format(Remaining ?? 0)}/{Format(total)} {Unit}");
            else if (Remaining is { } remaining) parts.Add($"剩余 {Format(remaining)} {Unit}");
            if (Exceeded) parts.Add("额度已用尽");
            return parts.Count == 0 ? "额度不可用" : string.Join(" · ", parts);
        }
    }

    internal static QoderQuota Parse(JsonElement root)
    {
        root = QoderProtocol.Unwrap(root);
        var user = Object(root, "userQuota", "user_quota", "quota");
        var totalContainer = Object(root, "totalQuota", "total_quota");
        var totalSummary = Object(totalContainer, "quotaSummary", "quota_summary");
        var sharedContainer = Object(root, "sharedQuota", "shared_quota");
        var sharedSummary = Object(sharedContainer, "quotaSummary", "quota_summary");
        if (user.ValueKind != JsonValueKind.Object) user = totalSummary;

        var used = Number(user, "used", "usedValue", "used_value");
        var total = Number(user, "total", "limitValue", "limit_value");
        var remaining = Number(user, "remaining", "remainingValue", "remaining_value");
        var percentage = Number(user, "percentage", "usagePercentage", "usage_percentage");
        var unit = QoderProtocol.ReadString(user, "unit") ?? "credits";
        if (sharedSummary.ValueKind == JsonValueKind.Object)
        {
            used = Add(used, Number(sharedSummary, "used", "usedValue", "used_value"));
            total = Add(total, Number(sharedSummary, "total", "limitValue", "limit_value"));
            remaining = Add(remaining, Number(sharedSummary, "remaining", "remainingValue", "remaining_value"));
        }
        used ??= total is { } declaredTotal && remaining is { } declaredRemaining ? Math.Max(0, declaredTotal - declaredRemaining) : 0;
        if (total is { } declared && remaining == null) remaining = Math.Max(0, declared - used.Value);
        if (percentage == null && total is > 0 && used is { } consumed) percentage = consumed / total.Value * 100;
        if (Number(root, "totalUsagePercentage", "total_usage_percentage") is { } combined) percentage = combined;
        var exceeded = QoderProtocol.ReadBool(root, "isQuotaExceeded", "is_quota_exceeded") == true
            || total is > 0 && remaining <= 0;
        if (exceeded) percentage = 100;

        var addOn = Object(root, "addOnQuota", "add_on_quota");
        var addOnUsed = Number(addOn, "used", "usedValue", "used_value");
        var addOnTotal = Number(addOn, "total", "limitValue", "limit_value");
        var addOnRemaining = Number(addOn, "remaining", "remainingValue", "remaining_value");
        if (addOnTotal is { } addOnDeclared && addOnRemaining == null) addOnRemaining = Math.Max(0, addOnDeclared - (addOnUsed ?? 0));

        return new QoderQuota(
            null,
            QoderProtocol.ReadString(root, "userType", "user_type", "planTierName"),
            used, total, remaining, percentage, unit, exceeded,
            Timestamp(root, "expiresAt", "expires_at", "expireTime", "expire_time"),
            addOnUsed, addOnTotal, addOnRemaining,
            QoderProtocol.ReadString(addOn, "unit"));
    }

    internal static QoderQuota ParsePlan(JsonElement root, QoderQuota quota) =>
        QoderProtocol.ReadString(QoderProtocol.Unwrap(root), "plan_tier_name", "planTierName", "planName", "plan_name", "tier", "name") is { } plan
            ? quota with { Plan = plan }
            : quota;

    private static JsonElement Object(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object) return value;
        return default;
    }

    private static double? Number(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        }
        return null;
    }

    private static double? Add(double? left, double? right) => left == null && right == null ? null : (left ?? 0) + (right ?? 0);

    private static DateTimeOffset? Timestamp(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number > 1_000_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number) : DateTimeOffset.FromUnixTimeSeconds(number);
            if (value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.All(char.IsAsciiDigit) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number > 1_000_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number) : DateTimeOffset.FromUnixTimeSeconds(number);
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
        }
        return null;
    }

    private static string First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>One official Qoder activity (promotional credit bundles, not a fixed daily check-in).</summary>
internal sealed record QoderActivity(
    string Id,
    string Title,
    string? Description,
    bool CanClaim,
    bool Claimed,
    bool Eligible,
    string? Reason,
    double? Limit,
    double? Used,
    double? Remaining,
    DateTimeOffset? EndAt,
    string? TagStyle)
{
    internal string Status => Claimed ? "已领取"
        : CanClaim ? "可领取"
        : TagStyle?.Equals("EXPIRED", StringComparison.OrdinalIgnoreCase) == true || EndAt is { } end && end <= DateTimeOffset.UtcNow ? "已结束"
        : "不可领取";

    internal string Display
    {
        get
        {
            var parts = new List<string> { "- [" + Status + "] " + Id + " · " + Title };
            if (!string.IsNullOrWhiteSpace(Description) && !string.Equals(Description, Title, StringComparison.Ordinal)) parts.Add(Description);
            if (Limit is { } limit) parts.Add($"剩余 {Format(Remaining ?? 0)}/{Format(limit)}");
            if (EndAt is { } end && end.Year < 2100) parts.Add($"截止 {end:yyyy-MM-dd}");
            if (!string.IsNullOrWhiteSpace(Reason) && !CanClaim && !Claimed) parts.Add(Reason);
            return string.Join(" · ", parts);
        }
    }

    internal static IReadOnlyList<QoderActivity> ParseList(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind is JsonValueKind.Array or JsonValueKind.Object) root = data;
        }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("activities", out var activities) && activities.ValueKind == JsonValueKind.Array)
            root = activities;
        if (root.ValueKind != JsonValueKind.Array) return [];
        var result = new List<QoderActivity>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = QoderProtocol.ReadString(item, "activityId", "activity_id", "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var title = QoderProtocol.ReadString(item, "activityName", "activity_name", "name", "title", "activityTitle") ?? id;
            var description = QoderProtocol.ReadString(item, "rewardDesc", "reward_desc", "description", "desc", "activityDesc", "reward");
            var limit = Number(item, "limit", "total", "limitValue", "limit_value");
            var used = Number(item, "used", "usedValue", "used_value");
            var remaining = Number(item, "remaining", "remainingValue", "remaining_value") ?? (limit is { } total ? Math.Max(0, total - (used ?? 0)) : null);
            var canClaim = QoderProtocol.ReadBool(item, "canClaim", "can_claim") == true;
            var claimed = QoderProtocol.ReadBool(item, "claimed", "isClaimed", "is_claimed") == true;
            var eligible = QoderProtocol.ReadBool(item, "eligible", "canClaim", "can_claim") == true;
            result.Add(new QoderActivity(
                id.Trim(), title, description, canClaim, claimed, eligible,
                QoderProtocol.ReadString(item, "reason", "message", "msg"),
                limit, used, remaining,
                Timestamp(item, "activityEndAt", "activity_end_at", "endAt", "end_at", "endTime", "end_time"),
                QoderProtocol.ReadString(item, "tagStyle", "tag_style", "status")));
        }
        return result;
    }

    private static double? Number(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        }
        return null;
    }

    private static DateTimeOffset? Timestamp(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number > 1_000_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number) : DateTimeOffset.FromUnixTimeSeconds(number);
            if (value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.All(char.IsAsciiDigit) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number > 1_000_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number) : DateTimeOffset.FromUnixTimeSeconds(number);
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
        }
        return null;
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
