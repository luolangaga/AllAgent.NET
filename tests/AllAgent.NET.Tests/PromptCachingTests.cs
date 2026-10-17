using System.Text.Json;
using System.Text.Json.Nodes;
using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class PromptCachingTests
{
    private static ChatRequest Request() => new(new ApiConfig { DefaultModel = "fixture-model" },
        [new("system", "Reusable policy"), new("system", "Project instructions"), new("user", "Research this")],
        [new("zeta", "Last", JsonSerializer.SerializeToElement(new { type = "object" })),
         new("alpha", "First", JsonSerializer.SerializeToElement(new { type = "object" }))]);

    [Fact]
    public void ProvidersKeepIdenticalToolPrefixesWhenRegistrationOrderChanges()
    {
        var request = Request();
        var reversed = request with { Tools = request.Tools!.Reverse().ToArray() };
        JsonNode Chat(ChatRequest value) => JsonNode.Parse(OpenAiProvider.BuildRawRequestBody(value.Config, value, true))!;
        JsonNode Gemini(ChatRequest value) => JsonSerializer.SerializeToNode(GeminiProvider.BuildBody(value))!;
        foreach (var build in new Func<ChatRequest, JsonNode>[] { Chat, Gemini, AnthropicProvider.Body, ResponsesProvider.Body })
        {
            var first = build(request);
            var second = build(reversed);
            Assert.Equal(first["tools"]!.ToJsonString(), second["tools"]!.ToJsonString());
            Assert.True(first["tools"]!.ToJsonString().IndexOf("alpha", StringComparison.Ordinal) < first["tools"]!.ToJsonString().IndexOf("zeta", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AnthropicCachesSystemAndGrowingHistoryWithoutModifyingSignedThinking()
    {
        var state = """{"protocol":"anthropic","content":[{"type":"thinking","thinking":"private","signature":"signed"},{"type":"tool_use","id":"call1","name":"alpha","input":{}}]}""";
        var request = Request() with { Messages = [new("system", "Reusable policy"), new("system", "Project instructions"), new("user", "Research this"), new("assistant", "", ProviderStateJson: state), new("tool", "Source text", ToolCallId: "call1")] };
        var body = AnthropicProvider.Body(request);
        var system = body["system"]!.AsArray();
        Assert.Equal("Reusable policy", system[0]!["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", system[0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("Project instructions", system[1]!["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", system[1]!["cache_control"]!["type"]!.GetValue<string>());
        var messages = body["messages"]!.AsArray();
        Assert.Equal("ephemeral", messages[^1]!["content"]![0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Null(messages[1]!["content"]![0]!["cache_control"]);
        Assert.Equal("signed", messages[1]!["content"]![0]!["signature"]!.GetValue<string>());
        Assert.Equal(state, request.Messages[3].ProviderStateJson);
        Assert.Null(body["tools"]![0]!["cache_control"]);
        Assert.Equal("ephemeral", messages[1]!["content"]![1]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal(4, body.ToJsonString().Split("cache_control").Length - 1);
    }

    [Fact]
    public void AnthropicKeepsTheStableBreakpointBeforeVolatilePolicy()
    {
        var request = Request() with
        {
            Messages =
            [
                new("system", "Reusable policy"),
                new("system", "Project instructions"),
                new ChatMessage("system", "Question preference: minimal") { IsVolatileContext = true },
                new("user", "Research this")
            ]
        };
        var body = AnthropicProvider.Body(request);
        var system = body["system"]!.AsArray();
        Assert.Equal(3, system.Count);
        Assert.Equal("ephemeral", system[0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("ephemeral", system[1]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Null(system[2]!["cache_control"]);
        // Stable system blocks stay byte-identical when only the volatile tail changes.
        var changed = request with { Messages = request.Messages.Select(message => message.IsVolatileContext ? message with { Content = "Question preference: proactive" } : message).ToArray() };
        var changedSystem = AnthropicProvider.Body(changed)["system"]!.AsArray();
        Assert.Equal(system[0]!.ToJsonString(), changedSystem[0]!.ToJsonString());
        Assert.Equal(system[1]!.ToJsonString(), changedSystem[1]!.ToJsonString());
    }

    [Fact]
    public void AnthropicAppliesExtendedTtlToEveryBreakpointOnlyWhenConfigured()
    {
        var request = Request() with
        {
            Messages = [new("system", "Reusable policy"), new("system", "Project instructions"), new("user", "Research this"), new("assistant", "Working"), new("user", "Continue")]
        };
        Assert.DoesNotContain("\"ttl\"", AnthropicProvider.Body(request).ToJsonString());
        var extended = AnthropicProvider.Body(request with { PromptCacheTtl = "1h" });
        Assert.Equal(4, extended.ToJsonString().Split("cache_control").Length - 1);
        Assert.Equal(4, extended.ToJsonString().Split("\"ttl\":\"1h\"").Length - 1);
    }

    [Fact]
    public void GeminiPreservesAllSystemInstructionsAndConversationOrder()
    {
        var body = JsonSerializer.SerializeToNode(GeminiProvider.BuildBody(Request()))!;
        Assert.Equal("Reusable policy", body["system_instruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("Project instructions", body["system_instruction"]!["parts"]![1]!["text"]!.GetValue<string>());
        Assert.Equal("Research this", body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodeActKeepsReusableGuidanceBeforeRuntimeContextAndPermissionChanges(bool includeCapabilities)
    {
        var registry = new ToolRegistry(); registry.Register(new FixtureTools(includeCapabilities));
        await using var ask = new CodeActProvider(registry, new PythonCodeValidator("unused"), new FixtureSandbox("runtime-A"));
        await using var full = new CodeActProvider(registry, new PythonCodeValidator("unused"), new FixtureSandbox("runtime-B"), fullAccess: true);
        await ask.PrepareAsync(); await full.PrepareAsync();
        var marker = "\n\n## Sandbox runtime\n";
        var askPrefix = ask.Instructions[..ask.Instructions.IndexOf(marker, StringComparison.Ordinal)];
        var fullPrefix = full.Instructions[..full.Instructions.IndexOf(marker, StringComparison.Ordinal)];
        Assert.Equal(askPrefix, fullPrefix);
        Assert.StartsWith("You are AllAgent", askPrefix);
        if (includeCapabilities)
        {
            Assert.Contains("web_search", askPrefix); Assert.Contains("subagent", askPrefix);
            Assert.Contains("## Research", askPrefix); Assert.Contains("## Delegation", askPrefix);
        }
        else
        {
            Assert.DoesNotContain("web_search", askPrefix); Assert.DoesNotContain("subagent", askPrefix);
            Assert.DoesNotContain("## Research", askPrefix); Assert.DoesNotContain("## Delegation", askPrefix);
        }
        Assert.True(askPrefix.IndexOf("\"Z\"", StringComparison.Ordinal) < askPrefix.IndexOf("\"a\"", StringComparison.Ordinal));
        Assert.Contains("runtime-A", ask.Instructions); Assert.Contains("FULL ACCESS", full.Instructions);
    }

    private sealed class FixtureSandbox(string context) : ICodeSandbox
    {
        public Task<CodeSandboxContext?> PrepareAsync(IReadOnlyList<AgentToolDescriptor> tools, CancellationToken ct) => Task.FromResult<CodeSandboxContext?>(new(context, "Execute Python"));
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct) => Task.FromResult(ToolResult.Success("unused"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FixtureTools(bool includeCapabilities) : IAgentToolSet
    {
        public IReadOnlyList<AgentToolDescriptor> Definitions => new[] { "a", "Z" }.Concat(includeCapabilities ? new[] { "web_search", "subagent" } : []).Select(name => new AgentToolDescriptor(name, "Fixture " + name, JsonSerializer.SerializeToElement(new { type = "object" }), ToolRisk.Read)).ToArray();
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct) => ValueTask.FromResult(ToolResult.Success("unused"));
    }
}
