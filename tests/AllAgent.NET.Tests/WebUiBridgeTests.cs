using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
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
