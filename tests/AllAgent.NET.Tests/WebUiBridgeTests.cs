using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class WebUiBridgeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allagent-webui-" + Guid.NewGuid().ToString("N"));

    private string WebRoot => Path.Combine(_root, "web");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(WebRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(WebRoot, "index.html"), "<!doctype html><title>AllAgent workspace</title>");
        await File.WriteAllTextAsync(Path.Combine(WebRoot, "assets", "app.js"), "console.log('workspace')");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort cleanup */ }
        return Task.CompletedTask;
    }

    private sealed record EchoParams(string Value);

    [Fact]
    public async Task PluginTaskStartsInTheSelectedWorkspaceAndCanBeOpenedThroughTheNormalBridge()
    {
        using var environment = new TestWorkspace();
        var data = Path.Combine(_root, "data"); var shared = Path.Combine(_root, "shared");
        var target = Path.Combine(_root, "selected-workspace"); Directory.CreateDirectory(target);
        using var store = new DataStore(data, false, shared);
        store.AddApiConfig(new ApiConfig { ProviderId = "host-account", DefaultModel = "account-model" });
        await using var mcp = new McpToolService(store, new SkillExecutor());
        await using var tasks = new BackgroundTasks(data);
        await using var plugins = new PluginHost(shared);
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(_root), data);
        var catalog = new SkillCatalog(store, _root, shared);
        var host = new WebUiTerminalHost(store, mcp, tasks, checkpoints, catalog, _root, data);
        var provider = new HostAccountProvider();
        await using var session = WebUiSession.Start(host,
            () => environment.Options with { WorkspaceRoot = _root, SharedDirectory = shared },
            new AiProviderFactory(), plugins, shared, 1, _ => provider);
        const string publicMessage = "请审查 GitHub PR #7。";
        const string internalPrompt = "INTERNAL-WORKFLOW：核对 head SHA，然后审查变更。";
        var task = await session.StartPluginTaskAsync(new(target, "GitHub review #7", internalPrompt, Message: publicMessage), default);
        Assert.Equal(target, task.Workspace); Assert.NotEmpty(task.StreamId);
        Assert.Empty(store.Conversations);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(session.Url.Replace("http://", "ws://") + "bridge"), default);
        async Task<JsonDocument> Request(string id, string method, object? parameters = null)
        {
            var request = JsonSerializer.Serialize(new { id, method, @params = parameters });
            await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, default);
            // A live workspace also pushes stream/run/workspace events before the RPC response.
            while (true)
            {
                var response = JsonDocument.Parse(await ReceiveAsync(socket));
                if (response.RootElement.TryGetProperty("id", out var replyId) && replyId.GetString() == id) return response;
                response.Dispose();
            }
        }
        using (var current = await Request("current", "workspace.get"))
            Assert.Equal(_root, current.RootElement.GetProperty("data").GetProperty("workspace").GetString());
        using var switched = await Request("switch", "workspace.switch", new { path = target });
        Assert.Equal(JsonValueKind.Null, switched.RootElement.GetProperty("error").ValueKind);
        using var conversations = await Request("conversations", "conversations.list");
        var conversation = Assert.Single(conversations.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(task.ConversationId.ToString(), conversation.GetProperty("id").GetString());
        Assert.Equal("GitHub review #7", conversation.GetProperty("title").GetString());
        using var stream = await Request("stream", "chat.getStreamState", new { conversationId = task.ConversationId });
        if (stream.RootElement.GetProperty("data").ValueKind != JsonValueKind.Null)
            Assert.Equal(publicMessage, stream.RootElement.GetProperty("data").GetProperty("input").GetString());
        using var messages = await Request("messages", "conversations.getMessages", new { conversationId = task.ConversationId });
        foreach (var message in messages.RootElement.GetProperty("data").EnumerateArray().Where(message => message.GetProperty("role").GetString() == "user"))
            Assert.Equal(publicMessage, message.GetProperty("content").GetString());
        Assert.DoesNotContain("INTERNAL-WORKFLOW", messages.RootElement.GetRawText());
    }

    [Fact]
    public async Task WebUiOwnsExplicitMcpConnectionsAndStoppingItPreservesTerminalServices()
    {
        var data = Path.Combine(_root, "data");
        var shared = Path.Combine(_root, "shared");
        using var store = new DataStore(data);
        await using var terminalMcp = new McpToolService(store, new SkillExecutor());
        await using var tasks = new BackgroundTasks(data);
        await using var plugins = new PluginHost(shared);
        var config = new McpServerConfig { Name = "fixture", Command = Path.Combine(AppContext.BaseDirectory, "probe", "AllAgent.NET.ProcessProbe.exe"), Arguments = "mcp", IsEnabled = true };
        store.AddMcpServerConfig(config);
        Assert.True(await terminalMcp.ConnectServerAsync(config));
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(_root), data);
        var catalog = new SkillCatalog(store, _root, shared);
        var host = new WebUiTerminalHost(store, terminalMcp, tasks, checkpoints, catalog, _root, data);
        var session = WebUiSession.Start(host, () => new AgentOptions { WorkspaceRoot = _root, SharedDirectory = shared }, new AiProviderFactory(), plugins, shared, 1);
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(session.Url.Replace("http://", "ws://") + "bridge"), CancellationToken.None);
            using var listing = JsonDocument.Parse(await SendAsync(socket, "{\"id\":\"list\",\"method\":\"mcpServers.list\"}"));
            Assert.False(listing.RootElement.GetProperty("data")[0].GetProperty("isConnected").GetBoolean());
            using var connected = JsonDocument.Parse(await SendAsync(socket, JsonSerializer.Serialize(new { id = "connect", method = "mcpServers.connect", @params = new { id = config.Id } })));
            Assert.True(connected.RootElement.GetProperty("data").GetBoolean());
        }
        finally { await session.DisposeAsync(); }
        Assert.True(terminalMcp.IsConnected(config.Id));
        Assert.True((await terminalMcp.ExecuteServerToolAsync(config.Id, "echo", JsonSerializer.SerializeToElement(new { proof = "still alive" }), CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task FileReferencesResolveAgainstTheWorkspaceAndMissingFilesReportTheFullPath()
    {
        var data = Path.Combine(_root, "data");
        var shared = Path.Combine(_root, "shared");
        using var store = new DataStore(data);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        await using var tasks = new BackgroundTasks(data);
        await using var plugins = new PluginHost(shared);
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(_root), data);
        var catalog = new SkillCatalog(store, _root, shared);
        var host = new WebUiTerminalHost(store, mcp, tasks, checkpoints, catalog, _root, data);
        var session = WebUiSession.Start(host, () => new AgentOptions { WorkspaceRoot = _root, SharedDirectory = shared }, new AiProviderFactory(), plugins, shared, 1);
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(session.Url.Replace("http://", "ws://") + "bridge"), CancellationToken.None);
            // A reference the browser cannot serve is resolved against the workspace; a path that
            // is not there is reported back instead of anything being opened.
            using var missing = JsonDocument.Parse(await SendAsync(socket, JsonSerializer.Serialize(new { id = "open", method = "shell.openPath", @params = new { path = "pelican-bike.html" } })));
            Assert.Contains(Path.Combine(_root, "pelican-bike.html"), missing.RootElement.GetProperty("error").GetString());
        }
        finally { await session.DisposeAsync(); }
    }

    [Fact]
    public async Task BrowserReadsFooterContributionsAndTheConversationCacheRate()
    {
        var data = Path.Combine(_root, "data");
        var shared = Path.Combine(_root, "shared");
        using var store = new DataStore(data);
        await using var terminalMcp = new McpToolService(store, new SkillExecutor());
        await using var tasks = new BackgroundTasks(data);
        await using var plugins = new PluginHost(shared);
        var config = new ApiConfig { ProviderId = "codex", DisplayName = "Codex", DefaultModel = "gpt-5.1", AuthenticationProviderId = "codex" };
        store.AddApiConfig(config);
        var conversation = new Conversation { ModelId = "gpt-5.1" };
        conversation.CacheInputTokens = 1000;
        conversation.CachedInputTokens = 250;
        conversation.CacheWriteInputTokens = 120;
        conversation.CacheUsageRequests = 2;
        conversation.ContextUsedTokens = 1234;
        conversation.ContextUsageModel = "gpt-5.1";
        conversation.ContextUsageEstimated = false;
        store.AddConversation(conversation);
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(_root), data);
        var catalog = new SkillCatalog(store, _root, shared);
        var host = new WebUiTerminalHost(store, terminalMcp, tasks, checkpoints, catalog, _root, data);
        var session = WebUiSession.Start(host, () => new AgentOptions { WorkspaceRoot = _root, SharedDirectory = shared }, new AiProviderFactory(), plugins, shared, 1);
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(session.Url.Replace("http://", "ws://") + "bridge"), CancellationToken.None);
            // Cache hit rate and the stored context count come from the conversation record.
            using (var usage = JsonDocument.Parse(await SendAsync(socket, JsonSerializer.Serialize(new
            {
                id = "usage",
                method = "conversations.usage",
                @params = new { conversationId = conversation.Id },
            }))))
            {
                var payload = usage.RootElement.GetProperty("data");
                Assert.Equal(25d, payload.GetProperty("cacheHitRate").GetDouble(), 3);
                Assert.Equal(2, payload.GetProperty("cacheRequests").GetInt64());
                Assert.Equal(1234, payload.GetProperty("contextUsedTokens").GetInt32());
                Assert.False(payload.GetProperty("contextEstimated").GetBoolean());
            }
            // Providers are fetched off the bridge call; the page polls while a refresh is in flight.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string? label = null;
            while (label == null && !deadline.IsCancellationRequested)
            {
                using var footer = JsonDocument.Parse(await SendAsync(socket, JsonSerializer.Serialize(new
                {
                    id = "footer",
                    method = "session.footer",
                    @params = new { apiConfigId = config.Id, model = "gpt-5.1" },
                })));
                var items = footer.RootElement.GetProperty("data").GetProperty("items");
                if (items.GetArrayLength() > 0) label = items[0].GetProperty("label").GetString();
                else await Task.Delay(20);
            }
            Assert.Equal("Codex 额度", label);
        }
        finally { await session.DisposeAsync(); }
    }

    [Fact]
    public async Task McpConnectionsAreExplicitAndDisablingDisconnectingOrDisposingReleasesTools()
    {
        using var store = new DataStore(Path.Combine(_root, "data"));
        var config = new McpServerConfig { Name = "fixture", Command = Path.Combine(AppContext.BaseDirectory, "probe", "AllAgent.NET.ProcessProbe.exe"), Arguments = "mcp", IsEnabled = true };
        store.AddMcpServerConfig(config);
        var factory = new AiProviderFactory();
        await using var agents = new AgentService(store, factory, new AgentOptions());
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var dispatcher = new BridgeDispatcher();
        new BridgeHandlerRegistrar(dispatcher, store, agents, factory, new BridgeEventPusher(), mcp).RegisterAll();
        async Task<JsonElement> Invoke(string method, object? parameters = null)
        {
            using var json = JsonDocument.Parse(await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "mcp", method, @params = parameters ?? new { id = config.Id } })));
            return json.RootElement.Clone();
        }
        Assert.False((await Invoke("mcpServers.list")).GetProperty("data")[0].GetProperty("isConnected").GetBoolean());
        Assert.True((await Invoke("mcpServers.connect")).GetProperty("data").GetBoolean(), mcp.GetError(config.Id));
        Assert.NotEmpty(mcp.GetAllServerTools());
        await Invoke("mcpServers.save", new { id = config.Id, name = config.Name, command = config.Command, arguments = config.Arguments, isEnabled = false });
        Assert.False(mcp.IsConnected(config.Id));
        Assert.Empty(mcp.GetAllServerTools());
        Assert.Contains("先启用", (await Invoke("mcpServers.connect")).GetProperty("error").GetString());
        await Invoke("mcpServers.save", new { id = config.Id, name = config.Name, command = config.Command, arguments = config.Arguments, isEnabled = true });
        Assert.False(mcp.IsConnected(config.Id));
        Assert.True((await Invoke("mcpServers.connect")).GetProperty("data").GetBoolean());
        await Invoke("mcpServers.disconnect");
        Assert.False(mcp.IsConnected(config.Id));
        Assert.True((await Invoke("mcpServers.connect")).GetProperty("data").GetBoolean());
        await mcp.DisposeAsync();
        Assert.Empty(mcp.GetAllServerTools());
        Assert.False(mcp.IsConnected(config.Id));
    }

    [Fact]
    public async Task BrowserHistoryShowsHumanDraftsAndInterruptionStateWithoutHostInstructions()
    {
        using var store = new DataStore(Path.Combine(_root, "data"));
        var conversation = new Conversation(); store.AddConversation(conversation);
        store.AddMessage(conversation.Id, new Message { Role = "user", Content = "expanded file body", InputText = "read @file" });
        store.AddMessage(conversation.Id, new Message { Role = "user", Content = "internal continuation", IsInternal = true });
        store.AddMessage(conversation.Id, new Message { Role = "assistant", Content = "partial answer", IsInterrupted = true });
        var factory = new AiProviderFactory();
        await using var service = new AgentService(store, factory, new AgentOptions());
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var dispatcher = new BridgeDispatcher();
        new BridgeHandlerRegistrar(dispatcher, store, service, factory, new BridgeEventPusher(), mcp).RegisterAll();
        using var response = JsonDocument.Parse(await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "history", method = "conversations.getMessages", @params = new { conversationId = conversation.Id } })));
        var messages = response.RootElement.GetProperty("data");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("read @file", messages[0].GetProperty("content").GetString());
        Assert.True(messages[1].GetProperty("isInterrupted").GetBoolean());
    }

    [Fact]
    public async Task BrowserManagesPowerShellComponentsInTheSharedModuleStore()
    {
        var data = Path.Combine(_root, "data");
        var shared = Path.Combine(_root, "shared");
        using var store = new DataStore(data);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        await using var tasks = new BackgroundTasks(data);
        await using var plugins = new PluginHost(shared);
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(_root), data);
        var catalog = new SkillCatalog(store, _root, shared);
        var host = new WebUiTerminalHost(store, mcp, tasks, checkpoints, catalog, _root, data);
        var session = WebUiSession.Start(host, () => new AgentOptions { WorkspaceRoot = _root, SharedDirectory = shared }, new AiProviderFactory(), plugins, shared, 1);
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(session.Url.Replace("http://", "ws://") + "bridge"), CancellationToken.None);

            // A fresh shared directory has no modules, but the packaged SDK worker is reported: the
            // page can tell "nothing installed yet" apart from "component missing".
            using (var status = JsonDocument.Parse(await SendAsync(socket, "{\"id\":\"ps\",\"method\":\"powershell.status\"}")))
            {
                var payload = status.RootElement.GetProperty("data");
                Assert.True(payload.GetProperty("workerAvailable").GetBoolean());
                Assert.EndsWith(Path.Combine("powershell", "Modules"), payload.GetProperty("root").GetString());
                Assert.Equal(0, payload.GetProperty("modules").GetArrayLength());
            }

            // Installing is gated like the terminal's /powershell: unsafe names and loose versions
            // are rejected before any download starts.
            using (var name = JsonDocument.Parse(await SendAsync(socket, JsonSerializer.Serialize(new { id = "ps-name", method = "powershell.install", @params = new { name = "../escape" } }))))
                Assert.Contains("模块名称无效", name.RootElement.GetProperty("error").GetString());
            using (var version = JsonDocument.Parse(await SendAsync(socket, JsonSerializer.Serialize(new { id = "ps-version", method = "powershell.install", @params = new { name = "Pester", version = "5.7.x" } }))))
                Assert.Contains("版本格式无效", version.RootElement.GetProperty("error").GetString());
            using (var search = JsonDocument.Parse(await SendAsync(socket, JsonSerializer.Serialize(new { id = "ps-search", method = "powershell.search", @params = new { query = "" } }))))
                Assert.Contains("模块关键词", search.RootElement.GetProperty("error").GetString());

            // Commands come from the verified catalog; an uninstalled module is an error, not an empty page.
            using var commands = JsonDocument.Parse(await SendAsync(socket, JsonSerializer.Serialize(new { id = "ps-commands", method = "powershell.commands", @params = new { name = "Pester" } })));
            Assert.Contains("能力清单", commands.RootElement.GetProperty("error").GetString());
        }
        finally { await session.DisposeAsync(); }
    }

    private sealed class HostAccountProvider : IAiProvider
    {
        public string ProviderId => "host-account";
        public int Calls { get; private set; }
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            await Task.Yield();
            yield return new(ChunkType.TextDelta, "authenticated");
            yield return new(ChunkType.Done, FinishReason: "stop");
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([new("account-model", "Account model")]);
    }

    [Fact]
    public async Task BrowserModelRequestsUseTheHostsAuthenticatedProvider()
    {
        using var store = new DataStore(Path.Combine(_root, "data"));
        var config = new ApiConfig { ProviderId = "host-account", DefaultModel = "account-model", AuthenticationProviderId = "signed-in-account" };
        store.AddApiConfig(config);
        var factory = new AiProviderFactory();
        var provider = new HostAccountProvider();
        await using var service = new AgentService(store, factory, new AgentOptions()) { ProviderResolver = _ => provider };
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var dispatcher = new BridgeDispatcher();
        new BridgeHandlerRegistrar(dispatcher, store, service, factory, new BridgeEventPusher(), mcp).RegisterAll();
        Assert.Same(provider, service.ResolveProvider(config));
        using var response = JsonDocument.Parse(await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "test", method = "apiConfigs.test", @params = new { id = config.Id } })));
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("error").ValueKind);
        Assert.True(response.RootElement.GetProperty("data").GetBoolean());
        Assert.Equal(1, provider.Calls);
        using var models = JsonDocument.Parse(await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "models", method = "apiConfigs.refreshModels", @params = new { id = config.Id } })));
        Assert.Equal("account-model", models.RootElement.GetProperty("data").GetProperty("models")[0].GetString());
    }

    [Fact]
    public async Task TerminalEventsAreDeliveredAfterTheConversationRunIsReleased()
    {
        using var store = new DataStore(Path.Combine(_root, "data"));
        var conversation = new Conversation();
        store.AddConversation(conversation);
        await using var service = new AgentService(store, new AiProviderFactory(), new AgentOptions
        {
            WorkspaceRoot = _root, SharedDirectory = Path.Combine(_root, "shared"),
            PythonPath = Path.Combine(_root, "missing-python.exe"),
            PowerShellPath = Path.Combine(_root, "missing-worker.dll"),
            AllowSandboxInstallation = false,
        });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? runningAtError = null, runningAtDone = null;
        var stream = service.StartStream(conversation.Id, "test", new ApiConfig(), (_, evt) =>
        {
            if (evt is AgentError) runningAtError = service.IsRunning(conversation.Id);
            if (evt is AgentDone) { runningAtDone = service.IsRunning(conversation.Id); done.TrySetResult(); }
            return Task.CompletedTask;
        });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await service.CancelStreamAsync(stream);
        Assert.Equal(false, runningAtError);
        Assert.Equal(false, runningAtDone);
    }

    [Fact]
    public void PairingQrRendersForTheTerminalAndSettingsPage()
    {
        var text = WebUiQr.Text("http://192.168.1.5:8765/?access=token");
        Assert.Contains("█", text);
        Assert.True(text.Split('\n').Length > 10);
        Assert.StartsWith("data:image/png;base64,", WebUiQr.PngDataUrl("http://192.168.1.5:8765/?access=token"));
    }

    [Fact]
    public async Task LanListenerRequiresTheQrTokenAndKeepsPasswordOptional()
    {
        var access = new WebUiAccessControl(new WebUiNetworkSettings());
        await using var server = WebUiServer.Start(new BridgeDispatcher(), new BridgeEventPusher(), WebRoot,
            new WebUiServerOptions(Lan: true, Port: 0, Access: access, LocalAccessFree: false));
        using var http = new HttpClient();

        // Static assets load so the gate can explain how to enter; the bridge stays closed.
        Assert.Contains("AllAgent workspace", await http.GetStringAsync(server.LocalUrl));
        using (var status = JsonDocument.Parse(await http.GetStringAsync(server.LocalUrl + "__auth/status")))
        {
            Assert.True(status.RootElement.GetProperty("required").GetBoolean());
            Assert.False(status.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.False(status.RootElement.GetProperty("passwordConfigured").GetBoolean());
        }

        // No password is created by default: login redirects to the QR pairing route.
        using (var login = await http.PostAsync(server.LocalUrl + "__auth/login",
            new StringContent("{\"password\":\"anything\"}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.Conflict, login.StatusCode);

        using (var rejected = new ClientWebSocket())
            await Assert.ThrowsAsync<WebSocketException>(() => rejected.ConnectAsync(new Uri(server.LocalUrl.Replace("http://", "ws://") + "bridge"), CancellationToken.None));
        Assert.Contains("access=", server.AccessUrl(server.Url));

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(server.LocalUrl.Replace("http://", "ws://") + "bridge?access=" + access.AccessToken), CancellationToken.None);
        for (var attempt = 0; attempt < 100 && server.ConnectedClients == 0; attempt++) await Task.Delay(10);
        Assert.Equal(1, server.ConnectedClients);
    }

    [Fact]
    public async Task PasswordLoginIssuesTheTokenForRemoteDevices()
    {
        var access = new WebUiAccessControl(new WebUiNetworkSettings());
        access.SetPassword("s3cret");
        await using var server = WebUiServer.Start(new BridgeDispatcher(), new BridgeEventPusher(), WebRoot,
            new WebUiServerOptions(Lan: true, Port: 0, Access: access, LocalAccessFree: false));
        using var http = new HttpClient();
        using (var status = JsonDocument.Parse(await http.GetStringAsync(server.LocalUrl + "__auth/status")))
        {
            Assert.True(status.RootElement.GetProperty("required").GetBoolean());
            Assert.True(status.RootElement.GetProperty("passwordConfigured").GetBoolean());
        }
        using (var wrong = await http.PostAsync(server.LocalUrl + "__auth/login",
            new StringContent("{\"password\":\"wrong\"}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var response = await http.PostAsync(server.LocalUrl + "__auth/login",
            new StringContent("{\"password\":\"s3cret\"}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(access.AccessToken, body.RootElement.GetProperty("token").GetString());

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(server.LocalUrl.Replace("http://", "ws://") + "bridge?access=" + access.AccessToken), CancellationToken.None);
    }

    [Fact]
    public async Task LanHostHeaderForThisMachineIsAccepted()
    {
        var address = WebUiAddress.LanAddresses().FirstOrDefault();
        if (address is null) return; // a CI host without a LAN interface still passes the rest of the suite
        var access = new WebUiAccessControl(new WebUiNetworkSettings());
        await using var server = WebUiServer.Start(new BridgeDispatcher(), new BridgeEventPusher(), WebRoot,
            new WebUiServerOptions(Lan: true, Port: 0, Access: access, LocalAccessFree: false));
        Assert.True(WebUiAddress.IsLocalHost($"{address}:{server.Port}", lan: true));
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.LocalUrl);
        request.Headers.Host = $"{address}:{server.Port}";
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LanWebSocketHandshakeAcceptsTheMachinesOwnAddress()
    {
        var address = WebUiAddress.LanAddresses().FirstOrDefault();
        if (address is null) return;
        var access = new WebUiAccessControl(new WebUiNetworkSettings());
        await using var server = WebUiServer.Start(new BridgeDispatcher(), new BridgeEventPusher(), WebRoot,
            new WebUiServerOptions(Lan: true, Port: 0, Access: access));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        using var stream = tcp.GetStream();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = $"GET /bridge?access={access.AccessToken} HTTP/1.1\r\n"
            + $"Host: {address}:{server.Port}\r\n"
            + $"Origin: http://{address}:{server.Port}\r\n"
            + "Upgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buffer = new byte[512];
        var read = await stream.ReadAsync(buffer);
        var response = Encoding.ASCII.GetString(buffer, 0, read);
        Assert.StartsWith("HTTP/1.1 101", response);
    }

    [Fact]
    public async Task ThisMachineSkipsTheLanGate()
    {
        var access = new WebUiAccessControl(new WebUiNetworkSettings());
        access.SetPassword("s3cret");
        await using var server = WebUiServer.Start(new BridgeDispatcher(), new BridgeEventPusher(), WebRoot,
            new WebUiServerOptions(Lan: true, Port: 0, Access: access));
        using var http = new HttpClient();
        using (var status = JsonDocument.Parse(await http.GetStringAsync(server.LocalUrl + "__auth/status")))
        {
            Assert.False(status.RootElement.GetProperty("required").GetBoolean());
            Assert.True(status.RootElement.GetProperty("authenticated").GetBoolean());
        }
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(server.LocalUrl.Replace("http://", "ws://") + "bridge"), CancellationToken.None);
        for (var attempt = 0; attempt < 100 && server.ConnectedClients == 0; attempt++) await Task.Delay(10);
        Assert.Equal(1, server.ConnectedClients);
    }

    [Fact]
    public async Task RejectsForeignHostHeadersToBlockDnsRebinding()
    {
        await using var server = WebUiServer.Start(new BridgeDispatcher(), new BridgeEventPusher(), WebRoot);
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.LocalUrl);
        request.Headers.Host = "evil.example";
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BrowserReadsAndUpdatesNetworkAccessSettings()
    {
        var data = Path.Combine(_root, "data");
        var shared = Path.Combine(_root, "shared");
        using var store = new DataStore(data);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        await using var tasks = new BackgroundTasks(data);
        await using var plugins = new PluginHost(shared);
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(_root), data);
        var catalog = new SkillCatalog(store, _root, shared);
        var host = new WebUiTerminalHost(store, mcp, tasks, checkpoints, catalog, _root, data);
        var session = WebUiSession.Start(host, () => new AgentOptions { WorkspaceRoot = _root, SharedDirectory = shared }, new AiProviderFactory(), plugins, shared, 1);
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(session.Url.Replace("http://", "ws://") + "bridge"), CancellationToken.None);
            async Task<JsonElement> Request(string id, string method, object? parameters = null)
            {
                using var response = JsonDocument.Parse(await SendAsync(socket, JsonSerializer.Serialize(new { id, method, @params = parameters })));
                return response.RootElement.TryGetProperty("data", out var payload) ? payload.Clone() : default;
            }

            var info = await Request("get", "webui.network.get");
            Assert.False(info.GetProperty("lan").GetBoolean());
            Assert.False(info.GetProperty("authenticationRequired").GetBoolean());
            Assert.False(info.GetProperty("passwordConfigured").GetBoolean());
            Assert.False(string.IsNullOrEmpty(info.GetProperty("qrDataUrl").GetString()));

            // A password saved while loopback-only changes nothing for this machine, but it is
            // ready for the next LAN start; clearing it is symmetric.
            var saved = await Request("save", "webui.network.save", new { lan = false, port = session.Port, password = "secret", clearPassword = false });
            Assert.True(saved.GetProperty("passwordConfigured").GetBoolean());
            Assert.False(saved.GetProperty("authenticationRequired").GetBoolean());
            var cleared = await Request("clear", "webui.network.save", new { lan = false, port = session.Port, password = (string?)null, clearPassword = true });
            Assert.False(cleared.GetProperty("passwordConfigured").GetBoolean());
        }
        finally { await session.DisposeAsync(); }
    }

    [Fact]
    public async Task EnablingLanRestartsTheListenerAndPersistsTheChoice()
    {
        var data = Path.Combine(_root, "data");
        var shared = Path.Combine(_root, "shared");
        using var store = new DataStore(data);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        await using var tasks = new BackgroundTasks(data);
        await using var plugins = new PluginHost(shared);
        var checkpoints = new WorkspaceCheckpoints(new WorkspacePolicy(_root), data);
        var catalog = new SkillCatalog(store, _root, shared);
        var host = new WebUiTerminalHost(store, mcp, tasks, checkpoints, catalog, _root, data);
        var session = WebUiSession.Start(host, () => new AgentOptions { WorkspaceRoot = _root, SharedDirectory = shared }, new AiProviderFactory(), plugins, shared, 1);
        try
        {
            var info = await session.UpdateNetworkAsync(new WebUiNetworkParams(true, 0, null, false));
            Assert.True(info.Lan);
            Assert.True(info.AuthenticationRequired);
            Assert.False(info.PasswordConfigured);
            Assert.False(string.IsNullOrEmpty(info.QrDataUrl));
            Assert.Contains("access=", info.AccessUrl);

            // The settings call schedules the listener restart; wait for the new port to take over.
            for (var attempt = 0; attempt < 100 && session.Port != info.Port; attempt++) await Task.Delay(50);
            Assert.Equal(info.Port, session.Port);
            Assert.True(session.Lan);

            // This machine still opens the workspace without the token.
            using var http = new HttpClient();
            using var status = JsonDocument.Parse(await http.GetStringAsync(session.LocalUrl + "__auth/status"));
            Assert.False(status.RootElement.GetProperty("required").GetBoolean());
            Assert.True(status.RootElement.GetProperty("authenticated").GetBoolean());
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(session.LocalUrl.Replace("http://", "ws://") + "bridge"), CancellationToken.None);
            Assert.Equal(1, session.ConnectedClients);

            var persisted = WebUiNetworkSettings.Load(shared);
            Assert.True(persisted.Lan);
            Assert.Equal(session.Port, persisted.Port);
        }
        finally { await session.DisposeAsync(); }
    }

    private static async Task<string> SendAsync(ClientWebSocket socket, string json)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        return await ReceiveAsync(socket);
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        using var payload = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            payload.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(payload.ToArray());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"id\":\"x\"}")]
    [InlineData("[]")]
    public async Task MalformedRequestsReturnProtocolErrors(string request)
    {
        using var result = JsonDocument.Parse(await new BridgeDispatcher().InvokeAsync(request));
        Assert.Equal("response", result.RootElement.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Utf8CharactersSurviveWebSocketFragmentBoundaries()
    {
        var dispatcher = new BridgeDispatcher();
        dispatcher.Register<EchoParams, string>("echo", (parameters, _) => Task.FromResult(parameters.Value));
        await using var server = WebUiServer.Start(dispatcher, new BridgeEventPusher(), WebRoot);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(server.Url.Replace("http://", "ws://") + "bridge"), CancellationToken.None);
        var json = "{\"id\":\"utf8\",\"method\":\"echo\",\"params\":{\"value\":\"中文😀\"}}";
        var bytes = Encoding.UTF8.GetBytes(json);
        var split = Array.IndexOf(bytes, (byte)0xe4) + 1;
        Assert.True(split > 1);
        await socket.SendAsync(new ArraySegment<byte>(bytes, 0, split), WebSocketMessageType.Text, false, CancellationToken.None);
        await socket.SendAsync(new ArraySegment<byte>(bytes, split, bytes.Length - split), WebSocketMessageType.Text, true, CancellationToken.None);
        using var response = JsonDocument.Parse(await ReceiveAsync(socket));
        Assert.Equal("中文😀", response.RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public async Task RejectsCrossOriginAccessToHostTools()
    {
        await using var server = WebUiServer.Start(new BridgeDispatcher(), new BridgeEventPusher(), WebRoot);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://example.com");
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new Uri(server.Url.Replace("http://", "ws://") + "bridge"), CancellationToken.None));
        Assert.Equal(0, server.ConnectedClients);
    }

    [Fact]
    public async Task ServesTheWorkspaceAndBridgesRequestsOverWebSocket()
    {
        var dispatcher = new BridgeDispatcher();
        var pusher = new BridgeEventPusher();
        dispatcher.Register<EchoParams, string>("test.echo", (parameters, _) => Task.FromResult(parameters.Value + "!"));
        await using var server = WebUiServer.Start(dispatcher, pusher, WebRoot);
        using var http = new HttpClient();

        // Static delivery: index, hashed assets, SPA fallback for routes, 404 for missing files.
        Assert.Contains("AllAgent workspace", await http.GetStringAsync(server.Url));
        Assert.Contains("console.log", await http.GetStringAsync(server.Url + "assets/app.js"));
        Assert.True((await http.GetAsync(server.Url + "assets/app.js")).Headers.CacheControl?.NoCache);
        Assert.Contains("AllAgent workspace", await http.GetStringAsync(server.Url + "deep/route"));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(server.Url + "assets/missing.js")).StatusCode);

        // The browser bridge answers requests keyed by id.
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(server.Url.Replace("http://", "ws://") + "bridge"), CancellationToken.None);
        for (var attempt = 0; attempt < 100 && server.ConnectedClients == 0; attempt++) await Task.Delay(20);
        Assert.Equal(1, server.ConnectedClients);

        var requestId = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.Serialize(new { id = requestId, method = "test.echo", @params = new { value = "hi" } }, JsonOptions.Default);
        using (var document = JsonDocument.Parse(await SendAsync(socket, request)))
        {
            Assert.Equal(requestId, document.RootElement.GetProperty("id").GetString());
            Assert.Equal("response", document.RootElement.GetProperty("type").GetString());
            Assert.Equal("hi!", document.RootElement.GetProperty("data").GetString());
        }

        // Agent events are broadcast to every connected client.
        var pushedTask = ReceiveAsync(socket);
        pusher.Push(JsonSerializer.Serialize(new { type = "event", eventName = "agent.token", streamId = "s1", data = new { text = "hello" } }, JsonOptions.Default));
        using (var pushed = JsonDocument.Parse(await pushedTask.WaitAsync(TimeSpan.FromSeconds(10))))
        {
            Assert.Equal("agent.token", pushed.RootElement.GetProperty("eventName").GetString());
            Assert.Equal("s1", pushed.RootElement.GetProperty("streamId").GetString());
            Assert.Equal("hello", pushed.RootElement.GetProperty("data").GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task ApprovalBrokerPromptsTheBrowserAndAppliesTheAnswer()
    {
        var dispatcher = new BridgeDispatcher();
        var pushed = new List<string>();
        var pusher = new BridgeEventPusher();
        pusher.SetPusher(json => pushed.Add(json));
        var approvals = new WebApprovals(dispatcher, pusher)
        {
            PlanMode = () => false,
            FullAccess = () => false,
            HasClients = () => true,
        };
        using var arguments = JsonDocument.Parse("{\"path\":\"src/App.tsx\"}");
        var pending = approvals.ApproveAsync(new ToolApprovalRequest("file_edit", ToolRisk.Write, arguments.RootElement), CancellationToken.None).AsTask();

        for (var attempt = 0; attempt < 100 && pushed.Count == 0; attempt++) await Task.Delay(10);
        Assert.Single(pushed);
        var requestId = JsonDocument.Parse(pushed[0]).RootElement.GetProperty("requestId").GetString();
        Assert.NotNull(requestId);

        await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "1", method = "agent.approvalRespond", @params = new { requestId, approved = true } }, JsonOptions.Default));
        Assert.True(await pending);
    }

    [Fact]
    public async Task ReplaysPendingPromptsSoAReloadedPageCanStillAnswer()
    {
        var dispatcher = new BridgeDispatcher();
        var pushed = new List<string>();
        var pusher = new BridgeEventPusher();
        pusher.SetPusher(json => pushed.Add(json));
        var approvals = new WebApprovals(dispatcher, pusher) { PlanMode = () => false, FullAccess = () => false, HasClients = () => true };
        using var arguments = JsonDocument.Parse("{\"path\":\"src/App.tsx\"}");
        var pending = approvals.ApproveAsync(new ToolApprovalRequest("file_edit", ToolRisk.Write, arguments.RootElement), CancellationToken.None).AsTask();
        for (var attempt = 0; attempt < 100 && pushed.Count == 0; attempt++) await Task.Delay(10);
        Assert.Single(pushed);

        // Nothing pending any more: a replay is a no-op.
        var settled = JsonDocument.Parse(pushed[0]).RootElement.GetProperty("requestId").GetString();
        await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "1", method = "agent.approvalRespond", @params = new { requestId = settled, approved = false } }, JsonOptions.Default));
        Assert.False(await pending);
        approvals.ReplayPending();
        Assert.Equal(2, pushed.Count);
        Assert.Contains("interactions.resolved", pushed[1]);

        // A prompt still waiting is re-delivered verbatim, so the page can answer it.
        var question = approvals.AskAsync(new AgentQuestion("要继续吗？", new List<string> { "继续", "停下" }, false), CancellationToken.None);
        for (var attempt = 0; attempt < 100 && pushed.Count < 3; attempt++) await Task.Delay(10);
        await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "2", method = "agent.approvalRespond", @params = new { requestId = "no-such-prompt", approved = true } }, JsonOptions.Default));
        approvals.ReplayPending();
        Assert.Equal(4, pushed.Count);
        var replayed = JsonDocument.Parse(pushed[3]).RootElement;
        Assert.Equal("agent.question", replayed.GetProperty("type").GetString());
        Assert.Equal("要继续吗？", replayed.GetProperty("question").GetString());
        var questionId = replayed.GetProperty("id").GetString();
        await dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = "3", method = "agent.questionAnswer", @params = new { id = questionId, answer = "继续" } }, JsonOptions.Default));
        Assert.Equal("继续", await question);
    }

    [Fact]
    public async Task CancellingAnInteractionRemovesItAndNotifiesEveryClient()
    {
        var dispatcher = new BridgeDispatcher();
        var pushed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var pusher = new BridgeEventPusher();
        pusher.SetPusher(pushed.Enqueue);
        var approvals = new WebApprovals(dispatcher, pusher);
        using var cancellation = new CancellationTokenSource();
        var answer = approvals.AskAsync(new AgentQuestion("choose", ["one", "two"], true), cancellation.Token);
        Assert.Single(approvals.PendingPrompts());
        cancellation.Cancel();
        Assert.Null(await answer);
        Assert.Empty(approvals.PendingPrompts());
        Assert.Contains(pushed, json => json.Contains("interactions.resolved"));
        using var response = JsonDocument.Parse(await dispatcher.InvokeAsync("{\"id\":\"pending\",\"method\":\"interactions.pending\"}"));
        Assert.Equal(0, response.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task PermissionModesShortCircuitWithoutPrompting()
    {
        var dispatcher = new BridgeDispatcher();
        var pusher = new BridgeEventPusher();
        using var arguments = JsonDocument.Parse("{}");

        var planApprovals = new WebApprovals(dispatcher, pusher) { PlanMode = () => true, HasClients = () => true };
        Assert.False(await planApprovals.ApproveAsync(new ToolApprovalRequest("file_edit", ToolRisk.Write, arguments.RootElement), CancellationToken.None));

        var fullApprovals = new WebApprovals(dispatcher, pusher) { FullAccess = () => true, HasClients = () => true };
        Assert.True(await fullApprovals.ApproveAsync(new ToolApprovalRequest("file_edit", ToolRisk.Write, arguments.RootElement), CancellationToken.None));

        // Without a browser connected a prompt is denied instead of hanging.
        var offlineApprovals = new WebApprovals(dispatcher, pusher) { HasClients = () => false };
        Assert.False(await offlineApprovals.ApproveAsync(new ToolApprovalRequest("file_edit", ToolRisk.Write, arguments.RootElement), CancellationToken.None));
    }
}
