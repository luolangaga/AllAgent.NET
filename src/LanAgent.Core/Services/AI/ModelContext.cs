using System.Globalization;
using LanAgent.Core.Models;

namespace LanAgent.Core.Services.AI;

public static class ModelContext
{
    public static int Capacity(ApiConfig config) => Validate(config.ModelContextWindows.GetValueOrDefault(config.DefaultModel, config.ContextWindowTokens));
    public static int Validate(int value) => value is >= 1024 and <= 10000000 ? value : throw new ArgumentException("模型上下文容量范围为 1024–10000000 Token。");
    public static int Parse(string text)
    {
        var value = text.Trim(); var multiplier = 1m;
        if (value.EndsWith("k", StringComparison.OrdinalIgnoreCase)) { multiplier = 1000; value = value[..^1]; }
        else if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase)) { multiplier = 1000000; value = value[..^1]; }
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number * multiplier != decimal.Truncate(number * multiplier) || number * multiplier is < 1024 or > 10000000)
            throw new ArgumentException("请输入 Token 容量，例如 100k、200k、400k、1M 或 131072。");
        return Validate((int)(number * multiplier));
    }
    public static string Format(int tokens) => (tokens / 1000d).ToString(tokens % 1000 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + "k";
}
