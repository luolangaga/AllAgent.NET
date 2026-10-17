namespace AllAgent.NET.Core.Data;

/// <summary>Reuse existing storage in place; never merge or overwrite two user data roots.</summary>
public static class LegacyDataPaths
{
    public static bool HasDatabase(string directory) => new[] { "allagent.json", "allagent.json.bak", "lanagent.json", "lanagent.json.bak" }
        .Any(name => File.Exists(Path.Combine(directory, name)));

    public static string ProjectDirectory(string workspace)
    {
        var current = Path.Combine(workspace, ".allagent", "data");
        var legacy = Path.Combine(workspace, ".lanagent", "data");
        return HasDatabase(current) || !HasDatabase(legacy) ? current : legacy;
    }

    public static string SharedDirectory(string appData)
    {
        var current = Path.Combine(appData, "AllAgent.NET");
        // A populated current root always wins. Ambiguous roots are not silently merged.
        if (Directory.Exists(current) && Directory.EnumerateFileSystemEntries(current).Any()) return current;
        return new[] { Path.Combine(appData, "AllAgent"), Path.Combine(appData, "LanAgent") }
            .FirstOrDefault(path => Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) ?? current;
    }
}
