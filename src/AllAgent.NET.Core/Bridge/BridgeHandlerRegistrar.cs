using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.Core.Bridge;

// DTOs
public record ConversationDto(Guid Id, string Title, string ModelId, string ProviderId, DateTime CreatedAt, DateTime UpdatedAt);
public record MessageDto(Guid Id, string Role, string Content, string? ToolCallsJson, string? ToolCallId, string? ToolName, string? ReasoningContent, DateTime CreatedAt, string? HostToolTraceJson, bool IsHostActivity = false, bool IsInterrupted = false);
public record TaskParams(string Id);
public record SkillDto(Guid Id, string Name, string Description, string ParametersJson, string? CommandTemplate, string Type, bool IsEnabled);
public record ApiConfigDto(Guid Id, string ProviderId, string DisplayName, string BaseUrl, string ApiKey, string DefaultModel, double Temperature, int MaxTokens, bool IsEnabled, string? ExtraHeadersJson, bool ThinkingEnabled, int ThinkingBudget,
    int ContextWindowTokens = 100000, string Protocol = "", string ReasoningProtocol = "auto", string ReasoningEffort = "default",
    List<string>? Models = null, string? ApiKeyEnvironmentVariable = null, string? AuthenticationProviderId = null,
    int EffectiveContextTokens = 0, string EffectiveProtocol = "");
public record McpServerConfigDto(string Id, string Name, string Command, string Arguments, bool IsEnabled, DateTime CreatedAt, bool IsConnected = false);
public record SettingsDto(string Theme, string Language, string DefaultModel, bool StreamingEnabled, string DefaultProviderId, bool BrowserUseEnabled = true, string BrowserUseMode = "builtin", string BrowserExecutablePath = "", string BrowserCdpUrl = "");

// Request params
public record CreateConversationParams(string Title = "New Conversation");
public record RenameConversationParams(Guid Id, string Title);
public record DeleteConversationParams(Guid Id);
public record GetMessagesParams(Guid ConversationId);
public record SetTodosParams(Guid ConversationId, List<TodoItem> Todos);
public record StartStreamParams(Guid ConversationId, string Message, Guid ApiConfigId, bool ThinkingEnabled = false, int ThinkingBudget = 8000, string? Model = null, string? ReasoningEffort = null);
public record CancelStreamParams(string StreamId);
public record ImportSkillParams(string Json);
public record ImportSkillZipParams(string Base64Zip);
public record ToggleSkillParams(Guid Id, bool Enabled);
public record DeleteSkillParams(Guid Id);
public record SaveApiConfigParams(Guid? Id, string ProviderId, string DisplayName, string BaseUrl, string ApiKey, string DefaultModel, double Temperature, int MaxTokens, bool IsEnabled, string? ExtraHeadersJson, bool ThinkingEnabled = false, int ThinkingBudget = 8000,
    int ContextWindowTokens = 100000, string Protocol = "", string ReasoningProtocol = "auto", string ReasoningEffort = "default",
    List<string>? Models = null, string? ApiKeyEnvironmentVariable = null, string? AuthenticationProviderId = null);
public record ReasoningOptionsDto(string Protocol, IReadOnlyList<string> Protocols, IReadOnlyList<string> Efforts, string Effort, int Budget, int MinimumBudget, int MaximumBudget, string Label);
public record DeleteApiConfigParams(Guid Id);
public record TestApiConfigParams(Guid Id);
public record SaveSettingsParams(string Theme, string Language, string DefaultModel, bool StreamingEnabled, string DefaultProviderId, bool BrowserUseEnabled = true, string BrowserUseMode = "builtin", string BrowserExecutablePath = "", string BrowserCdpUrl = "");
public record SaveMcpServerConfigParams(string? Id, string Name, string Command, string Arguments, bool IsEnabled);
public record DeleteMcpServerConfigParams(string Id);
public record ConnectMcpServerParams(string Id);

public class BridgeHandlerRegistrar
{
    private readonly BridgeDispatcher _dispatcher;
    private readonly DataStore _store;
    private readonly AgentService _agentService;
    private readonly AiProviderFactory _providerFactory;
    private readonly BridgeEventPusher _eventPusher;
    private readonly McpToolService _mcpToolService;
    private readonly object _streamsGate = new();
    private readonly Dictionary<Guid, BridgeStreamJournal> _webStreams = new();

    public BridgeHandlerRegistrar(
        BridgeDispatcher dispatcher,
        DataStore store,
        AgentService agentService,
        AiProviderFactory providerFactory,
        BridgeEventPusher eventPusher,
        McpToolService mcpToolService)
    {
        _dispatcher = dispatcher;
        _store = store;
        _agentService = agentService;
        _providerFactory = providerFactory;
        _eventPusher = eventPusher;
        _mcpToolService = mcpToolService;
        // Background-child results and wake-up turns change history without a user-initiated stream; tell the WebView to refresh.
        _agentService.TaskRefreshRequested = conversationId => _eventPusher.Push(JsonSerializer.Serialize(
            new { type = "tasks.updated", conversationId = conversationId.ToString() }, JsonOptions.Default));
    }

    public void RegisterAll()
    {
        // Conversations
        _dispatcher.Register("conversations.list", ListConversationsAsync);
        _dispatcher.Register<CreateConversationParams, ConversationDto>("conversations.create", CreateConversationAsync);
        _dispatcher.Register<RenameConversationParams, bool>("conversations.rename", RenameConversationAsync);
        _dispatcher.Register<DeleteConversationParams, bool>("conversations.delete", DeleteConversationAsync);
        _dispatcher.Register<GetMessagesParams, List<MessageDto>>("conversations.getMessages", GetMessagesAsync);
        _dispatcher.Register<GetMessagesParams, IReadOnlyList<TodoItem>>("todos.get", (p, ct) => Task.FromResult(_store.GetTodos(p.ConversationId)));
        _dispatcher.Register<SetTodosParams, IReadOnlyList<TodoItem>>("todos.set", (p, ct) =>
        {
            if (_agentService.IsRunning(p.ConversationId)) throw new InvalidOperationException("请先停止当前生成再编辑 TODO。");
            return Task.FromResult(_store.SetTodos(p.ConversationId, p.Todos));
        });

        // Chat streaming
        _dispatcher.Register<StartStreamParams, string>("chat.startStream", StartStreamAsync);
        _dispatcher.Register<CancelStreamParams, bool>("chat.cancelStream", CancelStreamAsync);
        _dispatcher.Register<GetMessagesParams, object?>("chat.getStreamState", (p, _) =>
        {
            lock (_streamsGate)
                return Task.FromResult(_webStreams.TryGetValue(p.ConversationId, out var stream)
                    ? stream.Snapshot(GetEventName, GetEventData) : null);
        });
        _dispatcher.Register<TaskParams, BackgroundTaskInfo>("tasks.get", (p, ct) => Task.FromResult(_agentService.Jobs.Get(p.Id)));
        _dispatcher.Register<TaskParams, bool>("tasks.cancel", async (p, ct) => { _agentService.Jobs.Cancel(p.Id); await _agentService.Jobs.WaitAsync(p.Id, ct); return true; });
        _dispatcher.Register<IReadOnlyList<BackgroundTaskInfo>>("tasks.list", (ct) => Task.FromResult(_agentService.Jobs.List));

        // Skills
        _dispatcher.Register("skills.list", ListSkillsAsync);
        _dispatcher.Register<ImportSkillParams, SkillDto>("skills.import", ImportSkillAsync);
        _dispatcher.Register<ImportSkillZipParams, List<SkillDto>>("skills.importZip", ImportSkillZipAsync);
        _dispatcher.Register<ToggleSkillParams, bool>("skills.toggle", ToggleSkillAsync);
        _dispatcher.Register<DeleteSkillParams, bool>("skills.delete", DeleteSkillAsync);

        // API Configs
        _dispatcher.Register("apiConfigs.list", ListApiConfigsAsync);
        _dispatcher.Register<SaveApiConfigParams, ApiConfigDto>("apiConfigs.save", SaveApiConfigAsync);
        _dispatcher.Register<DeleteApiConfigParams, bool>("apiConfigs.delete", DeleteApiConfigAsync);
        _dispatcher.Register<TestApiConfigParams, bool>("apiConfigs.test", TestApiConfigAsync);
        _dispatcher.Register<TestApiConfigParams, ApiConfigDto>("apiConfigs.refreshModels", RefreshModelsAsync);
        _dispatcher.Register<TestApiConfigParams, ReasoningOptionsDto>("apiConfigs.reasoning", GetReasoningAsync);

        // Settings
        _dispatcher.Register<IReadOnlyList<AgentRoleDefinition>>("agents.list", ct => Task.FromResult(new AgentRoleCatalog(_store).List()));
        _dispatcher.Register<ImportSkillParams, bool>("agents.import", (p, ct) =>
        {
            new AgentRoleCatalog(_store).Import(p.Json);
            return Task.FromResult(true);
        });
        _dispatcher.Register<SettingsDto>("settings.get", (ct) => GetSettingsAsync(ct));
        _dispatcher.Register<SaveSettingsParams, bool>("settings.save", SaveSettingsAsync);

        // MCP Server Configs
        _dispatcher.Register("mcpServers.list", ListMcpServerConfigsAsync);
        _dispatcher.Register<SaveMcpServerConfigParams, McpServerConfigDto>("mcpServers.save", SaveMcpServerConfigAsync);
        _dispatcher.Register<DeleteMcpServerConfigParams, bool>("mcpServers.delete", DeleteMcpServerConfigAsync);
        _dispatcher.Register<ConnectMcpServerParams, bool>("mcpServers.connect", ConnectMcpServerAsync);
        _dispatcher.Register<ConnectMcpServerParams, bool>("mcpServers.disconnect", async (p, ct) => { await _mcpToolService.DisconnectServerAsync(p.Id); return true; });
    }

    // ── Conversations ──────────────────────────────────────────────────────────

    private Task<List<ConversationDto>> ListConversationsAsync(CancellationToken ct)
    {
        var convs = _store.Conversations
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => ToDto(c) with { Title = DisplayTitle(c) })
            .ToList();
        return Task.FromResult(convs);
    }

    /// <summary>Conversations created from the terminal keep a placeholder title until renamed; derive a readable one from the first prompt.</summary>
    private string DisplayTitle(Conversation conversation)
    {
        if (!string.IsNullOrWhiteSpace(conversation.Title) && conversation.Title != "New Conversation") return conversation.Title;
        var prompt = _store.GetVisibleMessages(conversation.Id).FirstOrDefault(message => message.Role == "user")?.Content;
        if (string.IsNullOrWhiteSpace(prompt)) return "新对话";
        var line = prompt.Replace('\n', ' ').Trim();
        return line.Length > 40 ? string.Concat(line.AsSpan(0, 40), "…") : line;
    }

    private Task<ConversationDto> CreateConversationAsync(CreateConversationParams p, CancellationToken ct)
    {
        var conv = new Conversation { Title = p.Title };
        _store.AddConversation(conv);
        return Task.FromResult(ToDto(conv));
    }

    private Task<bool> RenameConversationAsync(RenameConversationParams p, CancellationToken ct)
    {
        var conv = _store.GetConversation(p.Id);
        if (conv == null) return Task.FromResult(false);
        conv.Title = p.Title;
        conv.UpdatedAt = DateTime.UtcNow;
        _store.UpdateConversation(conv);
        return Task.FromResult(true);
    }

    private Task<bool> DeleteConversationAsync(DeleteConversationParams p, CancellationToken ct)
    {
        if (_agentService.IsRunning(p.Id)) throw new InvalidOperationException("请先停止当前生成再删除会话。");
        var conv = _store.GetConversation(p.Id);
        if (conv == null) return Task.FromResult(false);
        _store.DeleteConversation(p.Id);
        return Task.FromResult(true);
    }

    private Task<List<MessageDto>> GetMessagesAsync(GetMessagesParams p, CancellationToken ct)
    {
        var msgs = _store.GetVisibleMessages(p.ConversationId).Where(message => !message.IsInternal)
            .Select(ToDto)
            .ToList();
        return Task.FromResult(msgs);
    }

    // ── Chat Streaming ─────────────────────────────────────────────────────────

    private Task<string> StartStreamAsync(StartStreamParams p, CancellationToken ct)
    {
        if (_store.GetConversation(p.ConversationId) == null) throw new ArgumentException("会话不存在。");
        if (string.IsNullOrWhiteSpace(p.Message)) throw new ArgumentException("消息不能为空。");
        var config = _store.GetApiConfig(p.ApiConfigId)
            ?? throw new Exception($"ApiConfig {p.ApiConfigId} not found");
        if (!config.IsEnabled) throw new InvalidOperationException("所选模型配置已停用。");

        // Apply chat-level overrides to the config copy
        config = new ApiConfig
        {
            Id = config.Id,
            ProviderId = config.ProviderId,
            DisplayName = config.DisplayName,
            BaseUrl = config.BaseUrl,
            ApiKey = config.ApiKey,
            DefaultModel = string.IsNullOrWhiteSpace(p.Model) ? config.DefaultModel : p.Model!,
            Temperature = config.Temperature,
            MaxTokens = config.MaxTokens,
            IsEnabled = config.IsEnabled,
            ExtraHeadersJson = config.ExtraHeadersJson,
            ThinkingEnabled = p.ThinkingEnabled,
            ThinkingBudget = p.ThinkingBudget,
            ReasoningEffort = string.IsNullOrWhiteSpace(p.ReasoningEffort) ? config.ReasoningEffort : p.ReasoningEffort!,
            ReasoningProtocol = config.ReasoningProtocol,
            ContextWindowTokens = config.ContextWindowTokens,
            ModelContextWindows = config.ModelContextWindows,
            ModelLimits = config.ModelLimits,
            Models = config.Models,
            Protocol = config.Protocol,
            AuthenticationProviderId = config.AuthenticationProviderId,
            ApiKeyEnvironmentVariable = config.ApiKeyEnvironmentVariable,
        };

        lock (_streamsGate)
        {
            if (_agentService.IsRunning(p.ConversationId)) throw new InvalidOperationException("This conversation already has an active run.");
            var journal = new BridgeStreamJournal(_store.GetVisibleMessages(p.ConversationId).Where(message => !message.IsInternal).Select(ToDto).ToList(), p.Message);
            var streamId = _agentService.StartStream(p.ConversationId, p.Message, config,
                async (sid, evt) =>
                {
                    var timestamp = DateTime.UtcNow;
                    var sequence = journal.Append(evt, timestamp);
                    var json = JsonSerializer.Serialize(new
                    {
                        type = "event",
                        eventName = GetEventName(evt),
                        streamId = sid,
                        sequence,
                        timestamp,
                        data = GetEventData(evt)
                    }, JsonOptions.Default);
                    _eventPusher.Push(json);
                    if (evt is AgentDone)
                        lock (_streamsGate)
                            if (_webStreams.TryGetValue(p.ConversationId, out var current) && ReferenceEquals(current, journal))
                                _webStreams.Remove(p.ConversationId);
                    await Task.CompletedTask;
                });

            journal.StreamId = streamId;
            _webStreams[p.ConversationId] = journal;
            return Task.FromResult(streamId);
        }
    }

    private async Task<bool> CancelStreamAsync(CancelStreamParams p, CancellationToken ct)
    {
        await _agentService.CancelStreamAsync(p.StreamId, ct);
        return true;
    }

    // ── Skills ─────────────────────────────────────────────────────────────────

    private Task<List<SkillDto>> ListSkillsAsync(CancellationToken ct)
    {
        var skills = _store.Skills
            .OrderBy(s => s.Name)
            .Select(ToDto)
            .ToList();
        return Task.FromResult(skills);
    }

    private Task<SkillDto> ImportSkillAsync(ImportSkillParams p, CancellationToken ct)
    {
        var node = JsonSerializer.Deserialize<JsonElement>(p.Json);
        var skill = ParseSkillFromJson(node);
        _store.AddSkill(skill);
        return Task.FromResult(ToDto(skill));
    }

    private Task<List<SkillDto>> ImportSkillZipAsync(ImportSkillZipParams p, CancellationToken ct)
    {
        var zipBytes = Convert.FromBase64String(p.Base64Zip);
        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        var imported = new List<SkillDto>();

        var jsonEntries = archive.Entries
            .Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Length)
            .ToList();

        foreach (var entry in jsonEntries)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(json); }
            catch { continue; }

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (!item.TryGetProperty("name", out _)) continue;
                    var skill = ParseSkillFromJson(item);
                    _store.AddSkill(skill);
                    imported.Add(ToDto(skill));
                }
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("name", out _))
            {
                if (root.TryGetProperty("tools", out var toolsArr) && toolsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in toolsArr.EnumerateArray())
                    {
                        var inner = item.TryGetProperty("function", out var fn) ? fn : item;
                        if (!inner.TryGetProperty("name", out _)) continue;
                        var skill = ParseSkillFromJson(inner);
                        _store.AddSkill(skill);
                        imported.Add(ToDto(skill));
                    }
                }
                else
                {
                    var skill = ParseSkillFromJson(root);
                    _store.AddSkill(skill);
                    imported.Add(ToDto(skill));
                }
            }
        }

        if (imported.Count == 0)
            throw new Exception("ZIP 中未找到有效的 skill 定义（需要包含 name 字段的 JSON）");

        return Task.FromResult(imported);
    }

    private static Skill ParseSkillFromJson(JsonElement node) => new()
    {
        Name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
        Description = node.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
        ParametersJson = node.TryGetProperty("parameters", out var par) ? par.GetRawText() : "{}",
        CommandTemplate = node.TryGetProperty("command", out var cmd) ? cmd.GetString() : null,
        Type = node.TryGetProperty("command", out _) ? SkillType.Shell : SkillType.Custom,
    };

    private Task<bool> ToggleSkillAsync(ToggleSkillParams p, CancellationToken ct)
    {
        var skill = _store.GetSkill(p.Id);
        if (skill == null) return Task.FromResult(false);
        skill.IsEnabled = p.Enabled;
        _store.UpdateSkill(skill);
        return Task.FromResult(true);
    }

    private Task<bool> DeleteSkillAsync(DeleteSkillParams p, CancellationToken ct)
    {
        var skill = _store.GetSkill(p.Id);
        if (skill == null) return Task.FromResult(false);
        _store.DeleteSkill(p.Id);
        return Task.FromResult(true);
    }

    // ── API Configs ────────────────────────────────────────────────────────────

    private Task<List<ApiConfigDto>> ListApiConfigsAsync(CancellationToken ct)
    {
        var configs = _store.ApiConfigs
            .Select(ToDto)
            .ToList();
        return Task.FromResult(configs);
    }

    private Task<ApiConfigDto> SaveApiConfigAsync(SaveApiConfigParams p, CancellationToken ct)
    {
        ApiConfig config;
        bool isNew = false;
        if (p.Id.HasValue)
        {
            config = _store.GetApiConfig(p.Id.Value)
                ?? throw new Exception("ApiConfig not found");
        }
        else
        {
            config = new ApiConfig();
            isNew = true;
        }

        config.ProviderId = p.ProviderId;
        config.DisplayName = p.DisplayName;
        config.BaseUrl = p.BaseUrl;
        if (!string.IsNullOrEmpty(p.ApiKey) && p.ApiKey != "••••••••") config.ApiKey = p.ApiKey;
        config.DefaultModel = p.DefaultModel;
        config.Temperature = p.Temperature;
        config.MaxTokens = p.MaxTokens;
        config.IsEnabled = p.IsEnabled;
        config.ExtraHeadersJson = p.ExtraHeadersJson;
        config.ThinkingEnabled = p.ThinkingEnabled;
        config.ThinkingBudget = p.ThinkingBudget;
        if (p.ContextWindowTokens is < 1000 or > 10_000_000) throw new ArgumentException("上下文容量需要在 1,000 – 10,000,000 tokens 之间。");
        config.ContextWindowTokens = p.ContextWindowTokens;
        config.Protocol = p.Protocol ?? "";
        config.ReasoningProtocol = string.IsNullOrWhiteSpace(p.ReasoningProtocol) ? "auto" : p.ReasoningProtocol;
        config.ReasoningEffort = string.IsNullOrWhiteSpace(p.ReasoningEffort) ? "default" : p.ReasoningEffort;
        config.ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(p.ApiKeyEnvironmentVariable) ? null : p.ApiKeyEnvironmentVariable.Trim();
        config.AuthenticationProviderId = string.IsNullOrWhiteSpace(p.AuthenticationProviderId) ? null : p.AuthenticationProviderId.Trim();
        if (p.Models is { Count: > 0 })
        {
            if (p.Models.Count > 2000 || p.Models.Any(model => string.IsNullOrWhiteSpace(model) || model.Length > 300 || model.Any(char.IsControl))) throw new ArgumentException("模型列表需要 1–2000 个有效 ID。");
            config.Models = p.Models.Distinct(StringComparer.Ordinal).ToList();
        }
        if (!string.IsNullOrWhiteSpace(config.DefaultModel) && !config.Models.Contains(config.DefaultModel)) config.Models.Insert(0, config.DefaultModel);

        if (isNew)
            _store.AddApiConfig(config);
        else
            _store.UpdateApiConfig(config);

        return Task.FromResult(ToDto(config));
    }

    private Task<bool> DeleteApiConfigAsync(DeleteApiConfigParams p, CancellationToken ct)
    {
        var config = _store.GetApiConfig(p.Id);
        if (config == null) return Task.FromResult(false);
        _store.DeleteApiConfig(p.Id);
        return Task.FromResult(true);
    }

    private async Task<bool> TestApiConfigAsync(TestApiConfigParams p, CancellationToken ct)
    {
        var config = _store.GetApiConfig(p.Id)
            ?? throw new Exception("ApiConfig not found");
        var provider = _agentService.ProviderResolver?.Invoke(config) ?? _providerFactory.GetProvider(config);

        // Try a minimal chat to verify the endpoint works.
        // ListModelsAsync is unreliable (many providers don't support it),
        // so we always do a real streaming test.
        var testRequest = new ChatRequest(config,
            new List<ChatMessage> { new("user", "hi") }, null);

        try
        {
            await foreach (var chunk in provider.StreamChatAsync(testRequest, ct))
            {
                if (chunk.Type == ChunkType.Error)
                    throw new Exception(chunk.ErrorMessage ?? "Connection failed");
                // If we get any non-error chunk, the connection is working.
                // We intentionally consume at least one chunk before declaring success.
                if (chunk.Type == ChunkType.TextDelta || chunk.Type == ChunkType.Done)
                    break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex.Message.Contains("Connection failed")) { throw; }
        catch (Exception ex)
        {
            throw new Exception($"Connection test failed: {ex.Message}");
        }

        return true;
    }

    private async Task<ApiConfigDto> RefreshModelsAsync(TestApiConfigParams p, CancellationToken ct)
    {
        var config = _store.GetApiConfig(p.Id) ?? throw new Exception("ApiConfig not found");
        var provider = _agentService.ProviderResolver?.Invoke(config) ?? _providerFactory.GetProvider(config);
        var models = await provider.ListModelsAsync(config, ct);
        if (models.Count == 0) throw new InvalidOperationException("接口返回了空模型列表，已有列表未修改。");
        config.Models = models.Select(model => model.Id).ToList();
        ModelCatalog.Remember(config, models);
        _store.UpdateApiConfig(config);
        return ToDto(config);
    }

    private Task<ReasoningOptionsDto> GetReasoningAsync(TestApiConfigParams p, CancellationToken ct)
    {
        var config = _store.GetApiConfig(p.Id) ?? throw new Exception("ApiConfig not found");
        var capability = ReasoningSettings.Capabilities(config);
        return Task.FromResult(new ReasoningOptionsDto(capability.Protocol, ReasoningSettings.Protocols.ToArray(), ReasoningSettings.Efforts(config).ToArray(), config.ReasoningEffort, config.ThinkingBudget, capability.MinimumBudget, capability.MaximumBudget, ReasoningSettings.Label(config)));
    }

    // ── Settings ───────────────────────────────────────────────────────────────

private Task<SettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var settings = _store.GetAllSettings();
        return Task.FromResult(new SettingsDto(
            settings.GetValueOrDefault("theme", "system"),
            settings.GetValueOrDefault("language", "zh"),
            settings.GetValueOrDefault("defaultModel", ""),
            settings.GetValueOrDefault("streamingEnabled", "true") == "true",
            settings.GetValueOrDefault("defaultProviderId", ""),
            _agentService.BrowserUse.Snapshot().Configuration.Enabled,
            _agentService.BrowserUse.Snapshot().Configuration.Mode,
            _agentService.BrowserUse.Snapshot().Configuration.ExecutablePath,
            _agentService.BrowserUse.Snapshot().Configuration.CdpUrl
        ));
    }

    private Task<bool> SaveSettingsAsync(SaveSettingsParams p, CancellationToken ct)
    {
        _agentService.BrowserUse.Save(_store, new(p.BrowserUseEnabled, p.BrowserUseMode, p.BrowserExecutablePath, p.BrowserCdpUrl));
        _store.SetSetting("theme", p.Theme);
        _store.SetSetting("language", p.Language);
        _store.SetSetting("defaultModel", p.DefaultModel);
        _store.SetSetting("streamingEnabled", p.StreamingEnabled.ToString().ToLower());
        _store.SetSetting("defaultProviderId", p.DefaultProviderId);
        return Task.FromResult(true);
    }

    // ── MCP Server Configs ────────────────────────────────────────────────

    private Task<List<McpServerConfigDto>> ListMcpServerConfigsAsync(CancellationToken ct)
    {
        var configs = _store.McpServerConfigs
            .OrderBy(c => c.Name)
            .Select(ToDto)
            .ToList();
        return Task.FromResult(configs);
    }

    private async Task<McpServerConfigDto> SaveMcpServerConfigAsync(SaveMcpServerConfigParams p, CancellationToken ct)
    {
        McpServerConfig config;
        bool isNew = string.IsNullOrEmpty(p.Id);
        if (!isNew)
        {
            config = _store.GetMcpServerConfig(p.Id!) ?? throw new Exception("McpServerConfig not found");
            await _mcpToolService.DisconnectServerAsync(config.Id);
        }
        else
        {
            config = new McpServerConfig();
        }

        config.Name = p.Name;
        config.Command = p.Command;
        config.Arguments = p.Arguments;
        config.IsEnabled = p.IsEnabled;

        if (isNew)
            _store.AddMcpServerConfig(config);
        else
            _store.UpdateMcpServerConfig(config);

        return ToDto(config);
    }

    private async Task<bool> DeleteMcpServerConfigAsync(DeleteMcpServerConfigParams p, CancellationToken ct)
    {
        var config = _store.GetMcpServerConfig(p.Id);
        if (config == null) return false;
        await _mcpToolService.DisconnectServerAsync(p.Id);
        _store.DeleteMcpServerConfig(p.Id);
        return true;
    }

    private async Task<bool> ConnectMcpServerAsync(ConnectMcpServerParams p, CancellationToken ct)
    {
        var config = _store.GetMcpServerConfig(p.Id);
        if (config == null) return false;
        if (!config.IsEnabled) throw new InvalidOperationException("请先启用此 MCP 配置，再手动连接。");
        return await _mcpToolService.ConnectServerAsync(config, ct);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ConversationDto ToDto(Conversation c) =>
        new(c.Id, c.Title, c.ModelId, c.ProviderId, c.CreatedAt, c.UpdatedAt);

    private static MessageDto ToDto(Message m) =>
        new(m.Id, m.Role, m.Role == "user" ? m.InputText ?? m.Content : m.Content, m.ToolCallsJson, m.ToolCallId, m.ToolName, m.ReasoningContent, m.CreatedAt, m.HostToolTraceJson, m.IsHostActivity, m.IsInterrupted);

    private static SkillDto ToDto(Skill s) =>
        new(s.Id, s.Name, s.Description, s.ParametersJson, s.CommandTemplate, s.Type.ToString(), s.IsEnabled);

    public static ApiConfigDto ToDto(ApiConfig c) =>
        new(c.Id, c.ProviderId, c.DisplayName, c.BaseUrl,
            string.IsNullOrEmpty(c.ApiKey) ? "" : "••••••••",
            c.DefaultModel, c.Temperature, c.MaxTokens, c.IsEnabled,
            c.ExtraHeadersJson,
            c.ThinkingEnabled, c.ThinkingBudget,
            c.ContextWindowTokens, c.Protocol, c.ReasoningProtocol, c.ReasoningEffort,
            c.Models.ToList(), c.ApiKeyEnvironmentVariable, c.AuthenticationProviderId,
            ModelContext.Capacity(c), ProviderCatalog.ProtocolFor(c));

    private McpServerConfigDto ToDto(McpServerConfig c) =>
        new(c.Id, c.Name, c.Command, c.Arguments, c.IsEnabled, c.CreatedAt, _mcpToolService.IsConnected(c.Id));

    private static string GetEventName(AgentEvent evt) => evt switch
    {
        TokenDelta => "agent.token",
        ThinkingDelta => "agent.thinking",
        ToolCallStarted => "agent.toolCallStarted",
        // Policy rejections are recoverable: the run continues, so this gets
        // its own event instead of riding on the fatal agent.error channel.
        ToolCallRejected => "agent.toolCallRejected",
        ToolCallOutput => "agent.toolCallOutput",
        ToolCallCompleted => "agent.toolCallCompleted",
        AgentIteration => "agent.iteration",
        AgentDone => "agent.done",
        AgentError => "agent.error",
        AgentNotice => "agent.notice",
        AgentContextUsage => "agent.contextUsage",
        _ => "agent.unknown"
    };

    private static object GetEventData(AgentEvent evt) => evt switch
    {
        TokenDelta e => new { text = e.Text },
        ThinkingDelta e => new { text = e.Text },
        ToolCallStarted e => new { id = e.Id, name = e.ToolName, args = e.ArgsJson, parentId = e.ParentId },
        ToolCallRejected e => new { id = e.Id, tool = e.ToolName, reason = e.Reason, message = "未执行 " + e.ToolName + ": " + e.Reason },
        ToolCallOutput e => new { id = e.Id, line = e.Line, isError = e.IsError },
        ToolCallCompleted e => new { id = e.Id, name = e.ToolName, result = e.ResultJson },
        AgentIteration e => new { current = e.Current, maximum = e.Maximum, toolCalls = e.ToolCalls },
        AgentDone e => new { reason = e.FinishReason },
        AgentError e => new { message = e.Message },
        AgentNotice e => new { message = e.Message, resetResponse = e.ResetResponse },
        AgentContextUsage e => new { usedTokens = e.UsedTokens, capacityTokens = e.CapacityTokens, estimated = e.Estimated },
        _ => new { }
    };
}
