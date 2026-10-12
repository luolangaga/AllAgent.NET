namespace LanAgent.Core.Tools;

public static class ShellExecutablePolicy
{
    public static bool TryResolve(IReadOnlyDictionary<string, string> allowlist, string requested, out string executable, bool fullAccess = false)
    {
        executable = "";
        var candidates = AllowlistedCandidates(allowlist, requested);
        if (candidates.Length == 0 && fullAccess)
        {
            var resolved = ResolveInstalled(requested);
            if (resolved != null) candidates = [resolved];
        }
        if (candidates.Length != 1 || !Path.IsPathFullyQualified(candidates[0]) || !File.Exists(candidates[0])) return false;
        executable = Path.GetFullPath(candidates[0]);
        return true;
    }

    /// <summary>Resolves an installed program for execution. Explicit aliases are preapproved; all other programs need a per-call approval.</summary>
    public static bool TryResolveForExecution(IReadOnlyDictionary<string, string> allowlist, string requested,
        string? trustedPowerShell, out string executable, out bool preapproved)
    {
        executable = "";
        preapproved = false;
        if (string.IsNullOrWhiteSpace(requested) || requested.Contains('\0')) return false;

        var candidates = AllowlistedCandidates(allowlist, requested);
        if (candidates.Length == 1 && IsValid(candidates[0]))
        {
            executable = Path.GetFullPath(candidates[0]);
            preapproved = true;
            return true;
        }

        // EnvironmentDoctor may have found PowerShell 7.6 by its standard install path even when PATH is stale.
        if (trustedPowerShell != null && (requested.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || requested.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)) && IsValid(trustedPowerShell))
        {
            executable = Path.GetFullPath(trustedPowerShell);
            return true;
        }

        var resolved = ResolveInstalled(requested);
        if (resolved == null || !IsValid(resolved)) return false;
        executable = Path.GetFullPath(resolved);
        return true;
    }

    private static string[] AllowlistedCandidates(IReadOnlyDictionary<string, string> allowlist, string requested) =>
        allowlist.Where(entry => string.Equals(entry.Key, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Value, requested, StringComparison.OrdinalIgnoreCase) ||
            !requested.Contains('/') && !requested.Contains('\\') && string.Equals(Path.GetFileName(entry.Value), requested, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string? ResolveInstalled(string requested)
    {
        if (Path.IsPathFullyQualified(requested)) return requested;
        // Paths relative to the current directory are ambiguous and must be made explicit by the caller.
        if (requested.Contains('/') || requested.Contains('\\')) return null;
        string[] names = Path.HasExtension(requested)
            ? [requested]
            : OperatingSystem.IsWindows()
                ? new[] { requested + ".exe", requested + ".com", requested + ".ps1", requested + ".cmd", requested + ".bat" }
                : new[] { requested };
        return names.Select(LanAgent.Core.Runtime.EnvironmentDoctor.FindExecutable).FirstOrDefault(path => path != null);
    }

    private static bool IsValid(string path) => Path.IsPathFullyQualified(path) && File.Exists(path);
}
