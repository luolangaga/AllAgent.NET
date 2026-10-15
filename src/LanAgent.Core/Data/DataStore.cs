using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;

namespace LanAgent.Core.Data;

/// <summary>
/// JSON manifest plus immutable per-conversation revisions. Thread-safe,
/// atomic commits with fsync, checksum verification and backup recovery.
/// </summary>
public partial class DataStore : IDisposable
{
    private readonly string _filePath;
    private readonly string _bakPath;
    private readonly string _tmpPath;
    private readonly string _logPath;
    private readonly ReaderWriterLockSlim _lock = new();
    private StoreData _data;
    private bool _disposed;
    private readonly bool _readOnly;
    private byte[]? _savedHash;
    private (long Length, DateTime Modified)? _savedStamp;
    internal long PersistenceWrites { get; private set; }

    public DataStore() : this(null) { }

    public DataStore(string? dataDirectory) : this(dataDirectory, false) { }

    private DataStore(string? dataDirectory, bool readOnly)
    {
        _readOnly = readOnly;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = dataDirectory ?? Path.Combine(appData, "LanAgent");
        if (!readOnly) Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "lanagent.json");
        _bakPath = Path.Combine(dir, "lanagent.json.bak");
        _tmpPath = Path.Combine(dir, "lanagent.json.tmp");
        _logPath = Path.Combine(dir, "lanagent.log");
        _conversationDirectory = Path.Combine(dir, "conversations");
        _data = new StoreData();

        Log($"[DataStore] Starting up. File={_filePath}");
        using var startup = new StorageTransaction(_filePath);
        Load();
        InitializeConversationFiles();
        Log($"[DataStore] Loaded. Conversations={_data.Conversations.Count}, Skills={_data.Skills.Count}, ApiConfigs={_data.ApiConfigs.Count}, Settings={_data.AppSettings.Count}");
    }

    // ── Logging ────────────────────────────────────────────────────────────

    private void Log(string message)
    {
        if (_readOnly) return;
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            File.AppendAllText(_logPath, line);
        }
        catch
        {
            // Logging must never crash the app
        }
    }

    // ── Conversations ──────────────────────────────────────────────────────

    public IReadOnlyList<Conversation> Conversations
    {
        get
        {
            _lock.EnterReadLock();
            try { return _data.Conversations.ToList(); }
            finally { _lock.ExitReadLock(); }
        }
    }

    public Conversation? GetConversation(Guid id)
    {
        _lock.EnterReadLock();
        try { return _data.Conversations.FirstOrDefault(c => c.Id == id); }
        finally { _lock.ExitReadLock(); }
    }

    public void AddConversation(Conversation conv)
    {
        _lock.EnterWriteLock();
        try { using var transaction = BeginTransaction(); _data.Conversations.Add(conv); SaveAndVerify(conv.Id); }
        finally { _lock.ExitWriteLock(); }
    }

    public void UpdateConversation(Conversation conv)
    {
        _lock.EnterWriteLock();
        try
        {
            using var transaction = BeginTransaction();
            var idx = _data.Conversations.FindIndex(c => c.Id == conv.Id);
            if (idx < 0) throw new InvalidOperationException("会话已被删除，请刷新会话列表。");
            if (_externallyChanged.Contains(conv.Id) && !ReferenceEquals(_data.Conversations[idx], conv))
                throw new InvalidOperationException("会话已被另一终端更新，请重新打开，避免覆盖历史。");
            _data.Conversations[idx] = conv;
            SaveAndVerify(conv.Id);
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void DeleteConversation(Guid id)
    {
        _lock.EnterWriteLock();
        try { using var transaction = BeginTransaction(); _data.Conversations.RemoveAll(c => c.Id == id); _data.PendingInputs.RemoveAll(item => item.ConversationId == id); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Messages ───────────────────────────────────────────────────────────

    public List<Message> GetMessages(Guid conversationId)
    {
        _lock.EnterReadLock();
        try
        {
            var conv = _data.Conversations.FirstOrDefault(c => c.Id == conversationId);
            return conv?.Messages.OrderBy(m => m.CreatedAt).ToList() ?? new List<Message>();
        }
        finally { _lock.ExitReadLock(); }
    }

    public void AddMessage(Guid conversationId, Message msg)
    {
        _lock.EnterWriteLock();
        try
        {
            using var transaction = BeginTransaction();
            var conv = _data.Conversations.FirstOrDefault(c => c.Id == conversationId);
            conv?.Messages.Add(msg);
            SaveAndVerify(conversationId);
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Lifetime model usage ───────────────────────────────────────────────

    public IReadOnlyList<ModelUsageStats> GetTokenUsageStats()
    {
        _lock.EnterReadLock();
        try { return _data.TokenUsageStats.OrderByDescending(s => s.TotalTokens).ThenBy(s => s.ProviderId, StringComparer.Ordinal).ThenBy(s => s.ModelId, StringComparer.Ordinal).ToArray(); }
        finally { _lock.ExitReadLock(); }
    }

    public void RecordTokenUsage(string providerId, string modelId, ModelTokenUsage? usage, Guid? conversationId = null)
    {
        _lock.EnterWriteLock();
        try
        {
            using var transaction = BeginTransaction();
            var index = _data.TokenUsageStats.FindIndex(s => s.ProviderId == providerId && s.ModelId == modelId);
            var current = index >= 0 ? _data.TokenUsageStats[index] : new ModelUsageStats(providerId, modelId);
            var updated = current with
            {
                InputTokens = checked(current.InputTokens + Math.Max(0, usage?.InputTokens ?? 0)),
                OutputTokens = checked(current.OutputTokens + Math.Max(0, usage?.OutputTokens ?? 0)),
                Requests = checked(current.Requests + 1),
                RequestsWithUsage = checked(current.RequestsWithUsage + (usage == null ? 0 : 1))
            };
            if (index >= 0) _data.TokenUsageStats[index] = updated; else _data.TokenUsageStats.Add(updated);
            if (conversationId is { } id && usage?.CachedInputTokens is { } cached && _data.Conversations.FirstOrDefault(c => c.Id == id) is { } conversation)
            {
                conversation.CacheInputTokens = checked(conversation.CacheInputTokens + Math.Max(0, usage?.InputTokens ?? 0));
                conversation.CachedInputTokens = checked(conversation.CachedInputTokens + Math.Clamp(cached, 0, Math.Max(0, usage?.InputTokens ?? 0)));
                conversation.CacheUsageRequests = checked(conversation.CacheUsageRequests + 1);
            }
            SaveAndVerify(conversationId);
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Skills ─────────────────────────────────────────────────────────────

    public IReadOnlyList<Skill> Skills
    {
        get
        {
            _lock.EnterReadLock();
            try { return _data.Skills.ToList(); }
            finally { _lock.ExitReadLock(); }
        }
    }

    public Skill? GetSkill(Guid id)
    {
        _lock.EnterReadLock();
        try { return _data.Skills.FirstOrDefault(s => s.Id == id); }
        finally { _lock.ExitReadLock(); }
    }

    public void AddSkill(Skill skill)
    {
        _lock.EnterWriteLock();
        try { using var transaction = BeginTransaction(); _data.Skills.Add(skill); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    public void AddSkills(IEnumerable<Skill> skills)
    {
        _lock.EnterWriteLock();
        try { using var transaction = BeginTransaction(); _data.Skills.AddRange(skills); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    public void UpdateSkill(Skill skill)
    {
        _lock.EnterWriteLock();
        try
        {
            using var transaction = BeginTransaction();
            var idx = _data.Skills.FindIndex(s => s.Id == skill.Id);
            if (idx >= 0) _data.Skills[idx] = skill;
            SaveAndVerify();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void DeleteSkill(Guid id)
    {
        _lock.EnterWriteLock();
        try { using var transaction = BeginTransaction(); _data.Skills.RemoveAll(s => s.Id == id); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    public List<string> GetSkillNames()
    {
        _lock.EnterReadLock();
        try { return _data.Skills.Select(s => s.Name).ToList(); }
        finally { _lock.ExitReadLock(); }
    }

    // ── API Configs ────────────────────────────────────────────────────────

    public IReadOnlyList<ApiConfig> ApiConfigs
    {
        get
        {
            _lock.EnterReadLock();
            try { return _data.ApiConfigs.ToList(); }
            finally { _lock.ExitReadLock(); }
        }
    }

    public ApiConfig? GetApiConfig(Guid id)
    {
        _lock.EnterReadLock();
        try { return _data.ApiConfigs.FirstOrDefault(a => a.Id == id); }
        finally { _lock.ExitReadLock(); }
    }

    public void AddApiConfig(ApiConfig config)
    {
        _lock.EnterWriteLock();
        try { using var transaction = BeginTransaction(); _data.ApiConfigs.Add(config); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    public void UpdateApiConfig(ApiConfig config)
    {
        _lock.EnterWriteLock();
        try
        {
            using var transaction = BeginTransaction();
            var idx = _data.ApiConfigs.FindIndex(a => a.Id == config.Id);
            if (idx >= 0) _data.ApiConfigs[idx] = config;
            SaveAndVerify();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void DeleteApiConfig(Guid id)
    {
        _lock.EnterWriteLock();
        try { using var transaction = BeginTransaction(); _data.ApiConfigs.RemoveAll(a => a.Id == id); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    // ── MCP Server Configs ─────────────────────────────────────────────────

    public IReadOnlyList<McpServerConfig> McpServerConfigs
    {
        get
        {
            _lock.EnterReadLock();
            try { return _data.McpServerConfigs.ToList(); }
            finally { _lock.ExitReadLock(); }
        }
    }

    public McpServerConfig? GetMcpServerConfig(string id)
    {
        _lock.EnterReadLock();
        try { return _data.McpServerConfigs.FirstOrDefault(c => c.Id == id); }
        finally { _lock.ExitReadLock(); }
    }

    public void AddMcpServerConfig(McpServerConfig config)
    {
        _lock.EnterWriteLock();
        try { using var transaction = BeginTransaction(); _data.McpServerConfigs.Add(config); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    public void UpdateMcpServerConfig(McpServerConfig config)
    {
        _lock.EnterWriteLock();
        try
        {
            using var transaction = BeginTransaction();
            var idx = _data.McpServerConfigs.FindIndex(c => c.Id == config.Id);
            if (idx >= 0) _data.McpServerConfigs[idx] = config;
            SaveAndVerify();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void DeleteMcpServerConfig(string id)
    {
        _lock.EnterWriteLock();
        try { using var transaction = BeginTransaction(); _data.McpServerConfigs.RemoveAll(c => c.Id == id); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    // ── App Settings ───────────────────────────────────────────────────────

    public string? GetSetting(string key)
    {
        _lock.EnterReadLock();
        try { return _data.AppSettings.TryGetValue(key, out var v) ? v : null; }
        finally { _lock.ExitReadLock(); }
    }

    public void SetSetting(string key, string value)
    {
        _lock.EnterWriteLock();
        try
        {
            using var transaction = BeginTransaction();
            if (_data.AppSettings.TryGetValue(key, out var current) && current == value) return;
            _data.AppSettings[key] = value; SaveAndVerify();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public Dictionary<string, string> GetAllSettings()
    {
        _lock.EnterReadLock();
        try { return new Dictionary<string, string>(_data.AppSettings); }
        finally { _lock.ExitReadLock(); }
    }

    // ── Persistence ────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private void SaveAndVerify(Guid? changedConversation = null)
    {
        if (_readOnly) throw new InvalidOperationException("数据库快照只读。");
        var snapshot = CreateSplitSnapshot(changedConversation);
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var hash = SHA256.HashData(json);
        if (_savedHash != null && hash.AsSpan().SequenceEqual(_savedHash) && _savedStamp == FileStamp()) return;
        PreserveLegacyBackup();

        // 1. Write to temp file with explicit flush to disk
        WriteToFileAtomic(json, _tmpPath);

        // 2. Replace main file atomically
        try
        {
            if (File.Exists(_filePath))
            {
                File.Replace(_tmpPath, _filePath, _bakPath);
            }
            else
            {
                File.Move(_tmpPath, _filePath);
                try { File.WriteAllBytes(_bakPath, json); } catch { /* ignore */ }
            }
        }
        catch (IOException)
        {
            File.Move(_tmpPath, _filePath, overwrite: true);
            try { File.WriteAllBytes(_bakPath, json); } catch { /* ignore */ }
        }

        // 3. Verify exact bytes against the valid JSON produced by the serializer.
        // A streaming hash avoids rebuilding every message/config object on every save.
        using var verification = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (verification.Length != json.Length || !SHA256.HashData(verification).AsSpan().SequenceEqual(hash))
        {
            Log($"[DataStore] CRITICAL: Save verification failed for {_filePath}");
            throw new InvalidOperationException(
                "Data was written but could not be verified on read-back. " +
                "The storage medium may be unreliable or the file was corrupted during write.");
        }
        _savedHash = hash; _savedStamp = FileStamp(); PersistenceWrites++;
        Log($"[DataStore] Saved and verified. FileSize={json.Length} bytes");
        CleanupConversationFiles(snapshot);
    }

    private (long Length, DateTime Modified)? FileStamp()
    {
        var file = new FileInfo(_filePath);
        return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
    }

    private static void WriteToFileAtomic(byte[] content, string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65536,
            FileOptions.SequentialScan);

        // Buffer the snapshot, then issue one durable flush. WriteThrough used to
        // force every small encoding buffer through the disk cache separately.
        stream.Write(content);
        stream.Flush(true);
    }

    private void Load()
    {
        // Try main file
        if (TryLoadFile(_filePath, out var mainData) && mainData is not null)
        {
            _data = mainData;
            Log($"[DataStore] Loaded from main file. ApiConfigs={mainData.ApiConfigs.Count}");
            return;
        }

        // Main file missing or unreadable — try to restore from backup
        if (File.Exists(_bakPath))
        {
            Log($"[DataStore] Main file failed, trying backup: {_bakPath}");
            if (TryLoadFile(_bakPath, out var bakData) && bakData is not null)
            {
                _data = bakData;
                Log($"[DataStore] Loaded from backup. ApiConfigs={bakData.ApiConfigs.Count}");
                if (_readOnly) return;
                // Heal main file from backup
                try
                {
                    var json = File.ReadAllText(_bakPath);
                    File.WriteAllText(_filePath, json);
                    Log("[DataStore] Healed main file from backup.");
                }
                catch (Exception ex)
                {
                    Log($"[DataStore] Failed to heal main file: {ex.Message}");
                }
                return;
            }
        }

        if (File.Exists(_filePath) || File.Exists(_bakPath))
            throw new InvalidDataException("数据库及备份均无法完整读取；原文件已保留，请修复或恢复备份。");
        // Complete fallback: fresh start only when no persisted database exists.
        Log("[DataStore] Both main and backup failed. Starting fresh.");
        _data = new StoreData();
    }

    private bool TryLoadFile(string path, out StoreData? data)
    {
        data = null;
        try
        {
            if (!File.Exists(path))
            {
                Log($"[DataStore] File not found: {path}");
                return false;
            }

            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
            using var reader = new StreamReader(input);
            var json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json))
            {
                Log($"[DataStore] File is empty: {path}");
                return false;
            }

            var loaded = JsonSerializer.Deserialize<StoreData>(json, JsonOptions);
            if (loaded is null)
            {
                Log($"[DataStore] Deserialization returned null: {path}");
                return false;
            }
            if (loaded.Version > StoreData.CurrentVersion) throw new InvalidDataException("Unsupported database version.");
            if (loaded.Version < 3) _legacySource = path;
            if (loaded.Version >= 3) LoadSplitConversations(loaded);

            // Version migration hook
            if (loaded.Version < StoreData.CurrentVersion)
            {
                Log($"[DataStore] Migrating data from v{loaded.Version} to v{StoreData.CurrentVersion}");
                Migrate(ref loaded);
            }

            // Ensure collections are never null after deserialization
            loaded.Conversations ??= new List<Conversation>();
            loaded.PendingInputs ??= new List<PendingInput>();
            loaded.Skills ??= new List<Skill>();
            loaded.ApiConfigs ??= new List<ApiConfig>();
            loaded.AppSettings ??= new Dictionary<string, string>();
            loaded.McpServerConfigs ??= new List<McpServerConfig>();
            loaded.TokenUsageStats ??= new List<ModelUsageStats>();

            Log($"[DataStore] Deserialization OK: {path}. ApiConfigs={loaded.ApiConfigs.Count}, Skills={loaded.Skills.Count}, Conversations={loaded.Conversations.Count}");
            data = loaded;
            if (path == _filePath)
            {
                _savedHash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
                _savedStamp = FileStamp();
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"[DataStore] Deserialization FAILED: {path}. Error: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void Migrate(ref StoreData data)
    {
        if (data.Version < 2)
        {
            data.McpServerConfigs ??= new List<McpServerConfig>();
        }
        data.Version = StoreData.CurrentVersion;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();

        try { using var transaction = new StorageTransaction(_filePath); if (!_readOnly && File.Exists(_tmpPath)) File.Delete(_tmpPath); }
        catch { /* ignore */ }
    }

    // ── Internal data model ────────────────────────────────────────────────

    // Using internal (not private) to avoid any reflection issues with JsonSerializer
    internal class StoreData
    {
        public const int CurrentVersion = 3;
        public int Version { get; set; } = 1;
        public List<Conversation> Conversations { get; set; } = new();
        public List<ConversationFile> ConversationFiles { get; set; } = new();
        public List<PendingInput> PendingInputs { get; set; } = new();
        public List<Skill> Skills { get; set; } = new();
        public List<ApiConfig> ApiConfigs { get; set; } = new();
        public List<McpServerConfig> McpServerConfigs { get; set; } = new();
        public List<ModelUsageStats> TokenUsageStats { get; set; } = new();
        public Dictionary<string, string> AppSettings { get; set; } = new();
    }
}
