using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllAgent.NET.Core.Services.Updates;

public sealed record UpdateState
{
    public DateTimeOffset? LastCheckUtc { get; init; }
    public string? LatestVersion { get; init; }
    public string? LatestSource { get; init; }
    public bool? LatestPrerelease { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? PreviousVersion { get; init; }
    public string? LastInstalledVersion { get; init; }
    public DateTimeOffset? LastUpdateUtc { get; init; }
    public string? LastResult { get; init; }
    public string? LastError { get; init; }
    public string? LastLogPath { get; init; }
}

public sealed class UpdateStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Directory { get; }
    public string Path { get; }

    public UpdateStateStore(string directory)
    {
        Directory = System.IO.Path.GetFullPath(directory);
        Path = System.IO.Path.Combine(Directory, "update-state.json");
    }

    public UpdateState Load()
    {
        try
        {
            if (!File.Exists(Path)) return new UpdateState();
            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(Path), Options) ?? new UpdateState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UpdateState();
        }
    }

    public void Save(UpdateState state)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
        File.Move(temporary, Path, overwrite: true);
    }

    public UpdateState Update(Func<UpdateState, UpdateState> change)
    {
        var next = change(Load());
        Save(next);
        return next;
    }
}

public sealed class UpdateWorkDirectory
{
    private UpdateWorkDirectory(string root, string directory)
    {
        Root = root;
        Directory = directory;
    }

    public string Root { get; }
    public string Directory { get; }
    public string PlanPath => System.IO.Path.Combine(Directory, "plan.json");
    public string ScriptPath => System.IO.Path.Combine(Directory, "apply-update.ps1");
    public string LogPath => System.IO.Path.Combine(Directory, "update.log");
    public string ResultPath => System.IO.Path.Combine(Directory, "result.json");
    public string ArtifactPath(string name) => System.IO.Path.Combine(Directory, name);
    public string HealthMarkerPath => System.IO.Path.Combine(Directory, "healthy.json");

    public static UpdateWorkDirectory Create(string version)
    {
        var root = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AllAgent.NET",
            "updates");
        var directory = System.IO.Path.Combine(root, version + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        System.IO.Directory.CreateDirectory(directory);
        return new UpdateWorkDirectory(root, directory);
    }

    public static void Cleanup(string root, TimeSpan olderThan)
    {
        try
        {
            if (!System.IO.Directory.Exists(root)) return;
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var directory in System.IO.Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (System.IO.Directory.GetLastWriteTimeUtc(directory) < cutoff)
                        System.IO.Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
