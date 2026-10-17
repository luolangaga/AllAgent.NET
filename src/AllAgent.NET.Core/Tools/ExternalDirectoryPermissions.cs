using System.Collections.Concurrent;

namespace AllAgent.NET.Core.Tools;

/// <summary>Explicit directory grants scoped to an agent conversation, never written as permanent global permissions.</summary>
public sealed class ExternalDirectoryPermissions
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _sessions = new();
    public IReadOnlyList<string> List(Guid session) => _sessions.TryGetValue(session, out var paths) ? paths.Keys.Order().ToArray() : [];
    public void Allow(Guid session, string directory) => _sessions.GetOrAdd(session, _ => new(StringComparer.OrdinalIgnoreCase))[Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))] = 0;
    public void Clear(Guid session) => _sessions.TryRemove(session, out _);
    internal bool Allows(Guid session, string path) => List(session).Any(directory => Contains(directory, path));
    internal static bool Contains(string directory, string path) => path.Equals(directory, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
