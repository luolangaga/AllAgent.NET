using System.Reflection;
using NuGet.Versioning;

namespace AllAgent.NET.Core.Services.Updates;

public static class UpdateVersion
{
    public static string Current(string? assemblyPath = null)
    {
        var assembly = assemblyPath == null
            ? Assembly.GetEntryAssembly() ?? typeof(UpdateVersion).Assembly
            : Assembly.LoadFrom(assemblyPath);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0].Trim();
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public static NuGetVersion Parse(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        return NuGetVersion.TryParse(text, out var version)
            ? version
            : throw new ArgumentException("无法识别的版本号：" + value);
    }

    public static bool TryParse(string? value, out NuGetVersion version)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (NuGetVersion.TryParse(text, out var parsed) && parsed != null)
        {
            version = parsed;
            return true;
        }
        version = new NuGetVersion(0, 0, 0);
        return false;
    }

    public static bool IsNewer(string candidate, string current) =>
        TryParse(candidate, out var next) && TryParse(current, out var installed) && next > installed;

    public static int Compare(string left, string right)
    {
        if (!TryParse(left, out var a)) return -1;
        if (!TryParse(right, out var b)) return 1;
        return a.CompareTo(b);
    }

    public static string Normalize(string value) => TryParse(value, out var parsed) ? parsed.ToNormalizedString() : value.Trim();
}
