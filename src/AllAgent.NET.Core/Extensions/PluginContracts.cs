using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Extensions;

/// <summary>Host SDK v1. Plugins run as trusted .NET code, not inside the Python microVM.</summary>
public interface IAllAgentPlugin : IAsyncDisposable
{
    string Id { get; }
    string Name { get; }
    void Register(PluginContext context);
    ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record PluginCommand(string Name, string Description, Func<string[], CancellationToken, Task<string>> Execute);
public enum HookStage { BeforeTurn, AfterTurn, TurnError, BeforeTool, AfterTool }
public sealed record HookEvent(HookStage Stage, Guid? SessionId = null, string? ToolName = null, bool? Ok = null, System.Text.Json.JsonElement? Arguments = null, string? UserMessage = null);
public sealed record HookDecision(bool Allow = true, string? Reason = null);
public sealed record AgentHook(string Name, Func<HookEvent, CancellationToken, ValueTask<HookDecision>> Invoke);

public interface IAuthenticationProvider
{
    string Id { get; }
    string Name { get; }
    string? LoginInstructions => null;
    Task<AuthenticationStatus> GetStatusAsync(CancellationToken ct) => AuthenticationStatus.FromTokenAsync(this, ct);
    void ValidateModelConfiguration(AllAgent.NET.Core.Models.ApiConfig config) { }
    Task<AuthenticationCredential?> GetCredentialAsync(CancellationToken ct) => AuthenticationCredential.FromTokenAsync(this, ct);
    Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct);
    Task<string?> GetAccessTokenAsync(CancellationToken ct);
    Task LogoutAsync(CancellationToken ct);
}

/// <summary>Optional: providers that take a paste-in secret (API key / token) instead of a browser flow.</summary>
public interface IInteractiveAuthentication
{
    /// <summary>One-line prompt shown in the host's secret input field.</summary>
    string InputPrompt { get; }
    /// <summary>Validates and stores the entered secret. Throw to surface the reason.</summary>
    Task LoginWithInputAsync(string input, CancellationToken ct);
}

public sealed record AuthenticationCredential(string AccessToken, string? ContextId = null)
{
    public string? InferenceBaseUrl { get; init; }
    public string? AccountId { get; init; }
    internal static async Task<AuthenticationCredential?> FromTokenAsync(IAuthenticationProvider provider, CancellationToken ct) =>
        await provider.GetAccessTokenAsync(ct) is { } token ? new(token) : null;
}

public sealed record AuthenticationStatus(bool LoggedIn, bool ModelConnectionAvailable = true, string? Account = null, string? Message = null)
{
    internal static async Task<AuthenticationStatus> FromTokenAsync(IAuthenticationProvider provider, CancellationToken ct) => new(await provider.GetAccessTokenAsync(ct) != null);
}

public sealed record AgentTaskLauncher(Func<string, string> Start);

/// <summary>Authentication plugins can connect their own model catalog after login.</summary>
public interface IAccountModelProvider
{
    AllAgent.NET.Core.Models.ApiConfig CreateConfiguration();
    IAiProvider ModelProvider { get; }
}

public sealed class PluginContext(string dataDirectory, IReadOnlyDictionary<Type, object>? services = null, string? packageId = null, PluginWebEvents? webEvents = null, PluginSettingsStore? settings = null)
{
    // Preserve the pre-settings constructor signature for already compiled plugins.
    public PluginContext(string dataDirectory, IReadOnlyDictionary<Type, object>? services, string? packageId, PluginWebEvents? webEvents)
        : this(dataDirectory, services, packageId, webEvents, null) { }

    private readonly PluginWebChannel? _web = webEvents == null ? null : new PluginWebChannel(packageId ?? "plugin", webEvents);
    private readonly PluginSettings? _settings = settings == null ? null : new(packageId ?? "plugin", settings);
    public string DataDirectory { get; } = dataDirectory;
    /// <summary>This package's namespace in the shared plugin settings store.</summary>
    public PluginSettings Settings => _settings ?? throw new InvalidOperationException("此宿主未提供插件设置存储。");
    /// <summary>Channel to the plugin's own browser frames: push events, everything else flows
    /// through the bridge methods registered with <see cref="AddWebBridge"/>.</summary>
    public PluginWebChannel Web => _web ?? throw new InvalidOperationException("此宿主未提供插件 Web 通道。");
    /// <summary>Methods exposed to the plugin's own frames as plugin.&lt;packageId&gt;.&lt;name&gt;.</summary>
    internal List<PluginWebBridgeHandler> WebBridges { get; } = [];
    public T GetService<T>() where T : class => services?.TryGetValue(typeof(T), out var service) == true ? (T)service : throw new InvalidOperationException("此宿主未提供服务：" + typeof(T).Name);
    internal List<IAgentToolSet> Tools { get; } = [];
    internal List<IAgentToolSet> WorkspaceIndependentTools { get; } = [];
    internal List<IAiProvider> Providers { get; } = [];
    internal List<ModelFooterProvider> FooterProviders { get; } = [];
    public void AddModelFooter(ModelFooterProvider provider) => FooterProviders.Add(provider);
    internal List<PluginCommand> Commands { get; } = [];
    internal List<AgentHook> Hooks { get; } = [];
    internal List<AgentTurnExtension> TurnExtensions { get; } = [];
    internal List<AgentRequestExtension> RequestExtensions { get; } = [];
    internal List<IAuthenticationProvider> Authentication { get; } = [];
    public void AddTools(IAgentToolSet tools) => Tools.Add(tools);
    /// <summary>Registers tools that do not capture a workspace store or local paths, so the host
    /// can safely use them in sessions launched in another workspace (e.g. remote account reads).</summary>
    public void AddWorkspaceIndependentTools(IAgentToolSet tools) { Tools.Add(tools); WorkspaceIndependentTools.Add(tools); }
    public void AddProvider(IAiProvider provider) => Providers.Add(provider);
    public void AddCommand(PluginCommand command) => Commands.Add(command);
    public void AddHook(AgentHook hook) => Hooks.Add(hook);
    public void AddTurnExtension(AgentTurnExtension extension) => TurnExtensions.Add(extension);
    public void AddRequestExtension(AgentRequestExtension extension) => RequestExtensions.Add(extension);
    public void AddAuthentication(IAuthenticationProvider authentication) => Authentication.Add(authentication);
    /// <summary>Exposes a backend method to this plugin's browser frames. Only frames of this
    /// package can reach it; host methods and other plugins stay out of scope.</summary>
    public void AddWebBridge(string name, Func<JsonElement?, CancellationToken, Task<JsonElement?>> handler) => WebBridges.Add(new(name, handler));
    public void AddWebBridge<TParams, TResult>(string name, Func<TParams, CancellationToken, Task<TResult>> handler) => AddWebBridge(name, async (payload, ct) =>
    {
        TParams? parameters = default;
        if (payload is { } element && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) parameters = element.Deserialize<TParams>(JsonOptions.Default);
        var result = await handler(parameters!, ct);
        return result is null ? null : JsonSerializer.SerializeToElement(result, JsonOptions.Default);
    });
}

/// <summary>Model-scoped plain-text footer contribution. Fetch runs off the render thread; never return secrets.</summary>
public sealed record ModelFooterContext(string ProviderId, string ModelId, string? AuthenticationProviderId);
public sealed record ModelFooterItem(string Label, string Value);
public sealed record ModelFooterProvider(string Id,
    Func<ModelFooterContext, bool> Matches,
    Func<ModelFooterContext, CancellationToken, Task<IReadOnlyList<ModelFooterItem>>> FetchAsync);
