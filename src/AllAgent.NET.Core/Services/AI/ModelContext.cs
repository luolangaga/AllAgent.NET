using System.Globalization;
using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Core.Services.AI;

public static class ModelContext
{
    /// <summary>Unset marker kept for configs written before provider presets existed.</summary>
    private const int FactoryDefault = 100000;
    public static int Capacity(ApiConfig config) => Capacity(config, config.DefaultModel);
    public static int Capacity(ApiConfig config, string model)
    {
        if (config.ModelContextWindows.TryGetValue(model, out var manual)) return Validate(manual);
        // A limit discovered from the authenticated endpoint is the model's real window.
        if (config.ModelLimits.TryGetValue(model, out var discovered) && discovered.ContextTokens is > 0)
            return Validate(discovered.ContextTokens!.Value);
        var known = model == config.DefaultModel ? ModelCatalog.Resolve(config) : ModelCatalog.Resolve(config, model);
        return Validate(ExplicitDefault(config) ?? ProviderPreset(config, model) ?? known?.ContextTokens ?? FactoryDefault);
    }
    /// <summary>A capacity the user typed in the GUI; the untouched factory value must not shadow a provider preset.</summary>
    private static int? ExplicitDefault(ApiConfig config) => config.ContextWindowTokens is > 0 and not FactoryDefault ? config.ContextWindowTokens : null;
    private static int? ProviderPreset(ApiConfig config, string model) => config.ProviderId switch
    {
        "workbuddy" or "workbuddy-intl" => 300000,
        "codex" or "openai" when model is "gpt-6.1-sol" or "gpt-6-astra" or "gpt-6-sol" or "gpt-6-luna" => 1050000,
        _ => null
    };
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
