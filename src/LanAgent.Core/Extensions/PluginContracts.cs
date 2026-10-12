using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;

namespace LanAgent.Core.Extensions;

/// <summary>Host SDK v1. Plugins run as trusted .NET code, not inside the Python microVM.</summary>
public interface ILanAgentPlugin : IAsyncDisposable
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
    void ValidateModelConfiguration(LanAgent.Core.Models.ApiConfig config) { }
    Task<AuthenticationCredential?> GetCredentialAsync(CancellationToken ct) => AuthenticationCredential.FromTokenAsync(this, ct);
    Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct);
    Task<string?> GetAccessTokenAsync(CancellationToken ct);
    Task LogoutAsync(CancellationToken ct);
}

public sealed record AuthenticationCredential(string AccessToken, string? ContextId = null)
{
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
    LanAgent.Core.Models.ApiConfig CreateConfiguration();
    IAiProvider ModelProvider { get; }
}

public sealed class PluginContext(string dataDirectory, IReadOnlyDictionary<Type, object>? services = null)
{
    public string DataDirectory { get; } = dataDirectory;
    public T GetService<T>() where T : class => services?.TryGetValue(typeof(T), out var service) == true ? (T)service : throw new InvalidOperationException("此宿主未提供服务：" + typeof(T).Name);
    internal List<IAgentToolSet> Tools { get; } = [];
    internal List<IAiProvider> Providers { get; } = [];
    internal List<PluginCommand> Commands { get; } = [];
    internal List<AgentHook> Hooks { get; } = [];
    internal List<IAuthenticationProvider> Authentication { get; } = [];
    public void AddTools(IAgentToolSet tools) => Tools.Add(tools);
    public void AddProvider(IAiProvider provider) => Providers.Add(provider);
    public void AddCommand(PluginCommand command) => Commands.Add(command);
    public void AddHook(AgentHook hook) => Hooks.Add(hook);
    public void AddAuthentication(IAuthenticationProvider authentication) => Authentication.Add(authentication);
}
