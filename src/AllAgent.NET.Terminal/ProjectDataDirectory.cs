using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AllAgent.NET.Terminal;

internal static class ProjectDataDirectory
{
    internal sealed record ProjectEntry(string Workspace, string DataDirectory);

    public static void Register(string workspace, string dataDirectory, string? programDirectory = null)
    {
        var path = IndexPath(programDirectory);
        using var mutex = new Mutex(false, "allagent-project-index-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..24]);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("项目索引正在使用中。"); }
        catch (AbandonedMutexException) { }
        try
        {
            var source = File.Exists(path) ? path : Path.Combine(Path.GetDirectoryName(path)!, "lanagent-projects.json");
            var entries = Read(source);
            var entry = new ProjectEntry(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)), Path.GetFullPath(dataDirectory));
            var existing = entries.FindIndex(item => string.Equals(item.Workspace, entry.Workspace, StringComparison.OrdinalIgnoreCase));
            if (source == path && existing >= 0 && string.Equals(entries[existing].DataDirectory, entry.DataDirectory, StringComparison.OrdinalIgnoreCase)) return;
            if (existing >= 0) entries[existing] = entry; else entries.Add(entry);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(path + ".tmp", path, overwrite: true);
        }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>Known workspaces from the project index, newest data first; the caller filters dead paths.</summary>
    public static IReadOnlyList<ProjectEntry> List(string? programDirectory = null)
    {
        var path = IndexPath(programDirectory);
        var source = File.Exists(path) ? path : Path.Combine(Path.GetDirectoryName(path)!, "lanagent-projects.json");
        return Read(source);
    }

    private static List<ProjectEntry> Read(string source)
    {
        if (!File.Exists(source)) return [];
        try { return JsonSerializer.Deserialize<List<ProjectEntry>>(File.ReadAllBytes(source)) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string IndexPath(string? programDirectory)
    {
        var executable = Environment.ProcessPath;
        var actualProgramDirectory = executable != null && !string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(executable)! : AppContext.BaseDirectory;
        var directory = programDirectory ?? actualProgramDirectory;
        if (WritableDirectory.CanWrite(directory)) return Path.Combine(directory, "allagent-projects.json");
        // Program Files is read-only for standard users; keep the index with the other user data.
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllAgent.NET");
        Directory.CreateDirectory(fallback);
        return Path.Combine(fallback, "allagent-projects.json");
    }
}
