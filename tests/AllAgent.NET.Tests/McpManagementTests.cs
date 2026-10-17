using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class McpManagementTests
{
    [Fact]
    public async Task HttpMcpResolvesAuthenticationPluginForEveryRequest()
    {
        var authentication = new OAuthTests.FixtureAuthentication();
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            Assert.Equal("Bearer fixture", context.Request.Headers.Authorization.ToString());
            if (context.Request.Method != "POST") { context.Response.StatusCode = 405; return; }
            using var doc = await JsonDocument.ParseAsync(context.Request.Body); var node = doc.RootElement;
            if (!node.TryGetProperty("id", out var id)) { context.Response.StatusCode = 202; return; }
            object result = node.GetProperty("method").GetString() == "initialize" ? new { protocolVersion = node.GetProperty("params").GetProperty("protocolVersion").GetString(), serverInfo = new { name = "Authenticated fixture", version = "1" }, capabilities = new { tools = new { } } } : new { tools = Array.Empty<object>() };
            context.Response.ContentType = "application/json"; await context.Response.WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
        });
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data")); await using var service = new McpToolService(store, new SkillExecutor()) { AuthenticationResolver = _ => authentication };
        var config = new McpServerConfig { Name = "oauth", Command = server.Url, Transport = "http", AuthenticationProviderId = "fixture" };
        Assert.True(await service.ConnectServerAsync(config), service.GetError(config.Id)); Assert.True(authentication.Reads >= 2);
    }
    private static McpServerConfig Config(string name) => new() { Name = name, Command = Path.Combine(AppContext.BaseDirectory, "probe", "AllAgent.NET.ProcessProbe.exe"), ArgumentList = new[] { "mcp", "space argument", "\"literal\"", "$(whoami);&" }, EnvironmentVariables = new() { ["MCP_TEST_LABEL"] = "fixture-label" } };
    [Fact]
    public async Task StdioPreservesArgumentsNestedValuesPromptsResourcesAndReconnect()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data")); await using var service = new McpToolService(store, new SkillExecutor());
        var config = Config("probe"); Assert.True(await service.ConnectServerAsync(config), service.GetError(config.Id));
        var args = JsonSerializer.SerializeToElement(new { payload = new { nested = new[] { 1, 2 }, flag = true } });
        var result = await service.ExecuteServerToolAsync(config.Id, "echo", args, default); Assert.True(result.Ok);
        using var output = JsonDocument.Parse(result.ToJson()); using var content = JsonDocument.Parse(output.RootElement.GetProperty("data").GetProperty("content").GetString()!);
        Assert.Equal("space argument", content.RootElement.GetProperty("startupArgs")[0].GetString()); Assert.Equal("$(whoami);&", content.RootElement.GetProperty("startupArgs")[2].GetString());
        Assert.True(content.RootElement.GetProperty("payload").GetProperty("payload").GetProperty("flag").GetBoolean());
        Assert.Equal(2, content.RootElement.GetProperty("payload").GetProperty("payload").GetProperty("nested")[1].GetInt32());
        Assert.Single(await service.ListPromptsAsync(config.Id)); Assert.Single(await service.ListResourcesAsync(config.Id));
        var prompt = await service.GetPromptAsync(config.Id, "review", new() { ["target"] = "fixture" }); Assert.Contains("Review fixture", prompt.ToJson());
        var resource = await service.ReadResourceAsync(config.Id, "test://proof"); Assert.True(resource.Truncated); using var json = JsonDocument.Parse(resource.ToJson()); Assert.Equal(ToolResult.MaxOutputCharacters, json.RootElement.GetProperty("data").GetProperty("content").GetString()!.Length);
        Assert.True(await service.ConnectServerAsync(config)); Assert.Single(service.GetAllServerTools());
        await service.DisconnectServerAsync(config.Id); Assert.False(service.IsConnected(config.Id)); Assert.Empty(service.GetAllServerTools());
    }
    [Fact]
    public async Task RegistryRefreshRemovesDisconnectedToolsAndNamespacingAvoidsCollisions()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data")); await using var service = new McpToolService(store, new SkillExecutor());
        var first = Config("first"); var second = Config("second"); Assert.True(await service.ConnectServerAsync(first)); Assert.True(await service.ConnectServerAsync(second));
        var registry = new ToolRegistry(); registry.Register(new McpToolSet(service)); Assert.Equal(2, registry.Definitions.Count); Assert.Equal(2, registry.Definitions.Select(d => d.Name).Distinct().Count());
        var old = registry.Definitions.Select(d => d.Name).ToArray(); await service.DisconnectServerAsync(first.Id); registry.Replace<McpToolSet>(new McpToolSet(service)); Assert.Single(registry.Definitions);
        var removed = old.Single(name => !registry.Definitions.Any(d => d.Name == name)); Assert.False((await registry.CallAsync(removed, JsonSerializer.SerializeToElement(new { }), default)).Ok);
    }
    [Fact]
    public async Task ConnectionTimeoutProducesDiagnosticAndNoTools()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data")); await using var service = new McpToolService(store, new SkillExecutor());
        var config = Config("stall"); config.ArgumentList = new[] { "wait" }; config.TimeoutSeconds = 1;
        Assert.False(await service.ConnectServerAsync(config)); Assert.Contains("超时", service.GetError(config.Id)); Assert.False(service.IsConnected(config.Id)); Assert.Empty(service.GetAllServerTools());
    }
    [Fact]
    public async Task StreamableHttpConnectsWithConfiguredHeaders()
    {
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            Assert.Equal("Bearer fixture", context.Request.Headers.Authorization.ToString());
            if (context.Request.Method != "POST") { context.Response.StatusCode = 405; return; }
            using var doc = await JsonDocument.ParseAsync(context.Request.Body); var node = doc.RootElement;
            if (!node.TryGetProperty("id", out var id)) { context.Response.StatusCode = 202; return; }
            object result = node.GetProperty("method").GetString() == "initialize" ? new { protocolVersion = node.GetProperty("params").GetProperty("protocolVersion").GetString(), serverInfo = new { name = "HTTP probe", version = "1" }, capabilities = new { tools = new { } } } : new { tools = Array.Empty<object>() };
            context.Response.ContentType = "application/json"; await context.Response.WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
        });
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data")); await using var service = new McpToolService(store, new SkillExecutor());
        var config = new McpServerConfig { Name = "http", Command = server.Url, Transport = "http", Headers = new() { ["Authorization"] = "Bearer fixture" } };
        Assert.True(await service.ConnectServerAsync(config), service.GetError(config.Id)); Assert.True(service.IsConnected(config.Id));
    }
}
