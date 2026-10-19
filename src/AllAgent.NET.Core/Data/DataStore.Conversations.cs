using System.Security.Cryptography;
using System.Text.Json;
using AllAgent.NET.Core.Models;

namespace AllAgent.NET.Core.Data;

public partial class DataStore
{
    public void ImportProjectData(string sourceDirectory, string workspace)
    {
        if (File.Exists(_filePath) || LegacyDataPaths.HasDatabase(Path.GetDirectoryName(_filePath)!) || !LegacyDataPaths.HasDatabase(sourceDirectory)) return;
        using var source = new DataStore(sourceDirectory, readOnly: true);
        _lock.EnterWriteLock();
        try
        {
            using var transaction = BeginTransaction();
            if (File.Exists(_filePath)) return;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            _data.Conversations = source._data.Conversations.Where(c => c.WorkspaceRoot == null ||
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(c.WorkspaceRoot)), root, StringComparison.OrdinalIgnoreCase)).ToList();
            var ids = _data.Conversations.Select(c => c.Id).ToHashSet();
            _data.PendingInputs = source._data.PendingInputs.Where(item => ids.Contains(item.ConversationId)).ToList();
            // Shared collections are referenced by every workspace store in this process;
            // replace their contents instead of swapping the reference other stores hold.
            CopyInto(Config.ApiConfigs, source._data.ApiConfigs);
            CopyInto(Config.Skills, source._data.Skills);
            CopyInto(Config.McpServerConfigs, source._data.McpServerConfigs);
            CopyInto(Config.TokenUsageStats, source._data.TokenUsageStats);
            Config.AppSettings.Clear();
            foreach (var (key, value) in source._data.AppSettings) Config.AppSettings[key] = value;
            SaveAndVerify();
        }
        finally { _lock.ExitWriteLock(); }
    }
    private readonly string _conversationDirectory;
    private readonly Dictionary<Guid, ConversationFile> _conversationFiles = [];
    private readonly HashSet<string> _retiredFiles = new(StringComparer.OrdinalIgnoreCase);
    private string? _legacySource;
    internal long ConversationWrites { get; private set; }
    internal long ConversationBytesWritten { get; private set; }
    internal sealed record ConversationFile(Guid Id, string Revision);

    private string ConversationPath(ConversationFile file)
    {
        if (file.Revision.Length != 64 || file.Revision.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Invalid conversation revision.");
        return Path.Combine(_conversationDirectory, $"{file.Id:N}-{file.Revision}.json");
    }

    private StoreData CreateSplitSnapshot(Guid? changed)
    {
        Directory.CreateDirectory(_conversationDirectory);
        var references = new List<ConversationFile>(_data.Conversations.Count);
        foreach (var conversation in _data.Conversations)
        {
            if (!_conversationFiles.TryGetValue(conversation.Id, out var reference) || changed == conversation.Id)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(conversation, JsonOptions);
                var revision = Convert.ToHexString(SHA256.HashData(bytes));
                var next = new ConversationFile(conversation.Id, revision);
                if (reference != next || !File.Exists(ConversationPath(next)))
                {
                    var path = ConversationPath(next);
                    WriteToFileAtomic(bytes, path + ".tmp");
                    File.Move(path + ".tmp", path, overwrite: true);
                    using var stream = File.OpenRead(path);
                    if (!SHA256.HashData(stream).AsSpan().SequenceEqual(Convert.FromHexString(revision)))
                        throw new IOException("Conversation verification failed.");
                    ConversationWrites++; ConversationBytesWritten += bytes.Length;
                    if (reference != null) _retiredFiles.Add(ConversationPath(reference));
                }
                reference = next; _conversationFiles[conversation.Id] = next;
            }
            references.Add(reference);
        }
        foreach (var removed in _conversationFiles.Keys.Except(references.Select(r => r.Id)).ToArray())
        { _retiredFiles.Add(ConversationPath(_conversationFiles[removed])); _conversationFiles.Remove(removed); }
        // Configuration lives in the shared manifest when one is configured.
        var split = _sharedData != null;
        return new StoreData
        {
            Version = StoreData.CurrentVersion, ConversationFiles = references,
            PendingInputs = _data.PendingInputs,
            Skills = split ? new List<Skill>() : _data.Skills,
            ApiConfigs = split ? new List<ApiConfig>() : _data.ApiConfigs,
            McpServerConfigs = split ? new List<McpServerConfig>() : _data.McpServerConfigs,
            TokenUsageStats = split ? new List<ModelUsageStats>() : _data.TokenUsageStats,
            AppSettings = split ? new Dictionary<string, string>() : _data.AppSettings
        };
    }

    private void LoadSplitConversations(StoreData snapshot)
    {
        var conversations = new List<Conversation>();
        var ids = new HashSet<Guid>();
        foreach (var reference in snapshot.ConversationFiles)
        {
            if (!ids.Add(reference.Id)) throw new InvalidDataException("Duplicate conversation reference.");
            if (_conversationFiles.TryGetValue(reference.Id, out var previous) && previous == reference &&
                _data.Conversations.FirstOrDefault(c => c.Id == reference.Id) is { } existing)
            { conversations.Add(existing); continue; }
            var bytes = File.ReadAllBytes(ConversationPath(reference));
            if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(Convert.FromHexString(reference.Revision)))
                throw new InvalidDataException("Conversation checksum mismatch.");
            var conversation = JsonSerializer.Deserialize<Conversation>(bytes, JsonOptions) ?? throw new InvalidDataException("Invalid conversation file.");
            if (conversation.Id != reference.Id) throw new InvalidDataException("Conversation identity mismatch.");
            conversations.Add(conversation);
        }
        snapshot.Conversations = conversations;
    }

    private void InitializeConversationFiles()
    {
        foreach (var reference in _data.ConversationFiles) _conversationFiles[reference.Id] = reference;
        if (Directory.Exists(_conversationDirectory))
            foreach (var path in Directory.EnumerateFiles(_conversationDirectory, "*.json")) _retiredFiles.Add(path);
    }

    private void PreserveLegacyBackup()
    {
        if (_legacySource == null) return;
        var backup = Path.Combine(Path.GetDirectoryName(_filePath)!, "allagent.legacy-v2.json.bak");
        if (!File.Exists(backup)) File.Copy(_legacySource, backup);
        _legacySource = null;
    }

    private void CleanupConversationFiles(StoreData snapshot)
    {
        // Immutable revisions plus an atomic manifest keep multi-file updates and backup recovery consistent.
        var keep = snapshot.ConversationFiles.Select(ConversationPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var manifest in new[] { _bakPath, Path.Combine(Path.GetDirectoryName(_filePath)!, "lanagent.json"), Path.Combine(Path.GetDirectoryName(_filePath)!, "lanagent.json.bak") })
            {
                if (!File.Exists(manifest)) continue;
                var backup = JsonSerializer.Deserialize<StoreData>(File.ReadAllBytes(manifest), JsonOptions);
                if (backup != null) foreach (var reference in backup.ConversationFiles) keep.Add(ConversationPath(reference));
            }
        }
        catch { return; } // Never remove revisions when the backup cannot be inspected.
        foreach (var path in _retiredFiles.ToArray())
        {
            if (keep.Contains(path)) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != 97 || name[32] != '-' || !Guid.TryParseExact(name[..32], "N", out _) || name[33..].Any(c => !Uri.IsHexDigit(c))) continue;
            try { File.Delete(path); _retiredFiles.Remove(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
