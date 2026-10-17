namespace AllAgent.NET.Core.Tools;

public sealed class WorkspacePolicy
{
    public string Root { get; }
    public bool FullAccess { get; }
    private readonly ExternalDirectoryPermissions? _permissions;
    private readonly AsyncLocal<string?> _requestPath = new();
    public WorkspacePolicy(string root, bool fullAccess = false, ExternalDirectoryPermissions? permissions = null)
    {
        _permissions = permissions;
        FullAccess = fullAccess;
        if (!FullAccess && root.StartsWith("\\\\", StringComparison.Ordinal)) throw new UnauthorizedAccessException("UNC workspaces are not allowed.");
        RejectAmbiguousSegments(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        Resolve(".");
    }

    public string Resolve(string path)
        => ResolveCore(path, false);
    public string ResolveForApproval(string path) => ResolveCore(path, true);
    public bool IsExternal(string full) => !ExternalDirectoryPermissions.Contains(Root, full);
    public bool HasAccess(string full) => FullAccess || !IsExternal(full) || _requestPath.Value?.Equals(full, StringComparison.OrdinalIgnoreCase) == true || _permissions?.Allows(AllAgent.NET.Core.Runtime.AgentExecutionScope.ConversationId ?? Guid.Empty, full) == true;
    internal IDisposable EnterRequest(string full)
    { var previous = _requestPath.Value; _requestPath.Value = full; return new Restore(() => _requestPath.Value = previous); }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
    private string ResolveCore(string path, bool forApproval)
    {
        if (string.IsNullOrWhiteSpace(path) || !FullAccess && path.StartsWith("\\\\", StringComparison.Ordinal) || path.Contains(':') && !Path.IsPathFullyQualified(path))
            throw new UnauthorizedAccessException("Invalid workspace path. Use '.' for the workspace root or a workspace-relative path.");
        RejectAmbiguousSegments(path);
        var full = Path.GetFullPath(path, Root);
        if (!forApproval && !HasAccess(full))
            throw new UnauthorizedAccessException("Path leaves the approved workspace. Use '.' for its root and relative paths for files inside it.");
        if (full.AsSpan((Path.GetPathRoot(full) ?? "").Length).Contains(':')) throw new UnauthorizedAccessException("Alternate data streams are not allowed.");
        // Reject junctions and symlinks, including existing ancestors of new files.
        var current = full;
        while (!string.IsNullOrEmpty(current))
        {
            if (!FullAccess && (File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Reparse points are not allowed.");
            current = Path.GetDirectoryName(current);
        }
        return full;
    }

    private static void RejectAmbiguousSegments(string path)
    {
        foreach (var segment in path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..") continue;
            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (segment.EndsWith(' ') || segment.EndsWith('.') ||
                new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3]))
                throw new UnauthorizedAccessException("Windows device names and ambiguous path suffixes are not allowed.");
        }
    }
}
