using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Services;

public sealed class FileCheckpoint
{
    public string Path { get; set; } = "";
    public byte[]? Before { get; set; }
    public byte[]? After { get; set; }
    public bool Completed { get; set; }
}
public sealed class WorkspaceCheckpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Label { get; set; } = "";
    public bool Undone { get; set; }
    public bool ExternalActions { get; set; }
    public List<FileCheckpoint> Files { get; set; } = [];
    public List<Message> BeforeMessages { get; set; } = [];
    public List<Message> AfterMessages { get; set; } = [];
}

/// <summary>Host-side, persisted snapshots of file-tool writes. Never snapshots arbitrary Shell or MCP effects.</summary>
public sealed partial class WorkspaceCheckpoints
{
    private readonly WorkspacePolicy _workspace;
    private readonly WorkspacePolicy _storage;
    private readonly object _gate = new();
    private WorkspaceCheckpoint? _active;
    public string WorkspaceRoot => _workspace.Root;
    public WorkspaceCheckpoints(WorkspacePolicy workspace, string dataDirectory)
    {
        _workspace = workspace;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace.Root.ToUpperInvariant())))[..24];
        var root = Path.Combine(Path.GetFullPath(dataDirectory), "checkpoints", key);
        Directory.CreateDirectory(root); _storage = new WorkspacePolicy(root);
    }
    public WorkspaceCheckpoint Begin(Guid sessionId, string label, IEnumerable<Message>? messages = null)
    {
        lock (_gate)
        {
            if (_active != null) throw new InvalidOperationException("A checkpoint turn is already active.");
            EnsureNoRevertJournal();
            var checkpoint = new WorkspaceCheckpoint { SessionId = sessionId, Label = ToolResult.Clip(label, 160), BeforeMessages = CloneMessages(messages ?? []) };
            Save(checkpoint); _active = checkpoint; return checkpoint;
        }
    }
    public void End(IEnumerable<Message>? messages = null)
    {
        lock (_gate)
        {
            if (_active == null) return;
            var checkpoint = _active; _active = null;
            checkpoint.AfterMessages = CloneMessages(messages ?? []); Save(checkpoint);
        }
    }
    public void MarkExternalAction() { lock (_gate) { if (_active is { ExternalActions: false }) { _active.ExternalActions = true; Save(_active); } } }
    public void BeforeWrite(string path)
    {
        lock (_gate)
        {
            if (_active == null) return;
            var relative = Path.GetRelativePath(_workspace.Root, _workspace.Resolve(path));
            if (_active.Files.Any(f => string.Equals(f.Path, relative, StringComparison.OrdinalIgnoreCase))) return;
            var before = Read(relative);
            _active.Files.Add(new FileCheckpoint { Path = relative, Before = before }); Save(_active);
        }
    }
    public void AfterWrite(string path)
    {
        lock (_gate)
        {
            if (_active == null) return;
            var relative = Path.GetRelativePath(_workspace.Root, _workspace.Resolve(path));
            var file = _active.Files.Single(f => string.Equals(f.Path, relative, StringComparison.OrdinalIgnoreCase));
            file.After = Read(relative); file.Completed = true; Save(_active);
        }
    }
    public IReadOnlyList<WorkspaceCheckpoint> List(Guid sessionId)
    {
        lock (_gate)
        {
            return Directory.EnumerateFiles(_storage.Root, "*.json").Where(file => file != RevertJournalPath).Select(file =>
            {
                _storage.Resolve(file);
                return JsonSerializer.Deserialize<WorkspaceCheckpoint>(File.ReadAllText(file)) ?? throw new IOException("Invalid checkpoint.");
            }).Where(c => c.SessionId == sessionId).OrderByDescending(c => c.CreatedAt).ToArray();
        }
    }
    public WorkspaceCheckpoint Restore(Guid sessionId, Guid checkpointId, bool redo)
    {
        lock (_gate)
        {
            if (_active != null) throw new InvalidOperationException("Cannot restore while an agent turn is active.");
            EnsureNoRevertJournal();
            var checkpoint = List(sessionId).Single(c => c.Id == checkpointId);
            if (checkpoint.Undone != redo) throw new InvalidOperationException(redo ? "Checkpoint was not undone." : "Checkpoint already undone.");
            var files = checkpoint.Files.Where(f => f.Completed).ToArray();
            foreach (var file in checkpoint.Files.Where(f => !f.Completed))
                if (!Equal(Read(file.Path), file.Before)) throw new IOException("检查点写入曾中断，无法安全确认文件状态：" + file.Path);
            // Validate every target and expected content before touching any file.
            foreach (var file in files)
            {
                var expected = redo ? file.Before : file.After;
                if (!Equal(Read(file.Path), expected)) throw new IOException("文件已被其他操作修改，未还原任何文件：" + file.Path);
            }
            foreach (var file in files)
            {
                var target = _workspace.Resolve(file.Path); var replacement = redo ? file.After : file.Before;
                if (replacement == null) { if (File.Exists(target)) File.Delete(target); }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllBytes(temporary, replacement); _workspace.Resolve(file.Path); File.Move(temporary, target, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            checkpoint.Undone = !redo; Save(checkpoint); return checkpoint;
        }
    }
    private byte[]? Read(string path)
    {
        var target = _workspace.Resolve(path);
        if (Directory.Exists(target)) throw new IOException("Checkpoint target is a directory: " + path);
        if (!File.Exists(target)) return null;
        return File.ReadAllBytes(target);
    }
    private static bool Equal(byte[]? left, byte[]? right) => left == null || right == null ? left == right : left.AsSpan().SequenceEqual(right);
    private void Save(WorkspaceCheckpoint checkpoint)
    {
        var path = _storage.Resolve(checkpoint.Id.ToString("N") + ".json"); var temporary = _storage.Resolve(checkpoint.Id.ToString("N") + ".tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(checkpoint)); File.Move(temporary, path, true);
    }
    private static List<Message> CloneMessages(IEnumerable<Message> messages) => JsonSerializer.Deserialize<List<Message>>(JsonSerializer.Serialize(messages))!;
}
