using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Services;

/// <summary>One workspace-relative path offered by a composer's `@` picker.</summary>
public sealed record WorkspaceFileEntry(string Path, string Name, bool IsDirectory);

/// <summary>
/// Lists workspace files for `@`-reference completion. The directory the user is
/// typing in is listed first; a bare name additionally searches the tree so
/// `@Chat.tsx` finds `src/views/Chat.tsx`. Build output and VCS metadata are skipped.
/// </summary>
public static class WorkspaceFiles
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", ".vscode", "node_modules", "bin", "obj",
        "dist", "build", "out", "target", ".venv", "venv", "__pycache__", ".next", ".turbo", "coverage",
    };

    private const int ScanLimit = 20_000;
    private const int DepthLimit = 8;

    public static IReadOnlyList<WorkspaceFileEntry> Match(string workspace, string? query, int limit = 40)
    {
        limit = Math.Clamp(limit, 1, 200);
        var normalized = (query ?? string.Empty).Replace('\\', '/').TrimStart();
        if (normalized.StartsWith('@')) normalized = normalized[1..];
        var slash = normalized.LastIndexOf('/');
        var directoryPart = slash >= 0 ? normalized[..slash] : string.Empty;
        var namePart = slash >= 0 ? normalized[(slash + 1)..] : normalized;

        WorkspacePolicy policy;
        string directory;
        try
        {
            policy = new WorkspacePolicy(workspace);
            directory = policy.Resolve(directoryPart.Length == 0 ? "." : directoryPart);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or ArgumentException)
        {
            return [];
        }
        if (!Directory.Exists(directory)) return [];

        var results = new List<WorkspaceFileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.TrimEndingDirectorySeparator(policy.Root);
        foreach (var entry in Enumerate(directory))
        {
            if (!entry.Name.StartsWith(namePart, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Relative(root, entry.FullName);
            if (seen.Add(path)) results.Add(new(path, entry.Name, entry.IsDirectory));
        }
        if (slash < 0 && namePart.Length > 0) Search(root, namePart, results, seen, limit);
        return results
            .OrderBy(entry => entry.IsDirectory ? 0 : 1)
            .ThenBy(entry => entry.Path.Count(c => c == '/'))
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    /// <summary>Bounded depth-first search for a file name the user typed without a directory.</summary>
    private static void Search(string root, string namePart, List<WorkspaceFileEntry> results, HashSet<string> seen, int limit)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var scanned = 0;
        while (pending.Count > 0 && scanned < ScanLimit && results.Count < limit)
        {
            var (current, depth) = pending.Pop();
            foreach (var entry in Enumerate(current))
            {
                scanned++;
                if (entry.IsDirectory)
                {
                    if (depth < DepthLimit && !SkippedDirectories.Contains(entry.Name)) pending.Push((entry.FullName, depth + 1));
                    continue;
                }
                if (!entry.Name.StartsWith(namePart, StringComparison.OrdinalIgnoreCase)) continue;
                var path = Relative(root, entry.FullName);
                if (seen.Add(path)) results.Add(new(path, entry.Name, false));
            }
        }
    }

    private static string Relative(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full);
        return relative.Replace('\\', '/');
    }

    /// <summary>Immediate children without junctions, symlinks or unreadable entries.</summary>
    private static IReadOnlyList<Child> Enumerate(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateFileSystemInfos()
                .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                .Select(entry => new Child(entry.Name, entry.FullName, (entry.Attributes & FileAttributes.Directory) != 0))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private sealed record Child(string Name, string FullName, bool IsDirectory);
}
