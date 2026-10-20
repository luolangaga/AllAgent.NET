using System.Text.Json;

namespace AllAgent.NET.Core.Localization;

/// <summary>
/// Reads the language before a DataStore exists, so --help, attach errors and the
/// workspace fallback notice use the saved preference instead of only the system locale.
/// </summary>
public static class LanguageProbe
{
    public static string? Read(string? manifestDirectory)
    {
        if (string.IsNullOrWhiteSpace(manifestDirectory)) return null;
        try
        {
            var path = Path.Combine(manifestDirectory, "allagent.json");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("appSettings", out var settings) || settings.ValueKind != JsonValueKind.Object) return null;
            return settings.TryGetProperty(LanguageSetting.Key, out var language) && language.ValueKind == JsonValueKind.String
                ? language.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
