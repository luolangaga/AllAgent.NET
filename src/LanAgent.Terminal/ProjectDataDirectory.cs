using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanAgent.Terminal;

internal static class ProjectDataDirectory
{
    internal sealed record ProjectEntry(string Workspace, string DataDirectory);

    public static void Register(string workspace, string dataDirectory, string? programDirectory = null)
    {
        var executable = Environment.ProcessPath;
        var actualProgramDirectory = executable != null && !string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(executable)! : AppContext.BaseDirectory;
        var path = Path.Combine(programDirectory ?? actualProgramDirectory, "lanagent-projects.json");
        using var mutex = new Mutex(false, "lanagent-project-index-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..24]);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("项目索引正在使用中。"); }
        catch (AbandonedMutexException) { }
        try
        {
            var entries = File.Exists(path) ? JsonSerializer.Deserialize<List<ProjectEntry>>(File.ReadAllBytes(path)) ?? [] : [];
            var entry = new ProjectEntry(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)), Path.GetFullPath(dataDirectory));
            var existing = entries.FindIndex(item => string.Equals(item.Workspace, entry.Workspace, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0 && string.Equals(entries[existing].DataDirectory, entry.DataDirectory, StringComparison.OrdinalIgnoreCase)) return;
            if (existing >= 0) entries[existing] = entry; else entries.Add(entry);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(path + ".tmp", path, overwrite: true);
        }
        finally { mutex.ReleaseMutex(); }
    }
}
