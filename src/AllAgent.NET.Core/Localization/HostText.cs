using System.Globalization;

namespace AllAgent.NET.Core.Localization;

public enum UiLanguage { Zh, En }

/// <summary>
/// Process-wide language for host-composed text. The setting itself lives in the shared
/// manifest; this type only resolves it and serves inline bilingual pairs.
/// </summary>
public static class HostText
{
    private static readonly object Gate = new();
    private static UiLanguage _language = Resolve(null);

    public static UiLanguage Language => _language;
    public static string ResolvedCode => _language == UiLanguage.Zh ? "zh" : "en";
    public static string CultureTag => _language == UiLanguage.Zh ? "zh-CN" : "en-US";
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(CultureTag);
    public static event Action? Changed;

    public static void Set(UiLanguage language)
    {
        lock (Gate)
        {
            if (_language == language) return;
            _language = language;
        }
        Changed?.Invoke();
    }

    public static UiLanguage Resolve(string? setting, CultureInfo? culture = null)
    {
        if (TryParse(Environment.GetEnvironmentVariable("ALLAGENT_LANGUAGE"), out var overridden)) return overridden;
        if (TryParse(setting, out var explicitLanguage)) return explicitLanguage;
        var current = culture ?? CultureInfo.CurrentUICulture;
        return current.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
            current.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.Zh
            : UiLanguage.En;
    }

    public static string Pick(string zh, string en) => _language == UiLanguage.Zh ? zh : en;

    public static IDisposable Scope(UiLanguage language)
    {
        var previous = _language;
        Set(language);
        return new Restore(previous);
    }

    private static bool TryParse(string? value, out UiLanguage language)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "zh": language = UiLanguage.Zh; return true;
            case "en": language = UiLanguage.En; return true;
            default: language = UiLanguage.Zh; return false;
        }
    }

    private sealed class Restore(UiLanguage previous) : IDisposable
    {
        public void Dispose() => Set(previous);
    }
}
