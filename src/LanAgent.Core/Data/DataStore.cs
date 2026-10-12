using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;

namespace LanAgent.Core.Data;

/// <summary>
/// JSON file-based data store. Thread-safe, atomic writes with fsync,
/// post-write verification, and automatic backup recovery.
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

    public DataStore() : this(null) { }

    public DataStore(string? dataDirectory)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = dataDirectory ?? Path.Combine(appData, "LanAgent");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "lanagent.json");
        _bakPath = Path.Combine(dir, "lanagent.json.bak");
        _tmpPath = Path.Combine(dir, "lanagent.json.tmp");
        _logPath = Path.Combine(dir, "lanagent.log");
        _data = new StoreData();

        Log($"[DataStore] Starting up. File={_filePath}");
        Load();
        Log($"[DataStore] Loaded. Conversations={_data.Conversations.Count}, Skills={_data.Skills.Count}, ApiConfigs={_data.ApiConfigs.Count}, Settings={_data.AppSettings.Count}");
    }

    // ── Logging ────────────────────────────────────────────────────────────

    private void Log(string message)
    {
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
        try { _data.Conversations.Add(conv); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    public void UpdateConversation(Conversation conv)
    {
        _lock.EnterWriteLock();
        try
        {
            var idx = _data.Conversations.FindIndex(c => c.Id == conv.Id);
            if (idx >= 0) _data.Conversations[idx] = conv;
            SaveAndVerify();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void DeleteConversation(Guid id)
    {
        _lock.EnterWriteLock();
        try { _data.Conversations.RemoveAll(c => c.Id == id); _data.PendingInputs.RemoveAll(item => item.ConversationId == id); SaveAndVerify(); }
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
            var conv = _data.Conversations.FirstOrDefault(c => c.Id == conversationId);
            conv?.Messages.Add(msg);
            SaveAndVerify();
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
            SaveAndVerify();
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
        try { _data.Skills.Add(skill); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    public void AddSkills(IEnumerable<Skill> skills)
    {
        _lock.EnterWriteLock();
        try { _data.Skills.AddRange(skills); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    public void UpdateSkill(Skill skill)
    {
        _lock.EnterWriteLock();
        try
        {
            var idx = _data.Skills.FindIndex(s => s.Id == skill.Id);
            if (idx >= 0) _data.Skills[idx] = skill;
            SaveAndVerify();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void DeleteSkill(Guid id)
    {
        _lock.EnterWriteLock();
        try { _data.Skills.RemoveAll(s => s.Id == id); SaveAndVerify(); }
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
        try { _data.ApiConfigs.Add(config); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    public void UpdateApiConfig(ApiConfig config)
    {
        _lock.EnterWriteLock();
        try
        {
            var idx = _data.ApiConfigs.FindIndex(a => a.Id == config.Id);
            if (idx >= 0) _data.ApiConfigs[idx] = config;
            SaveAndVerify();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void DeleteApiConfig(Guid id)
    {
        _lock.EnterWriteLock();
        try { _data.ApiConfigs.RemoveAll(a => a.Id == id); SaveAndVerify(); }
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
        try { _data.McpServerConfigs.Add(config); SaveAndVerify(); }
        finally { _lock.ExitWriteLock(); }
    }

    public void UpdateMcpServerConfig(McpServerConfig config)
    {
        _lock.EnterWriteLock();
        try
        {
            var idx = _data.McpServerConfigs.FindIndex(c => c.Id == config.Id);
            if (idx >= 0) _data.McpServerConfigs[idx] = config;
            SaveAndVerify();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void DeleteMcpServerConfig(string id)
    {
        _lock.EnterWriteLock();
        try { _data.McpServerConfigs.RemoveAll(c => c.Id == id); SaveAndVerify(); }
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
        try { _data.AppSettings[key] = value; SaveAndVerify(); }
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

    private void SaveAndVerify()
    {
        var json = JsonSerializer.Serialize(_data, JsonOptions);

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
                try { File.WriteAllText(_bakPath, json); } catch { /* ignore */ }
            }
        }
        catch (IOException)
        {
            File.Move(_tmpPath, _filePath, overwrite: true);
            try { File.WriteAllText(_bakPath, json); } catch { /* ignore */ }
        }

        // 3. Verify: read back and ensure it parses
        var verified = TryLoadFile(_filePath, out var verifiedData);
        if (!verified || verifiedData is null)
        {
            Log($"[DataStore] CRITICAL: Save verification failed for {_filePath}");
            throw new InvalidOperationException(
                "Data was written but could not be verified on read-back. " +
                "The storage medium may be unreliable or the file was corrupted during write.");
        }

        Log($"[DataStore] Saved and verified. FileSize={new FileInfo(_filePath).Length} bytes");
    }

    private static void WriteToFileAtomic(string content, string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);

        using var writer = new StreamWriter(stream);
        writer.Write(content);
        writer.Flush();
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

        // Complete fallback: fresh start
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

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                Log($"[DataStore] File is empty: {path}");
                data = new StoreData();
                return true;
            }

            var loaded = JsonSerializer.Deserialize<StoreData>(json, JsonOptions);
            if (loaded is null)
            {
                Log($"[DataStore] Deserialization returned null: {path}");
                return false;
            }

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

        try { if (File.Exists(_tmpPath)) File.Delete(_tmpPath); }
        catch { /* ignore */ }
    }

    // ── Internal data model ────────────────────────────────────────────────

    // Using internal (not private) to avoid any reflection issues with JsonSerializer
    internal class StoreData
    {
        public const int CurrentVersion = 2;
        public int Version { get; set; } = 1;
        public List<Conversation> Conversations { get; set; } = new();
        public List<PendingInput> PendingInputs { get; set; } = new();
        public List<Skill> Skills { get; set; } = new();
        public List<ApiConfig> ApiConfigs { get; set; } = new();
        public List<McpServerConfig> McpServerConfigs { get; set; } = new();
        public List<ModelUsageStats> TokenUsageStats { get; set; } = new();
        public Dictionary<string, string> AppSettings { get; set; } = new();
    }
}
