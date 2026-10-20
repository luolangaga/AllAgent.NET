using AllAgent.NET.Core.Data;

namespace AllAgent.NET.Core.Localization;

/// <summary>The persisted UI language preference: auto follows the operating system.</summary>
public static class LanguageSetting
{
    public const string Key = "language";
    public const string Default = "auto";
    public static readonly string[] Values = ["auto", "zh", "en"];

    public static string Current(DataStore store) =>
        store.GetSetting(Key) is { } value && Values.Contains(value) ? value : Default;

    public static UiLanguage Resolve(DataStore store) => HostText.Resolve(Current(store));

    public static void Apply(DataStore store) => HostText.Set(Resolve(store));

    public static void Save(DataStore store, string value)
    {
        var normalized = Values.Contains(value) ? value : Default;
        store.SetSetting(Key, normalized);
        HostText.Set(HostText.Resolve(normalized));
    }
}
