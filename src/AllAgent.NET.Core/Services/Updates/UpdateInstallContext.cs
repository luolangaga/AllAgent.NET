using System.Runtime.InteropServices;
using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Core.Services.Updates;

public sealed record UpdateInstallContext(
    UpdateInstallKind Kind,
    string InstallDirectory,
    string Architecture,
    string ArchitectureLabel,
    string? NuGetPackageId,
    string? NuGetToolPath,
    string? DotNetPath)
{
    public static UpdateInstallContext Detect(string? baseDirectory = null)
    {
        var directory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var architecture = UpdateArchitecture.Host;
        var label = UpdateArchitecture.Label;

        if (Environment.GetEnvironmentVariable("ALLAGENT_UPDATE_INSTALL_KIND")?.Trim().ToLowerInvariant() is { Length: > 0 } forced)
        {
            var forcedKind = forced switch
            {
                "nuget" or "tool" => UpdateInstallKind.NuGet,
                "installer" or "setup" => UpdateInstallKind.Installer,
                _ => UpdateInstallKind.Unknown,
            };
            if (forcedKind == UpdateInstallKind.NuGet)
                return NuGet(directory, architecture, label);
            if (forcedKind == UpdateInstallKind.Installer)
                return new(forcedKind, directory, architecture, label, null, null, FindDotnet());
            return new(UpdateInstallKind.Unknown, directory, architecture, label, null, null, FindDotnet());
        }

        var store = FindStore(directory);
        if (store is { } value)
            return NuGet(directory, architecture, label, value.PackageId, value.ToolPath);

        if (IsInstaller(directory))
            return new(UpdateInstallKind.Installer, directory, architecture, label, null, null, FindDotnet());

        // Source-tree and manually copied directories are not self-updatable. Only the
        // installer and the NuGet tool carry a known update/rollback contract.
        return new(UpdateInstallKind.Unknown, directory, architecture, label, null, null, FindDotnet());
    }

    public static string? FindDotnet() =>
        EnvironmentDoctor.FindExecutable("dotnet.exe") ??
        EnvironmentDoctor.FindExecutable("dotnet");

    private static UpdateInstallContext NuGet(string directory, string architecture, string label, string? packageId = null, string? toolPath = null)
    {
        var store = FindStore(directory);
        packageId ??= store?.PackageId ?? DefaultNuGetPackageId(architecture);
        toolPath ??= store?.ToolPath;
        return new(UpdateInstallKind.NuGet, directory, architecture, label, packageId, toolPath, FindDotnet());
    }

    public static string DefaultNuGetPackageId(string architecture) =>
        architecture == "win-arm64" ? "AllAgent.NET.Terminal.arm64" : "AllAgent.NET.Terminal";

    private sealed record StoreInfo(string PackageId, string? ToolPath);

    private static StoreInfo? FindStore(string directory)
    {
        var segments = directory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 2; index++)
        {
            if (!segments[index].Equals(".store", StringComparison.OrdinalIgnoreCase)) continue;
            var package = segments[index + 1];
            var toolDirectory = string.Join(Path.DirectorySeparatorChar, segments[..index]);
            if (toolDirectory.Length == 0) continue;
            // The default global tool directory is better addressed with --global; a custom
            // --tool-path install keeps its explicit directory.
            var defaultTools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
            if (Path.GetFullPath(toolDirectory).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(defaultTools).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                toolDirectory = null;
            return new StoreInfo(package, toolDirectory);
        }
        return null;
    }

    private static bool IsInstaller(string directory) =>
        File.Exists(Path.Combine(directory, "unins000.exe")) ||
        File.Exists(Path.Combine(directory, "unins000.dat"));
}
