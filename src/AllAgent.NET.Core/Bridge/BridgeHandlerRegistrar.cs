using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Localization;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Services.Updates;

namespace AllAgent.NET.Core.Bridge;

// DTOs
public record ConversationDto(Guid Id, string Title, string ModelId, string ProviderId, DateTime CreatedAt, DateTime UpdatedAt);
public record MessageDto(Guid Id, string Role, string Content, string? ToolCallsJson, string? ToolCallId, string? ToolName, string? ReasoningContent, DateTime CreatedAt, string? HostToolTraceJson, bool IsHostActivity = false, bool IsInterrupted = false, IReadOnlyList<ImageAttachment>? Images = null)
{
    public static MessageDto From(Message message) =>
        new(message.Id, message.Role, message.Role == "user" ? message.InputText ?? message.Content : message.Content, message.ToolCallsJson, message.ToolCallId,
            message.ToolName, message.ReasoningContent, message.CreatedAt, message.HostToolTraceJson, message.IsHostActivity, message.IsInterrupted,
            message.Images.Count > 0 ? message.Images : null);
}
public record TaskParams(string Id);
public record SkillDto(Guid Id, string Name, string Description, string ParametersJson, string? CommandTemplate, string Type, bool IsEnabled);
public record ApiConfigDto(Guid Id, string ProviderId, string DisplayName, string BaseUrl, string ApiKey, string DefaultModel, double Temperature, int MaxTokens, bool IsEnabled, string? ExtraHeadersJson, bool ThinkingEnabled, int ThinkingBudget,
    int ContextWindowTokens = 100000, string Protocol = "", string ReasoningProtocol = "auto", string ReasoningEffort = "default",
    List<string>? Models = null, string? ApiKeyEnvironmentVariable = null, string? AuthenticationProviderId = null,
    int EffectiveContextTokens = 0, string EffectiveProtocol = "", int EffectiveMaxTokens = 0, Dictionary<string, int>? ModelContextWindows = null);
public record McpServerConfigDto(string Id, string Name, string Command, string Arguments, bool IsEnabled, DateTime CreatedAt, bool IsConnected = false);
public record SettingsDto(string Theme, string Language, string DefaultModel, bool StreamingEnabled, string DefaultProviderId, bool BrowserUseEnabled = true, string BrowserUseMode = "builtin", string BrowserExecutablePath = "", string BrowserCdpUrl = "", string GitCommitConfigId = "", string LanguageResolved = "zh");
public record ToolInfoDto(string Name, string Description, string Risk, string ParametersJson, string Source, string Owner);
public record ToolsSnapshotDto(IReadOnlyList<ToolInfoDto> Tools, IReadOnlyList<string> Errors);
public record UpdateStatusDto(string CurrentVersion, string? LatestVersion, bool UpdateAvailable, string Source,
    string InstallKind, string Architecture, string? ReleaseUrl, string? ReleaseNotes, string? Error,
    string? LastResult, string? LastError, DateTimeOffset CheckedAt, bool FromCache, string? LogPath);
public record UpdateCheckParams(string? Source = null, string? Channel = null, bool? Force = null);
public record UpdateInstallParams(string? Source = null, string? Channel = null, bool? AllowUnsigned = null);
public record UpdateSettingsDto(string Source = "auto", string Channel = "stable", bool AutoCheck = true,
    bool AutoInstall = false, bool RequireSignature = false, bool RequireChecksum = true);
public record UpdateInvokeResult(bool Started, string Message, string? Version = null, string? LogPath = null);

// Request params
public record CreateConversationParams(string Title = "New Conversation");
public record RenameConversationParams(Guid Id, string Title);
public record DeleteConversationParams(Guid Id);
public record GetMessagesParams(Guid ConversationId);
public record SetTodosParams(Guid ConversationId, List<TodoItem> Todos);
public record StartStreamParams(Guid ConversationId, string Message, Guid ApiConfigId, bool ThinkingEnabled = false, int ThinkingBudget = 8000, string? Model = null, string? ReasoningEffort = null, List<ImageAttachment>? Images = null, string? Prompt = null);
public record StartCompactParams(Guid ConversationId, Guid ApiConfigId, string? Focus = null);
public record CancelStreamParams(string StreamId);
public record EnqueueInputParams(Guid ConversationId, string Message, string Delivery = "steer", string? Prompt = null, List<ImageAttachment>? Images = null);
public record CancelPendingInputParams(Guid ConversationId, Guid Id);
public record RunPendingParams(Guid ConversationId, Guid ApiConfigId, bool ThinkingEnabled = false, int ThinkingBudget = 8000, string? Model = null, string? ReasoningEffort = null);
public record PendingInputDto(Guid Id, string Draft, string Delivery, DateTime CreatedAt);
public record ImportSkillParams(string Json);
public record ImportSkillZipParams(string Base64Zip);
public record ToggleSkillParams(Guid Id, bool Enabled);
public record DeleteSkillParams(Guid Id);
public record SaveApiConfigParams(Guid? Id, string ProviderId, string DisplayName, string BaseUrl, string ApiKey, string DefaultModel, double Temperature, int MaxTokens, bool IsEnabled, string? ExtraHeadersJson, bool ThinkingEnabled = false, int ThinkingBudget = 8000,
    int ContextWindowTokens = 100000, string Protocol = "", string ReasoningProtocol = "auto", string ReasoningEffort = "default",
    List<string>? Models = null, string? ApiKeyEnvironmentVariable = null, string? AuthenticationProviderId = null, Dictionary<string, int>? ModelContextWindows = null);
public record ReasoningParams(Guid Id, string? Model = null);
public record ModelContextParams(Guid Id, string? Model = null);
public record SetModelContextParams(Guid Id, string? Model = null, int Tokens = 0);
public record ModelContextDto(string Model, int Capacity, int? Manual, int DefaultWindow);
public record ReasoningOptionsDto(string Protocol, IReadOnlyList<string> Protocols, IReadOnlyList<string> Efforts, string Effort, int Budget, int MinimumBudget, int MaximumBudget, string Label);
public record DeleteApiConfigParams(Guid Id);
public record TestApiConfigParams(Guid Id);
public record SaveSettingsParams(string Theme, string Language, string DefaultModel, bool StreamingEnabled, string DefaultProviderId, bool BrowserUseEnabled = true, string BrowserUseMode = "builtin", string BrowserExecutablePath = "", string BrowserCdpUrl = "", string GitCommitConfigId = "");
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
        // Updates
        _dispatcher.Register<UpdateStatusDto>("update.status", ct => UpdateStatusAsync(false, ct, null, null));
        _dispatcher.Register<UpdateCheckParams, UpdateStatusDto>("update.check", (parameters, ct) =>
            UpdateStatusAsync(parameters.Force ?? true, ct, parameters.Source, parameters.Channel));
        _dispatcher.Register<UpdateInstallParams, UpdateInvokeResult>("update.install", InstallUpdateAsync);
        _dispatcher.Register<UpdateSettingsDto>("update.settings.get", _ => Task.FromResult(UpdateSettingsSnapshot()));
        _dispatcher.Register<UpdateSettingsDto, bool>("update.settings.set", (parameters, _) =>
        {
            var settings = new UpdateSettings(
                UpdateSettings.ParseSource(parameters.Source) ?? UpdateSource.Auto,
                UpdateSettings.ParseChannel(parameters.Channel) ?? UpdateChannel.Stable,
                parameters.AutoCheck,
                parameters.AutoInstall,
                parameters.RequireSignature,
                parameters.RequireChecksum);
            settings.Save(_store.SetSetting);
            return Task.FromResult(true);
        });

        // Conversations
        _dispatcher.Register("conversations.list", ListConversationsAsync);
        _dispatcher.Register<CreateConversationParams, ConversationDto>("conversations.create", CreateConversationAsync);
        _dispatcher.Register<RenameConversationParams, bool>("conversations.rename", RenameConversationAsync);
        _dispatcher.Register<DeleteConversationParams, bool>("conversations.delete", DeleteConversationAsync);
        _dispatcher.Register<GetMessagesParams, List<MessageDto>>("conversations.getMessages", GetMessagesAsync);
        _dispatcher.Register<GetMessagesParams, IReadOnlyList<TodoItem>>("todos.get", (p, ct) => Task.FromResult(_store.GetTodos(p.ConversationId)));
        _dispatcher.Register<GetMessagesParams, PlanDocument?>("plans.get", (p, ct) => Task.FromResult(_store.GetPlan(p.ConversationId)));
        _dispatcher.Register<SetTodosParams, IReadOnlyList<TodoItem>>("todos.set", (p, ct) =>
        {
            if (_agentService.IsRunning(p.ConversationId)) throw new InvalidOperationException("请先停止当前生成再编辑 TODO。");
            return Task.FromResult(_store.SetTodos(p.ConversationId, p.Todos));
        });

        // Chat streaming
        _dispatcher.Register<StartStreamParams, string>("chat.startStream", StartStreamAsync);
        _dispatcher.Register<StartCompactParams, string>("chat.compact", StartCompactAsync);
        _dispatcher.Register<CancelStreamParams, bool>("chat.cancelStream", CancelStreamAsync);
        _dispatcher.Register<GetMessagesParams, object?>("chat.getStreamState", (p, _) =>
        {
            lock (_streamsGate)
                return Task.FromResult(_webStreams.TryGetValue(p.ConversationId, out var stream)
                    ? stream.Snapshot(GetEventName, GetEventData) : null);
        });
        _dispatcher.Register<GetMessagesParams, object?>("chat.promptDiagnostics", (p, _) => Task.FromResult(_agentService.PromptDiagnostics(p.ConversationId)));
        // Input admitted while a turn is running: steer reaches the next model step, queue waits
        // for the run to finish and is then started through chat.runPending.
        _dispatcher.Register<EnqueueInputParams, PendingInputDto>("chat.enqueueInput", EnqueueInputAsync);
        _dispatcher.Register<GetMessagesParams, IReadOnlyList<PendingInputDto>>("chat.pendingInputs", (p, _) =>
            Task.FromResult<IReadOnlyList<PendingInputDto>>(_store.GetPendingInputs(p.ConversationId)
                .Select(item => new PendingInputDto(item.Id, item.Draft, item.Delivery, item.CreatedAt)).ToArray()));
        _dispatcher.Register<CancelPendingInputParams, bool>("chat.cancelPendingInput", (p, _) =>
        {
            _store.CancelPendingInput(p.ConversationId, p.Id);
            return Task.FromResult(true);
        });
        _dispatcher.Register<RunPendingParams, string?>("chat.runPending", RunPendingAsync);
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
        _dispatcher.Register<ReasoningParams, ReasoningOptionsDto>("apiConfigs.reasoning", GetReasoningAsync);
        _dispatcher.Register<ModelContextParams, ModelContextDto>("apiConfigs.context", GetModelContextAsync);
        _dispatcher.Register<SetModelContextParams, ModelContextDto>("apiConfigs.setContext", SetModelContextAsync);

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

        // Tools catalog
        _dispatcher.Register<ToolsSnapshotDto>("tools.list", ListToolsAsync);
    }

    // ── Tools catalog ──────────────────────────────────────────────────────────

    /// <summary>Every tool the current host configuration exposes, with source attribution so
    /// the web tool browser can tell built-ins from MCP server tools. Hosts with plugin
    /// support re-register `tools.list` and layer their own attribution on top.</summary>
    public async Task<ToolsSnapshotDto> ListToolsAsync(CancellationToken ct)
    {
        var errors = new List<string>();
        // MCP tools are namespaced as mcp_<server-hash>_<tool>; rebuild the mapping to attribute them.
        var mcpOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var server in _mcpToolService.GetAllServerTools())
        {
            var prefix = "mcp_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.ServerId)))[..8].ToLowerInvariant();
            foreach (var tool in server.Tools) mcpOwners[prefix + "_" + tool.Name] = server.Name;
        }
        var tools = new List<ToolInfoDto>();
        try
        {
            var listing = await _agentService.ListToolsAsync(ct);
            foreach (var definition in listing.Tools)
            {
                // On-demand families stay registered but out of the model catalog until their skill loads.
                var capability = listing.Surface.Match(definition.Name);
                mcpOwners.TryGetValue(definition.Name, out var server);
                var source = capability != null ? "on-demand"
                    : server != null ? "mcp"
                    : definition.Name.StartsWith("skill_", StringComparison.Ordinal) ? "skill" : "host";
                tools.Add(new ToolInfoDto(definition.Name, definition.Description, definition.Risk.ToString().ToLowerInvariant(), definition.Parameters.GetRawText(), source, capability?.Label ?? server ?? ""));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add("工具目录暂不可用：" + ex.Message);
        }
        return new ToolsSnapshotDto(tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray(), errors);
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

    private async Task<bool> DeleteConversationAsync(DeleteConversationParams p, CancellationToken ct)
    {
        if (_agentService.IsRunning(p.Id)) throw new InvalidOperationException("请先停止当前生成再删除会话。");
        var conv = _store.GetConversation(p.Id);
        if (conv == null) return false;
        await _agentService.ReleaseRuntimeAsync(p.Id);
        _store.DeleteConversation(p.Id);
        return true;
    }

    private Task<List<MessageDto>> GetMessagesAsync(GetMessagesParams p, CancellationToken ct)
    {
        var msgs = _store.GetVisibleMessages(p.ConversationId).Where(message => !message.IsInternal)
            .Select(ToDto)
            .ToList();
        return Task.FromResult(msgs);
    }

    // ── Chat Streaming ─────────────────────────────────────────────────────────

    private async Task<string> StartStreamAsync(StartStreamParams p, CancellationToken ct)
    {
        if (_store.GetConversation(p.ConversationId) == null) throw new ArgumentException("会话不存在。");
        if (string.IsNullOrWhiteSpace(p.Message)) throw new ArgumentException("消息不能为空。");
        var config = _store.GetApiConfig(p.ApiConfigId)
            ?? throw new Exception($"ApiConfig {p.ApiConfigId} not found");
        if (!config.IsEnabled) throw new InvalidOperationException("所选模型配置已停用。");

        // `@` references resolve exactly like terminal input: text files are inlined and
        // images attached, then the composer's own attachments are added on top. A `Prompt`
        // override (an expanded skill) is already final — it is never re-expanded, and only
        // `Message` stays visible in the transcript, matching the terminal's skill branch.
        // A token the browser could not complete (a typo, or prose like `@someone`) stays as
        // plain text instead of failing the whole message.
        var expanded = string.IsNullOrEmpty(p.Prompt)
            ? await PromptReferences.ExpandWithImagesAsync(p.Message, _agentService.WorkspaceRoot, ct,
                allowExplicitExternalImages: true, unresolved: PromptReferences.UnresolvedReference.KeepAsText)
            : new PromptReferences.ExpandedPrompt(p.Prompt!, Array.Empty<ImageAttachment>());
        var images = new List<ImageAttachment>(expanded.Images);
        if (p.Images is { Count: > 0 }) images.AddRange(p.Images);
        ImageAttachment.Validate(images);

        config = WithChatOverrides(config, p.ThinkingEnabled, p.ThinkingBudget, p.Model, p.ReasoningEffort);

        lock (_streamsGate)
        {
            if (_agentService.IsRunning(p.ConversationId)) throw new InvalidOperationException("This conversation already has an active run.");
            var journal = new BridgeStreamJournal(_store.GetVisibleMessages(p.ConversationId).Where(message => !message.IsInternal).ToList(), p.Message);
            var streamId = _agentService.StartStream(p.ConversationId, expanded.Text, config,
                StreamForwarder(p.ConversationId, journal), images: images, inputText: p.Message);

            journal.StreamId = streamId;
            _webStreams[p.ConversationId] = journal;
            return streamId;
        }
    }

    /// <summary>Expands `@` references exactly like a submitted turn, then parks the prompt in the
    /// durable inbox. A steer is admitted at the next model step; a queue entry waits for idle.</summary>
    private async Task<PendingInputDto> EnqueueInputAsync(EnqueueInputParams p, CancellationToken ct)
    {
        if (_store.GetConversation(p.ConversationId) == null) throw new ArgumentException("会话不存在。");
        if (string.IsNullOrWhiteSpace(p.Message)) throw new ArgumentException("消息不能为空。");
        var expanded = string.IsNullOrEmpty(p.Prompt)
            ? await PromptReferences.ExpandWithImagesAsync(p.Message, _agentService.WorkspaceRoot, ct,
                allowExplicitExternalImages: true, unresolved: PromptReferences.UnresolvedReference.KeepAsText)
            : new PromptReferences.ExpandedPrompt(p.Prompt!, Array.Empty<ImageAttachment>());
        var images = new List<ImageAttachment>(expanded.Images);
        if (p.Images is { Count: > 0 }) images.AddRange(p.Images);
        ImageAttachment.Validate(images);
        var item = _store.EnqueueInput(p.ConversationId, p.Message, expanded.Text, images, p.Delivery);
        return new PendingInputDto(item.Id, item.Draft, item.Delivery, item.CreatedAt);
    }

    /// <summary>Starts the next parked prompt as its own turn. The engine promotes the pending
    /// items itself, so the queued text reaches history exactly once and emits AgentInputAdmitted.</summary>
    private Task<string?> RunPendingAsync(RunPendingParams p, CancellationToken ct)
    {
        if (_store.GetConversation(p.ConversationId) == null) throw new ArgumentException("会话不存在。");
        var config = _store.GetApiConfig(p.ApiConfigId) ?? throw new Exception($"ApiConfig {p.ApiConfigId} not found");
        if (!config.IsEnabled) throw new InvalidOperationException("所选模型配置已停用。");
        config = WithChatOverrides(config, p.ThinkingEnabled, p.ThinkingBudget, p.Model, p.ReasoningEffort);

        lock (_streamsGate)
        {
            if (_agentService.IsRunning(p.ConversationId)) return Task.FromResult<string?>(null);
            var pending = _store.GetPendingInputs(p.ConversationId);
            var next = pending.FirstOrDefault(item => item.Delivery == "steer") ?? pending.FirstOrDefault();
            if (next == null) return Task.FromResult<string?>(null);
            var journal = new BridgeStreamJournal(_store.GetVisibleMessages(p.ConversationId).Where(message => !message.IsInternal).ToList(), next.Draft);
            var streamId = _agentService.StartStream(p.ConversationId, next.Content, config,
                StreamForwarder(p.ConversationId, journal), images: next.Images, inputText: next.Draft, fromInbox: true);

            journal.StreamId = streamId;
            _webStreams[p.ConversationId] = journal;
            return Task.FromResult<string?>(streamId);
        }
    }

    /// <summary>Chat-level model and thinking overrides share the base provider credentials.</summary>
    private static ApiConfig WithChatOverrides(ApiConfig config, bool thinkingEnabled, int thinkingBudget, string? model, string? reasoningEffort) => new()
    {
        Id = config.Id,
        ProviderId = config.ProviderId,
        DisplayName = config.DisplayName,
        BaseUrl = config.BaseUrl,
        ApiKey = config.ApiKey,
        DefaultModel = string.IsNullOrWhiteSpace(model) ? config.DefaultModel : model!,
        Temperature = config.Temperature,
        MaxTokens = config.MaxTokens,
        IsEnabled = config.IsEnabled,
        ExtraHeadersJson = config.ExtraHeadersJson,
        ThinkingEnabled = thinkingEnabled,
        ThinkingBudget = thinkingBudget,
        ReasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? config.ReasoningEffort : reasoningEffort!,
        ReasoningProtocol = config.ReasoningProtocol,
        ContextWindowTokens = config.ContextWindowTokens,
        ModelContextWindows = config.ModelContextWindows,
        ModelLimits = config.ModelLimits,
        ModelReasoningEfforts = config.ModelReasoningEfforts,
        ModelThinkingBudgets = config.ModelThinkingBudgets,
        ModelReasoningLevels = config.ModelReasoningLevels,
        Models = config.Models,
        Protocol = config.Protocol,
        AuthenticationProviderId = config.AuthenticationProviderId,
        ApiKeyEnvironmentVariable = config.ApiKeyEnvironmentVariable,
    };

    // Compaction only summarizes existing context, so it runs as an internal turn without the
    // sandbox probe and without chat-level model overrides; everything else (journal, stream
    // bookkeeping, event shape) matches a normal web stream.
    private Task<string> StartCompactAsync(StartCompactParams p, CancellationToken ct)
    {
        if (_store.GetConversation(p.ConversationId) == null) throw new ArgumentException("会话不存在。");
        var config = _store.GetApiConfig(p.ApiConfigId) ?? throw new Exception($"ApiConfig {p.ApiConfigId} not found");
        if (!config.IsEnabled) throw new InvalidOperationException("所选模型配置已停用。");

        lock (_streamsGate)
        {
            if (_agentService.IsRunning(p.ConversationId)) throw new InvalidOperationException("This conversation already has an active run.");
            var journal = new BridgeStreamJournal(_store.GetVisibleMessages(p.ConversationId).Where(message => !message.IsInternal).ToList(), "/compact");
            var streamId = _agentService.StartStream(p.ConversationId, p.Focus ?? string.Empty, config,
                StreamForwarder(p.ConversationId, journal), internalMessage: true, sessionOperation: "compact");

            journal.StreamId = streamId;
            _webStreams[p.ConversationId] = journal;
            return Task.FromResult(streamId);
        }
    }

    private Func<string, AgentEvent, Task> StreamForwarder(Guid conversationId, BridgeStreamJournal journal) =>
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
                    if (_webStreams.TryGetValue(conversationId, out var current) && ReferenceEquals(current, journal))
                        _webStreams.Remove(conversationId);
            await Task.CompletedTask;
        };

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
        if (p.MaxTokens < 0) throw new ArgumentException("最大输出 Tokens 需要是 0（自动）或正整数。");
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
        if (p.ModelContextWindows != null)
        {
            if (p.ModelContextWindows.Count > 2000) throw new ArgumentException("上下文容量最多记录 2000 个模型。");
            foreach (var (model, tokens) in p.ModelContextWindows)
                if (string.IsNullOrWhiteSpace(model) || model.Length > 300) throw new ArgumentException("模型 ID 需要 1–300 个字符。");
            config.ModelContextWindows = p.ModelContextWindows
                .Where(item => item.Value > 0)
                .ToDictionary(item => item.Key, item => ModelContext.Validate(item.Value), StringComparer.Ordinal);
        }
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

    private static readonly ConcurrentDictionary<string, DateTimeOffset> CatalogRefreshAttempts = new(StringComparer.Ordinal);
    private static readonly TimeSpan CatalogRefreshRetry = TimeSpan.FromMinutes(10);

    private async Task<ReasoningOptionsDto> GetReasoningAsync(ReasoningParams p, CancellationToken ct)
    {
        var config = _store.GetApiConfig(p.Id) ?? throw new Exception("ApiConfig not found");
        var model = string.IsNullOrWhiteSpace(p.Model) ? config.DefaultModel : p.Model!;
        // Account catalogs advertise each model's native effort levels. A config saved before that
        // metadata existed would otherwise fall back to family guesses, so refresh once per model.
        if (NeedsCatalogMetadata(config, model))
            config = await RefreshCatalogForReasoningAsync(config, model, ct);
        if (model != config.DefaultModel)
            config = WithChatOverrides(config, config.ThinkingEnabled, config.ThinkingBudget, model, null);
        var capability = ReasoningSettings.Capabilities(config);
        return new ReasoningOptionsDto(capability.Protocol, ReasoningSettings.Protocols.ToArray(), ReasoningSettings.Efforts(config).ToArray(), config.ReasoningEffort, config.ThinkingBudget, capability.MinimumBudget, capability.MaximumBudget, ReasoningSettings.Label(config));
    }

    private static bool NeedsCatalogMetadata(ApiConfig config, string model) =>
        !string.IsNullOrWhiteSpace(model)
        && config.Models.Count > 0
        && !string.IsNullOrWhiteSpace(config.AuthenticationProviderId)
        && !config.ModelReasoningLevels.ContainsKey(model);

    /// <summary>Best-effort refresh of an account catalog that predates advertised effort levels.</summary>
    private async Task<ApiConfig> RefreshCatalogForReasoningAsync(ApiConfig config, string model, CancellationToken ct)
    {
        var key = config.Id + "\0" + model;
        var now = DateTimeOffset.UtcNow;
        if (CatalogRefreshAttempts.TryGetValue(key, out var last) && now - last < CatalogRefreshRetry) return config;
        CatalogRefreshAttempts[key] = now;
        try
        {
            // Never let a slow endpoint hold up the composer; the family fallback covers the wait.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var provider = _agentService.ProviderResolver?.Invoke(config) ?? _providerFactory.GetProvider(config);
            var models = await provider.ListModelsAsync(config, deadline.Token);
            if (models.Count == 0) return config;
            config.Models = models.Select(item => item.Id).ToList();
            ModelCatalog.Remember(config, models);
            _store.UpdateApiConfig(config);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Offline or a rejected credential keeps the saved catalog and the family fallback.
        }
        return config;
    }

    private Task<ModelContextDto> GetModelContextAsync(ModelContextParams p, CancellationToken ct)
    {
        var config = _store.GetApiConfig(p.Id) ?? throw new Exception("ApiConfig not found");
        return Task.FromResult(ContextDto(config, ContextModel(config, p.Model)));
    }

    private Task<ModelContextDto> SetModelContextAsync(SetModelContextParams p, CancellationToken ct)
    {
        var config = _store.GetApiConfig(p.Id) ?? throw new Exception("ApiConfig not found");
        var model = ContextModel(config, p.Model);
        if (model.Length == 0) throw new ArgumentException("请先选择模型。");
        if (p.Tokens == 0) config.ModelContextWindows.Remove(model);
        else config.ModelContextWindows[model] = ModelContext.Validate(p.Tokens);
        _store.UpdateApiConfig(config);
        return Task.FromResult(ContextDto(config, model));
    }

    private static string ContextModel(ApiConfig config, string? model) =>
        string.IsNullOrWhiteSpace(model) ? config.DefaultModel : model.Trim();

    private static ModelContextDto ContextDto(ApiConfig config, string model) =>
        new(model, ModelContext.Capacity(config, model), config.ModelContextWindows.TryGetValue(model, out var manual) ? manual : null, config.ContextWindowTokens);

    // ── Settings ───────────────────────────────────────────────────────────────

private Task<SettingsDto> GetSettingsAsync(CancellationToken ct)
{
    var settings = _store.GetAllSettings();
    var language = LanguageSetting.Current(_store);
    return Task.FromResult(new SettingsDto(
        settings.GetValueOrDefault("theme", "system"),
        language,
        settings.GetValueOrDefault("defaultModel", ""),
        settings.GetValueOrDefault("streamingEnabled", "true") == "true",
        settings.GetValueOrDefault("defaultProviderId", ""),
        _agentService.BrowserUse.Snapshot().Configuration.Enabled,
        _agentService.BrowserUse.Snapshot().Configuration.Mode,
        _agentService.BrowserUse.Snapshot().Configuration.ExecutablePath,
        _agentService.BrowserUse.Snapshot().Configuration.CdpUrl,
        settings.GetValueOrDefault("gitCommitConfigId", ""),
        HostText.ResolvedCode
    ));
}

private Task<bool> SaveSettingsAsync(SaveSettingsParams p, CancellationToken ct)
{
    var language = LanguageSetting.Values.Contains(p.Language) ? p.Language : LanguageSetting.Default;
    _agentService.BrowserUse.Save(_store, new(p.BrowserUseEnabled, p.BrowserUseMode, p.BrowserExecutablePath, p.BrowserCdpUrl));
    _store.SetSetting("theme", p.Theme);
    _store.SetSetting(LanguageSetting.Key, language);
    _store.SetSetting("defaultModel", p.DefaultModel);
    _store.SetSetting("streamingEnabled", p.StreamingEnabled.ToString().ToLower());
    _store.SetSetting("defaultProviderId", p.DefaultProviderId);
    _store.SetSetting("gitCommitConfigId", p.GitCommitConfigId ?? "");
    LanguageSetting.Apply(_store);
    _eventPusher.Push(JsonSerializer.Serialize(new
    {
        type = "settings.changed",
        language,
        languageResolved = HostText.ResolvedCode,
    }, JsonOptions.Default));
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

    private UpdateService CreateUpdateService() =>
        UpdateServiceFactory.Create(_store.SharedDirectoryPath ?? _store.DirectoryPath, _store.GetSetting);

    private async Task<UpdateStatusDto> UpdateStatusAsync(bool force, CancellationToken ct, string? source, string? channel)
    {
        var settings = UpdateSettings.FromSettings(_store.GetSetting);
        if (UpdateSettings.ParseSource(source) is { } parsedSource) settings = settings with { Source = parsedSource };
        if (UpdateSettings.ParseChannel(channel) is { } parsedChannel) settings = settings with { Channel = parsedChannel };
        var service = CreateUpdateService();
        var check = force
            ? await service.CheckAsync(settings, force: true, ct)
            : service.GetCachedResult(settings.Channel == UpdateChannel.Preview);
        var state = service.State.Load();
        return new UpdateStatusDto(
            check.CurrentVersion,
            check.LatestVersion,
            check.UpdateAvailable,
            check.Source.ToString(),
            check.InstallKind.ToString(),
            check.Architecture,
            check.ReleaseUrl ?? state.ReleaseUrl,
            check.Release?.Notes ?? state.ReleaseNotes,
            check.Error,
            state.LastResult,
            state.LastError,
            check.CheckedAt,
            check.FromCache,
            state.LastLogPath);
    }

    private async Task<UpdateInvokeResult> InstallUpdateAsync(UpdateInstallParams parameters, CancellationToken ct)
    {
        var settings = UpdateSettings.FromSettings(_store.GetSetting);
        if (UpdateSettings.ParseSource(parameters.Source) is { } parsedSource) settings = settings with { Source = parsedSource };
        if (UpdateSettings.ParseChannel(parameters.Channel) is { } parsedChannel) settings = settings with { Channel = parsedChannel };
        var service = CreateUpdateService();
        var check = await service.CheckAsync(settings, force: true, ct);
        if (check.Error != null) return new UpdateInvokeResult(false, check.Error);
        if (!check.UpdateAvailable) return new UpdateInvokeResult(false, "已是最新版本：" + check.CurrentVersion);
        var restartArgs = UpdateRuntimeContext.GuiLauncher
            ? UpdateRuntimeContext.RestartArguments
            : UpdateRuntimeContext.WebUiHost ? ["web-ui"] : UpdateRuntimeContext.RestartArguments;
        var options = new UpdateInstallOptions(
            settings,
            Force: false,
            AllowUnsigned: parameters.AllowUnsigned ?? !settings.RequireSignature,
            Restart: true,
            RestartArguments: restartArgs,
            WorkingDirectory: UpdateRuntimeContext.WorkingDirectory,
            GuiLauncher: UpdateRuntimeContext.GuiLauncher);
        var result = await service.InstallAsync(check, settings, options, ct);
        if (result.Started)
        {
            // Let the bridge response reach the browser before the host exits; the detached
            // updater waits for this process and restarts the chosen surface afterwards.
            _ = Task.Run(async () =>
            {
                await Task.Delay(900);
                Environment.Exit(0);
            });
        }
        return new UpdateInvokeResult(result.Started, result.Message, result.Version, result.LogPath);
    }

    private UpdateSettingsDto UpdateSettingsSnapshot()
    {
        var settings = UpdateSettings.FromSettings(_store.GetSetting);
        return new UpdateSettingsDto(
            settings.Source.ToString().ToLowerInvariant(),
            settings.Channel == UpdateChannel.Preview ? "pre" : "stable",
            settings.AutoCheck,
            settings.AutoInstall,
            settings.RequireSignature,
            settings.RequireChecksum);
    }

    private static ConversationDto ToDto(Conversation c) =>
        new(c.Id, c.Title, c.ModelId, c.ProviderId, c.CreatedAt, c.UpdatedAt);

    private static MessageDto ToDto(Message m) => MessageDto.From(m);

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
            ModelContext.Capacity(c), ProviderCatalog.ProtocolFor(c), ModelCatalog.OutputLimit(c), new Dictionary<string, int>(c.ModelContextWindows));

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
        AgentInputAdmitted => "agent.inputAdmitted",
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
        AgentInputAdmitted e => new { message = MessageDto.From(e.Message) },
        _ => new { }
    };
}
