using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.Core.Data;

/// <summary>
/// JSON manifest plus immutable per-conversation revisions. Thread-safe,
/// atomic commits with fsync, checksum verification and backup recovery.
/// </summary>
public partial class DataStore : IDisposable
{
    public string DataDirectory => Path.GetDirectoryName(Path.GetFullPath(_filePath))!;
    private readonly string _filePath;
    private readonly string _bakPath;
    private readonly string _tmpPath;
    private readonly string _logPath;
    private readonly string? _sharedFilePath;
    private readonly string? _sharedBakPath;
    private readonly string? _sharedTmpPath;
    private StoreData? _sharedData;
    private readonly SharedConfigState? _sharedConfig;
    private readonly ReaderWriterLockSlim _lock;
    private readonly bool _ownsLock;
    private StoreData _data;
    private bool _disposed;
    private readonly bool _readOnly;
    private byte[]? _savedHash;
    private (long Length, DateTime Modified)? _savedStamp;
    internal long PersistenceWrites { get; private set; }

    public DataStore() : this(null, false) { }

    public DataStore(string? dataDirectory) : this(dataDirectory, false) { }

    private DataStore(string? dataDirectory, bool readOnly) : this(dataDirectory, readOnly, null) { }

    /// <summary>
    /// Configuration (providers, models, skills, MCP servers, settings, usage) is kept in
    /// <paramref name="sharedConfigDirectory"/> so every workspace sees the same setup and
    /// switching workspaces only switches conversations. A null shared directory keeps the
    /// single-manifest layout (desktop host, tests, explicit --data-dir runs).
    /// </summary>
    public DataStore(string? dataDirectory, bool readOnly, string? sharedConfigDirectory)
    {
        _readOnly = readOnly;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = dataDirectory ?? Path.Combine(appData, "AllAgent.NET");
        if (!readOnly) Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "allagent.json");
        _bakPath = Path.Combine(dir, "allagent.json.bak");
        _tmpPath = Path.Combine(dir, "allagent.json.tmp");
        _logPath = Path.Combine(dir, "allagent.log");
        _conversationDirectory = Path.Combine(dir, "conversations");
        _data = new StoreData();

        if (!string.IsNullOrWhiteSpace(sharedConfigDirectory))
        {
            var shared = Path.GetFullPath(sharedConfigDirectory);
            if (!string.Equals(Path.TrimEndingDirectorySeparator(shared), Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)), StringComparison.OrdinalIgnoreCase))
            {
                _sharedFilePath = Path.Combine(shared, "allagent.json");
                _sharedBakPath = _sharedFilePath + ".bak";
                _sharedTmpPath = _sharedFilePath + ".tmp";
                if (!readOnly) Directory.CreateDirectory(shared);
            }
        }

        // Every workspace in this process reads and writes one shared configuration object,
        // so switching workspaces cannot show an older snapshot or drop another workspace's
        // provider/login changes on its next save.
        _sharedConfig = _sharedFilePath == null ? null : AcquireSharedConfig(_sharedFilePath);
        _lock = _sharedConfig?.Lock ?? new ReaderWriterLockSlim();
        _ownsLock = _sharedConfig == null;

        Log($"[DataStore] Starting up. File={_filePath} Shared={_sharedFilePath ?? "<none>"}");
        try
        {
            using var startup = new StorageTransaction(_filePath);
            Load();
            InitializeConversationFiles();
            LoadSharedConfig();
        }
        catch
        {
            if (_sharedConfig != null) ReleaseSharedConfig(_sharedConfig); else if (_ownsLock) _lock.Dispose();
            throw;
        }
        Log($"[DataStore] Loaded. Conversations={_data.Conversations.Count}, Skills={Config.Skills.Count}, ApiConfigs={Config.ApiConfigs.Count}, Settings={Config.AppSettings.Count}");
    }

    /// <summary>Configuration snapshot: the shared manifest when one is configured.</summary>
    private StoreData Config => _sharedData ?? _data;

    private void LoadSharedConfig()
    {
        if (_sharedConfig == null || _sharedFilePath == null) return;
        _lock.EnterWriteLock();
        try
        {
            // Another workspace already opened this manifest in this process: adopt the live
            // object instead of reading the file again, which would fork the configuration.
            if (_sharedConfig.Data is { } live)
            {
                _sharedData = live;
                AdoptShared();
                return;
            }
            if (ReadSharedManifest() is { } shared)
            {
                _sharedConfig.Data = shared;
                try { _sharedConfig.Hash = SHA256.HashData(File.ReadAllBytes(_sharedFilePath)); } catch { _sharedConfig.Hash = null; }
                _sharedData = shared;
                AdoptShared();
                Log($"[DataStore] Shared config loaded from {_sharedFilePath}. ApiConfigs={shared.ApiConfigs.Count}, Skills={shared.Skills.Count}, Settings={shared.AppSettings.Count}");
                return;
            }
            // No shared manifest anywhere: seed from the legacy manifests when they still hold
            // configuration. The workspace manifest deliberately no longer carries providers,
            // so seeding only from it would silently discard a pre-split login.
            var seed = ReadLegacySeed(out var legacySource);
            _sharedConfig.Data = new StoreData
            {
                Version = StoreData.CurrentVersion,
                Skills = seed.Skills,
                ApiConfigs = seed.ApiConfigs,
                McpServerConfigs = seed.McpServerConfigs,
                TokenUsageStats = seed.TokenUsageStats,
                DailyUsageStats = seed.DailyUsageStats,
                AppSettings = seed.AppSettings,
            };
            _sharedData = _sharedConfig.Data;
            AdoptShared();
            if (legacySource || HasConfiguration(_sharedData))
            {
                Log($"[DataStore] Seeded shared config at {_sharedFilePath}. ApiConfigs={_sharedData.ApiConfigs.Count}");
                SaveShared();
            }
            else
            {
                // A genuinely empty profile must not create a manifest on startup: a
                // transiently missing shared file must never be replaced with empty data.
                Log($"[DataStore] No shared configuration found at {_sharedFilePath}; keeping it unset until configuration is saved.");
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Reads the shared manifest without ever treating a missing file as permission to
    /// overwrite it. A concurrent writer may briefly replace the file, so a missing main
    /// is retried and then recovered from the backup before the profile is considered new.
    /// </summary>
    private StoreData? ReadSharedManifest()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (File.Exists(_sharedFilePath))
            {
                if (TryLoadFile(_sharedFilePath, out var loaded) && loaded is not null) return loaded;
                // Present but unreadable: fall through to the backup, then fail loudly instead
                // of replacing a damaged manifest with whatever this workspace happens to hold.
                break;
            }
            // A concurrent writer may be between the temp file and the atomic replace.
            Thread.Sleep(50);
        }

        if (_sharedBakPath != null && TryLoadFile(_sharedBakPath, out var backup) && backup is not null)
        {
            Log($"[DataStore] Shared config recovered from backup: {_sharedBakPath}");
            if (!_readOnly)
            {
                try
                {
                    WriteToFileAtomic(File.ReadAllBytes(_sharedBakPath), _sharedTmpPath!);
                    File.Move(_sharedTmpPath!, _sharedFilePath!, overwrite: true);
                    Log("[DataStore] Healed shared config from backup.");
                }
                catch (Exception ex) { Log($"[DataStore] Failed to heal shared config: {ex.Message}"); }
            }
            return backup;
        }

        if (File.Exists(_sharedFilePath) || (_sharedBakPath != null && File.Exists(_sharedBakPath)))
            throw new InvalidDataException("共享配置及备份均无法完整读取；原文件已保留，请修复或恢复备份。");
        return null;
    }

    /// <summary>Pre-split manifests in the shared or workspace directory, newest naming first.</summary>
    private StoreData ReadLegacySeed(out bool fromLegacy)
    {
        var sharedLegacy = Path.Combine(Path.GetDirectoryName(_sharedFilePath!)!, "lanagent.json");
        if (TryLoadFile(sharedLegacy, out var sharedData) && sharedData is not null) { fromLegacy = true; return sharedData; }
        if (TryLoadFile(sharedLegacy + ".bak", out var sharedBackup) && sharedBackup is not null) { fromLegacy = true; return sharedBackup; }
        if (HasConfiguration(_data)) { fromLegacy = false; return _data; }

        var workspaceLegacy = Path.Combine(Path.GetDirectoryName(_filePath)!, "lanagent.json");
        if (TryLoadFile(workspaceLegacy, out var workspaceData) && workspaceData is not null) { fromLegacy = true; return workspaceData; }
        if (TryLoadFile(workspaceLegacy + ".bak", out var workspaceBackup) && workspaceBackup is not null) { fromLegacy = true; return workspaceBackup; }
        fromLegacy = false;
        return _data;
    }

    /// <summary>User-owned configuration; migration markers and host defaults in
    /// <see cref="StoreData.AppSettings"/> alone are not a profile worth creating.</summary>
    private static bool HasConfiguration(StoreData data) =>
        data.ApiConfigs.Count > 0 || data.Skills.Count > 0 || data.McpServerConfigs.Count > 0 ||
        data.TokenUsageStats.Count > 0 || data.DailyUsageStats.Count > 0;

    /// <summary>Share the configuration collections so every existing accessor keeps working.</summary>
    private void AdoptShared()
    {
        if (_sharedData == null) return;
        _data.Skills = _sharedData.Skills;
        _data.ApiConfigs = _sharedData.ApiConfigs;
        _data.McpServerConfigs = _sharedData.McpServerConfigs;
        _data.TokenUsageStats = _sharedData.TokenUsageStats;
        _data.DailyUsageStats = _sharedData.DailyUsageStats;
        _data.AppSettings = _sharedData.AppSettings;
    }

    private static void CopyInto<T>(List<T> target, IEnumerable<T> source)
    {
        target.Clear();
        target.AddRange(source);
    }

    /// <summary>Writes the shared configuration manifest, skipping unchanged content.</summary>
    private void SaveShared()
    {
        if (_readOnly || _sharedData == null || _sharedConfig == null || _sharedFilePath == null || _sharedBakPath == null || _sharedTmpPath == null) return;
        // Configuration accessors already hold the shared write lock; keep the constructor's
        // seed path safe as well without trying to re-enter a non-recursive lock.
        var entered = false;
        if (!_lock.IsWriteLockHeld) { _lock.EnterWriteLock(); entered = true; }
        try
        {
            var snapshot = new StoreData
            {
                Version = StoreData.CurrentVersion,
                Skills = _sharedData.Skills,
                ApiConfigs = _sharedData.ApiConfigs,
                McpServerConfigs = _sharedData.McpServerConfigs,
                TokenUsageStats = _sharedData.TokenUsageStats,
                DailyUsageStats = _sharedData.DailyUsageStats,
                AppSettings = _sharedData.AppSettings,
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            var hash = SHA256.HashData(json);
            if (_sharedConfig.Hash != null && hash.AsSpan().SequenceEqual(_sharedConfig.Hash)) return;
            WriteToFileAtomic(json, _sharedTmpPath);
            try
            {
                if (File.Exists(_sharedFilePath)) File.Replace(_sharedTmpPath, _sharedFilePath, _sharedBakPath);
                else
                {
                    File.Move(_sharedTmpPath, _sharedFilePath);
                    // Keep an older snapshot instead of replacing it with the same bytes:
                    // it may be the only remaining copy of a profile whose main file was lost.
                    if (!File.Exists(_sharedBakPath))
                        try { File.WriteAllBytes(_sharedBakPath, json); } catch { /* ignore */ }
                }
            }
            catch (IOException)
            {
                File.Move(_sharedTmpPath, _sharedFilePath, overwrite: true);
                if (!File.Exists(_sharedBakPath))
                    try { File.WriteAllBytes(_sharedBakPath, json); } catch { /* ignore */ }
            }
            using var verification = new FileStream(_sharedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            if (verification.Length != json.Length || !SHA256.HashData(verification).AsSpan().SequenceEqual(hash))
                throw new InvalidOperationException("共享配置写入后校验失败：" + _sharedFilePath);
            _sharedConfig.Hash = hash;
            PersistenceWrites++;
            Log($"[DataStore] Shared config saved. ApiConfigs={snapshot.ApiConfigs.Count}, Settings={snapshot.AppSettings.Count}, FileSize={json.Length}");
        }
        finally { if (entered) _lock.ExitWriteLock(); }
    }

    /// <summary>One shared configuration manifest per path for the whole process.</summary>
    private sealed class SharedConfigState(string path)
    {
        public string Path { get; } = path;
        public ReaderWriterLockSlim Lock { get; } = new(LockRecursionPolicy.NoRecursion);
        public StoreData? Data { get; set; }
        public byte[]? Hash { get; set; }
        public int References { get; set; }
    }

    private static readonly Dictionary<string, SharedConfigState> SharedConfigStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SharedConfigStatesGate = new();

    private static SharedConfigState AcquireSharedConfig(string path)
    {
        lock (SharedConfigStatesGate)
        {
            if (!SharedConfigStates.TryGetValue(path, out var state))
            {
                state = new SharedConfigState(path);
                SharedConfigStates[path] = state;
            }
            state.References++;
            return state;
        }
    }

    private static void ReleaseSharedConfig(SharedConfigState state)
    {
        lock (SharedConfigStatesGate)
        {
            if (--state.References > 0) return;
            SharedConfigStates.Remove(state.Path);
            state.Lock.Dispose();
        }
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

    /// <summary>Daily buckets written by <see cref="RecordTokenUsage"/>; older usage has no history.</summary>
    public IReadOnlyList<DailyUsageStats> GetDailyUsageStats()
    {
        _lock.EnterReadLock();
        try { return _data.DailyUsageStats.OrderBy(s => s.Date, StringComparer.Ordinal).ThenBy(s => s.ProviderId, StringComparer.Ordinal).ThenBy(s => s.ModelId, StringComparer.Ordinal).ToArray(); }
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

            // The panel's activity graph buckets by the user's local calendar day; lifetime totals
            // above still include usage recorded before daily tracking existed.
            var date = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var dailyIndex = _data.DailyUsageStats.FindIndex(s => s.Date == date && s.ProviderId == providerId && s.ModelId == modelId);
            var daily = dailyIndex >= 0 ? _data.DailyUsageStats[dailyIndex] : new DailyUsageStats(date, providerId, modelId);
            var dailyUpdated = daily with
            {
                InputTokens = checked(daily.InputTokens + Math.Max(0, usage?.InputTokens ?? 0)),
                OutputTokens = checked(daily.OutputTokens + Math.Max(0, usage?.OutputTokens ?? 0)),
                Requests = checked(daily.Requests + 1),
                RequestsWithUsage = checked(daily.RequestsWithUsage + (usage == null ? 0 : 1))
            };
            if (dailyIndex >= 0) _data.DailyUsageStats[dailyIndex] = dailyUpdated; else _data.DailyUsageStats.Add(dailyUpdated);

            if (conversationId is { } id && usage is { } reported && (reported.CachedInputTokens is not null || reported.CacheWriteInputTokens is not null) &&
                _data.Conversations.FirstOrDefault(c => c.Id == id) is { } conversation)
            {
                var input = Math.Max(0, reported.InputTokens);
                conversation.CacheInputTokens = checked(conversation.CacheInputTokens + input);
                conversation.CachedInputTokens = checked(conversation.CachedInputTokens + Math.Clamp(reported.CachedInputTokens ?? 0, 0, input));
                conversation.CacheWriteInputTokens = checked(conversation.CacheWriteInputTokens + Math.Clamp(reported.CacheWriteInputTokens ?? 0, 0, input));
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

    /// <summary>Directory holding this store's manifest and conversations.</summary>
    public string DirectoryPath => Path.GetDirectoryName(Path.GetFullPath(_filePath))!;

    /// <summary>Directory holding the shared provider/config manifest, when this store uses one.</summary>
    public string? SharedDirectoryPath => _sharedFilePath == null ? null : Path.GetDirectoryName(Path.GetFullPath(_sharedFilePath));

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
        try { return Config.AppSettings.TryGetValue(key, out var v) ? v : null; }
        finally { _lock.ExitReadLock(); }
    }

    public void SetSetting(string key, string value)
    {
        _lock.EnterWriteLock();
        try
        {
            using var transaction = BeginTransaction();
            if (Config.AppSettings.TryGetValue(key, out var current) && current == value) return;
            Config.AppSettings[key] = value; SaveAndVerify();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public Dictionary<string, string> GetAllSettings()
    {
        _lock.EnterReadLock();
        try { return new Dictionary<string, string>(Config.AppSettings); }
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
        // Configuration changes leave the workspace manifest untouched, so the shared
        // manifest is written first instead of after the unchanged-content short circuit.
        SaveShared();
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
        LoadLocal();
        // Configuration always comes from the shared manifest. Reloading the workspace
        // manifest (every write transaction refreshes from disk) must never replace the
        // configuration with the config-free workspace snapshot, or lookups by id fail.
        AdoptShared();
    }

    private void LoadLocal()
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
        // Read old manifests only when no current manifest exists. The first normal
        // commit writes the canonical name; old manifests remain recoverable.
        var directory = Path.GetDirectoryName(_filePath)!;
        var legacy = Path.Combine(directory, "lanagent.json");
        var legacyBackup = legacy + ".bak";
        if (TryLoadFile(legacy, out var legacyData) || TryLoadFile(legacyBackup, out legacyData))
        {
            _data = legacyData!;
            return;
        }
        if (File.Exists(legacy) || File.Exists(legacyBackup))
            throw new InvalidDataException("旧数据库及备份均无法完整读取；原文件已保留，请修复或恢复备份。");
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
            loaded.DailyUsageStats ??= new List<DailyUsageStats>();
            MigrateLegacyOutputLimits(loaded);

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

    // The original web form and provider preset wrote small limits (4096/8192/16384) before response
    // sizing followed the model catalog, and none of them can serve a modern agent request. Every
    // manifest load applies one pass that turns any explicit limit below the floor back into automatic
    // sizing; a value at or above it is a deliberate cap and stays. The marker stores the floor that
    // was applied, so changing the rule runs the pass once more instead of rewriting on every start.
    private const int LegacyOutputLimitFloor = 32768;
    private const string LegacyOutputLimitMarker = "migration:output-limit";

    private void MigrateLegacyOutputLimits(StoreData data)
    {
        var rule = LegacyOutputLimitFloor.ToString();
        if (data.AppSettings.TryGetValue(LegacyOutputLimitMarker, out var applied) && applied == rule) return;
        var migrated = data.ApiConfigs.Where(config => config.MaxTokens > 0 && config.MaxTokens < LegacyOutputLimitFloor).ToArray();
        foreach (var config in migrated) config.MaxTokens = 0;
        data.AppSettings[LegacyOutputLimitMarker] = rule;
        if (migrated.Length > 0) Log($"[DataStore] Output limit migration: {migrated.Length} config(s) below {rule} follow the model catalog again.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsLock) _lock.Dispose();

        try { using var transaction = new StorageTransaction(_filePath); if (!_readOnly && File.Exists(_tmpPath)) File.Delete(_tmpPath); }
        catch { /* ignore */ }
        try { if (!_readOnly && _sharedTmpPath != null && File.Exists(_sharedTmpPath)) File.Delete(_sharedTmpPath); }
        catch { /* ignore */ }
        if (_sharedConfig != null) ReleaseSharedConfig(_sharedConfig);
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
        public List<DailyUsageStats> DailyUsageStats { get; set; } = new();
        public Dictionary<string, string> AppSettings { get; set; } = new();
    }
}
