using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LanAgent.Core.Services;

public class McpToolInfo
{
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string InputSchemaJson { get; set; } = "{}";
}

public class McpToolService : IAsyncDisposable
{
    private readonly DataStore _store;
    private readonly SkillExecutor _skillExecutor;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Dictionary<string, McpClientConnection> _connections = new();
    private readonly Dictionary<string, List<McpToolInfo>> _serverTools = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly Dictionary<string, string> _errors = new();
    public Func<string, LanAgent.Core.Extensions.IAuthenticationProvider>? AuthenticationResolver { get; set; }
    public string? GetError(string id) { lock (_lock) return _errors.GetValueOrDefault(id); }
    public bool IsConnected(string id) { lock (_lock) return _connections.ContainsKey(id); }
    private McpClientConnection Connection(string id) { lock (_lock) return _connections.GetValueOrDefault(id) ?? throw new ArgumentException("MCP 服务尚未连接。"); }
    public Task<IReadOnlyList<object>> ListPromptsAsync(string id, CancellationToken ct = default) => Connection(id).ListPromptsAsync(ct);
    public Task<IReadOnlyList<object>> ListResourcesAsync(string id, CancellationToken ct = default) => Connection(id).ListResourcesAsync(ct);
    public Task<ToolResult> ReadResourceAsync(string id, string uri, CancellationToken ct = default) => Connection(id).ReadResourceAsync(uri, ct);
    public Task<ToolResult> GetPromptAsync(string id, string name, Dictionary<string, object?> arguments, CancellationToken ct = default) => Connection(id).GetPromptAsync(name, arguments, ct);

    public McpToolService(DataStore store, SkillExecutor skillExecutor, ILoggerFactory? loggerFactory = null)
    {
        _store = store;
        _skillExecutor = skillExecutor;
        _loggerFactory = loggerFactory;
    }

    public List<SkillDefinition> GetAllTools()
    {
        var tools = new List<SkillDefinition>();

        foreach (var skill in _store.Skills.Where(s => s.IsEnabled))
        {
            object? parameters;
            try { parameters = JsonSerializer.Deserialize<object>(skill.ParametersJson); }
            catch { parameters = new { }; }
            tools.Add(new SkillDefinition(skill.Name, skill.Description, parameters ?? new { }));
        }

        lock (_lock)
        {
            foreach (var kvp in _serverTools)
            {
                foreach (var tool in kvp.Value)
                {
                    object? parameters;
                    try { parameters = JsonSerializer.Deserialize<object>(tool.InputSchemaJson); }
                    catch { parameters = new { }; }
                    tools.Add(new SkillDefinition(tool.Name, tool.Description, parameters ?? new { }));
                }
            }
        }

        return tools;
    }

    public async IAsyncEnumerable<string> ExecuteAsync(
        string toolName, JsonElement arguments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var skill = _store.Skills.FirstOrDefault(s => s.Name == toolName && s.IsEnabled);
        if (skill != null)
        {
            await foreach (var line in _skillExecutor.ExecuteAsync(skill, arguments, ct))
                yield return line;
            yield break;
        }

        McpClientConnection? conn = null;
        lock (_lock)
        {
            foreach (var kvp in _serverTools)
            {
                if (kvp.Value.Any(t => t.Name == toolName) && _connections.TryGetValue(kvp.Key, out var c))
                {
                    conn = c;
                    break;
                }
            }
        }

        if (conn != null)
        {
            var args = new Dictionary<string, object?>();
            if (arguments.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in arguments.EnumerateObject())
                {
                    args[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => (object?)true,
                        JsonValueKind.False => (object?)false,
                        _ => prop.Value.GetRawText()
                    };
                }
            }

            string? result;
            try
            {
                result = await conn.CallToolAsync(toolName, args, ct);
            }
            catch (Exception ex)
            {
                result = $"[Error] MCP call failed: {ex.Message}";
            }
            if (result != null)
                yield return result;
            yield break;
        }

        yield return $"[Error] Tool '{toolName}' not found";
    }

    public async Task<bool> ConnectServerAsync(McpServerConfig config, CancellationToken ct = default)
    {
        await _connectionGate.WaitAsync(ct);
        McpClientConnection? connection = null;
        try
        {
            await DisconnectServerAsync(config.Id);
            connection = new McpClientConnection(config, _loggerFactory, config.AuthenticationProviderId is { Length: > 0 } authId ? AuthenticationResolver?.Invoke(authId) ?? throw new InvalidOperationException("该宿主未接入认证插件。") : null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 1, 120)));
            await connection.ConnectAsync(timeout.Token);

            lock (_lock)
            {
                _connections[config.Id] = connection;
                _serverTools[config.Id] = connection.DiscoveredTools;
                _errors.Remove(config.Id);
            }

            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (connection != null) await connection.DisposeAsync();
            lock (_lock) _errors[config.Id] = ex is OperationCanceledException ? "连接或工具发现超时。" : ToolResult.Clip(ex.Message, 1000);
            return false;
        }
        catch { if (connection != null) await connection.DisposeAsync(); throw; }
        finally { _connectionGate.Release(); }
    }

    public async Task<ToolResult> ExecuteServerToolAsync(string serverId, string toolName, JsonElement arguments, CancellationToken ct)
    {
        McpClientConnection? connection;
        lock (_lock)
        {
            if (!_serverTools.TryGetValue(serverId, out var tools) || !tools.Any(t => t.Name == toolName) || !_connections.TryGetValue(serverId, out connection))
                return ToolResult.Failure("MCP server is disconnected or tool is unknown.");
        }
        var args = arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        var output = await connection.CallToolAsync(toolName, args, ct);
        return output == null ? ToolResult.Failure("MCP server returned no result.") : output.StartsWith("[Error]", StringComparison.Ordinal)
            ? ToolResult.Failure(output) : ToolResult.Success(new { content = ToolResult.Clip(output) }, output.Length > ToolResult.MaxOutputCharacters);
    }

    public async Task DisconnectServerAsync(string serverId)
    {
        McpClientConnection? conn = null;
        lock (_lock)
        {
            _serverTools.Remove(serverId);
            _connections.Remove(serverId, out conn);
        }
        if (conn != null)
            await conn.DisposeAsync();
    }

    public async Task ConnectAllEnabledAsync(CancellationToken ct = default)
    {
        foreach (var config in _store.McpServerConfigs.Where(c => c.IsEnabled))
        {
            if (IsConnected(config.Id)) continue;
            await ConnectServerAsync(config, ct);
        }
    }

    public List<(string ServerId, string Name, List<McpToolInfo> Tools)> GetAllServerTools()
    {
        lock (_lock)
        {
            return _serverTools
                .Select(kvp =>
                {
                    var name = _connections.TryGetValue(kvp.Key, out var c) ? c.Config.Name : "?";
                    return (kvp.Key, name, kvp.Value.ToList());
                })
                .ToList();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var conn in _connections.Values)
            await conn.DisposeAsync();
        _connections.Clear();
    }
}

internal class McpClientConnection : IAsyncDisposable
{
    public McpServerConfig Config { get; }
    public List<McpToolInfo> DiscoveredTools { get; private set; } = new();
    private McpClient? _client;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly LanAgent.Core.Extensions.IAuthenticationProvider? _authentication;

    public McpClientConnection(McpServerConfig config, ILoggerFactory? loggerFactory = null, LanAgent.Core.Extensions.IAuthenticationProvider? authentication = null)
    {
        Config = config;
        _loggerFactory = loggerFactory;
        _authentication = authentication;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
            var args = Config.ArgumentList ?? CommandLineWords.Parse(Config.Arguments);

            IClientTransport transport;
            if (Config.Transport is "http" or "sse" || Config.Transport == "auto" && (Config.Command.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                Config.Command.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                var transportOptions = new HttpClientTransportOptions { Endpoint = new Uri(Config.Command), AdditionalHeaders = Config.Headers.ToDictionary(p => p.Key, p => ResolveEnvironment(p.Value)), TransportMode = Config.Transport == "sse" ? HttpTransportMode.Sse : Config.Transport == "http" ? HttpTransportMode.StreamableHttp : HttpTransportMode.AutoDetect };
                transport = _authentication == null ? new HttpClientTransport(transportOptions) : new HttpClientTransport(transportOptions, new HttpClient(new LanAgent.Core.Extensions.AuthenticationHttpHandler(_authentication, transportOptions.Endpoint)), _loggerFactory, ownsHttpClient: true);
            }
            else
            {
                transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = Config.Name,
                    Command = Config.Command,
                    Arguments = args,
                    EnvironmentVariables = Config.EnvironmentVariables.ToDictionary(p => p.Key, p => (string?)ResolveEnvironment(p.Value)),
                });
            }

            _client = await McpClient.CreateAsync(transport, new McpClientOptions { InitializationTimeout = TimeSpan.FromSeconds(Math.Clamp(Config.TimeoutSeconds, 1, 120)) }, cancellationToken: ct);

            DiscoveredTools = await DiscoverToolsAsync(ct);
    }

    private async Task<List<McpToolInfo>> DiscoverToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpToolInfo>();
        if (_client == null) return tools;

            var toolList = await _client.ListToolsAsync(cancellationToken: ct);
            foreach (var tool in toolList)
            {
                var schema = tool?.ProtocolTool?.InputSchema.GetRawText() ?? "{}";
                tools.Add(new McpToolInfo
                {
                    ServerId = Config.Id,
                    Name = tool?.Name ?? "",
                    Description = tool?.Description ?? "",
                    InputSchemaJson = schema
                });
            }
        return tools;
    }

    public async Task<string?> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken ct = default)
    {
        if (_client == null) return null;

        try
        {
            var result = await _client.CallToolAsync(toolName, args, cancellationToken: ct);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            return result.IsError == true ? "[Error] " + text : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"[Error] {ex.Message}";
        }
    }

    private static string ResolveEnvironment(string value)
    {
        if (value == "<REDACTED>") throw new ArgumentException("请重新设置脱敏导出中的凭据。");
        return System.Text.RegularExpressions.Regex.Replace(value, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? throw new ArgumentException("未设置 MCP 环境变量：" + match.Groups[1].Value));
    }

    public async Task<IReadOnlyList<object>> ListPromptsAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(Config.TimeoutSeconds));
        return (await _client!.ListPromptsAsync(cancellationToken: timeout.Token)).Select(p => (object)new { p.Name, p.Description, arguments = p.ProtocolPrompt.Arguments }).ToArray();
    }
    public async Task<IReadOnlyList<object>> ListResourcesAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(Config.TimeoutSeconds));
        return (await _client!.ListResourcesAsync(cancellationToken: timeout.Token)).Select(r => (object)new { r.Name, r.Uri, r.Description, r.MimeType }).ToArray();
    }
    public async Task<ToolResult> GetPromptAsync(string name, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(Config.TimeoutSeconds));
        var result = await _client!.GetPromptAsync(name, arguments, cancellationToken: timeout.Token);
        var messages = result.Messages.Select(m => new { role = m.Role.ToString(), text = m.Content is TextContentBlock text ? ToolResult.Clip(text.Text) : "[non-text content]" }).ToArray();
        return ToolResult.Success(new { result.Description, messages });
    }
    public async Task<ToolResult> ReadResourceAsync(string uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(Config.TimeoutSeconds));
        var result = await _client!.ReadResourceAsync(uri, cancellationToken: timeout.Token);
        var texts = result.Contents.OfType<TextResourceContents>().ToArray();
        if (texts.Length == 0) return ToolResult.Failure("资源未返回文本内容。");
        var joined = string.Join("\n", texts.Select(t => t.Text));
        return ToolResult.Success(new { uri, content = ToolResult.Clip(joined) }, joined.Length > ToolResult.MaxOutputCharacters);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }
}
