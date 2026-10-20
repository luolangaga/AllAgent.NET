using System.Runtime.InteropServices;

namespace AllAgent.NET.Core.Services.Updates;

public enum UpdateSource
{
    Auto,
    GitCode,
    GitHub,
    NuGet,
}

public enum UpdateChannel
{
    Stable,
    Preview,
}

public enum UpdateInstallKind
{
    Unknown,
    NuGet,
    Installer,
}

public enum UpdateSignatureStatus
{
    NotApplicable,
    Valid,
    Unsigned,
    Invalid,
}

public sealed record UpdateAsset(string Name, string Url, long? Size = null, string? Digest = null);

public sealed record UpdateRelease(
    string Version,
    string Tag,
    string Name,
    string Notes,
    string PageUrl,
    UpdateSource Source,
    IReadOnlyList<UpdateAsset> Assets,
    bool Prerelease)
{
    public UpdateAsset? FindAsset(params string[] suffixes) =>
        Assets.FirstOrDefault(asset => suffixes.Any(suffix => asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
}

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    UpdateSource Source,
    UpdateInstallKind InstallKind,
    string Architecture,
    UpdateRelease? Release,
    string? Error,
    DateTimeOffset CheckedAt,
    bool FromCache)
{
    public string? ReleaseUrl => Release?.PageUrl;
}

public sealed record UpdateSettings(
    UpdateSource Source = UpdateSource.Auto,
    UpdateChannel Channel = UpdateChannel.Stable,
    bool AutoCheck = true,
    bool AutoInstall = false,
    bool RequireSignature = false,
    bool RequireChecksum = true,
    int CheckIntervalHours = 24)
{
    public static UpdateSettings FromSettings(Func<string, string?> get)
    {
        var defaults = new UpdateSettings();
        return new UpdateSettings(
            ParseSource(get("update:source")) ?? defaults.Source,
            ParseChannel(get("update:channel")) ?? defaults.Channel,
            ParseBool(get("update:autoCheck")) ?? defaults.AutoCheck,
            ParseBool(get("update:autoInstall")) ?? defaults.AutoInstall,
            ParseBool(get("update:requireSignature")) ?? defaults.RequireSignature,
            ParseBool(get("update:requireChecksum")) ?? defaults.RequireChecksum,
            ParseInt(get("update:checkIntervalHours")) ?? defaults.CheckIntervalHours);
    }

    public static UpdateSettings FromEnvironment()
    {
        var defaults = new UpdateSettings();
        return new UpdateSettings(
            ParseSource(Environment.GetEnvironmentVariable("ALLAGENT_UPDATE_SOURCE")) ?? defaults.Source,
            ParseChannel(Environment.GetEnvironmentVariable("ALLAGENT_UPDATE_CHANNEL")) ?? defaults.Channel,
            ParseBool(Environment.GetEnvironmentVariable("ALLAGENT_UPDATE_CHECK")) ?? defaults.AutoCheck,
            ParseBool(Environment.GetEnvironmentVariable("ALLAGENT_UPDATE_AUTO_INSTALL")) ?? defaults.AutoInstall,
            ParseBool(Environment.GetEnvironmentVariable("ALLAGENT_UPDATE_REQUIRE_SIGNATURE")) ?? defaults.RequireSignature,
            ParseBool(Environment.GetEnvironmentVariable("ALLAGENT_UPDATE_REQUIRE_CHECKSUM")) ?? defaults.RequireChecksum,
            24);
    }

    public void Save(Action<string, string> set)
    {
        set("update:source", Source.ToString().ToLowerInvariant());
        set("update:channel", Channel == UpdateChannel.Preview ? "pre" : "stable");
        set("update:autoCheck", AutoCheck ? "on" : "off");
        set("update:autoInstall", AutoInstall ? "on" : "off");
        set("update:requireSignature", RequireSignature ? "on" : "off");
        set("update:requireChecksum", RequireChecksum ? "on" : "off");
    }

    public static UpdateSource? ParseSource(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "auto" => UpdateSource.Auto,
        "gitcode" => UpdateSource.GitCode,
        "github" => UpdateSource.GitHub,
        "nuget" => UpdateSource.NuGet,
        _ => null,
    };

    public static UpdateChannel? ParseChannel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "stable" or "release" => UpdateChannel.Stable,
        "pre" or "preview" or "prerelease" or "beta" => UpdateChannel.Preview,
        _ => null,
    };

    private static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "on" or "yes" or "enabled" => true,
        "0" or "false" or "off" or "no" or "disabled" => false,
        _ => null,
    };

    private static int? ParseInt(string? value) => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
}

public sealed record UpdateInstallOptions(
    UpdateSettings Settings,
    bool Force = false,
    bool AllowUnsigned = true,
    bool Restart = true,
    string[]? RestartArguments = null,
    string? WorkingDirectory = null,
    bool GuiLauncher = false);

public sealed record UpdateInstallResult(
    bool Started,
    string Message,
    string? LogPath = null,
    string? Version = null,
    string? PreviousVersion = null,
    string? PlanPath = null,
    string? Error = null);

public static class UpdateArchitecture
{
    public static string Host =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => "win-x64",
        };

    public static string Label => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };
}
