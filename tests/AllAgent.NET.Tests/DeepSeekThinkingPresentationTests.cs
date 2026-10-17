using System.Text.Json;
using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Services.AI;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Microsoft.AspNetCore.Http;
using Spectre.Console.Testing;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class DeepSeekThinkingPresentationTests
{
    [Fact]
    public async Task CompatibleThinkingFieldWorksWithCompactSseAndEmptyNativeField()
    {
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("""
                data:{"choices":[{"delta":{"reasoning_content":"","reasoning":"兼容接口的思考"}}]}

                data:{"choices":[{"delta":{"content":"完成"},"finish_reason":"stop"}]}

                data:[DONE]

                """);
        });
        var thoughts = new System.Text.StringBuilder();
        await foreach (var chunk in new OpenAiProvider().StreamChatAsync(new(new ApiConfig { ProviderId = "deepseek", DefaultModel = "deepseek-flash", BaseUrl = server.Url }, [new("user", "fixture")])))
            if (chunk.Type == ChunkType.ThinkingDelta) thoughts.Append(chunk.TextDelta);
        Assert.Equal("兼容接口的思考", thoughts.ToString());
    }
    [Fact]
    public async Task StreamedReasoningSurvivesCodeExecutionAndShowsCumulativeCountInFullFrame()
    {
        using var workspace = new TestWorkspace(); using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var requests = 0;
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body);
            Assert.Equal("enabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("high", request.RootElement.GetProperty("reasoning_effort").GetString());
            var first = Interlocked.Increment(ref requests) == 1;
            if (!first) Assert.Contains(request.RootElement.GetProperty("messages").EnumerateArray(),
                m => m.TryGetProperty("reasoning_content", out var reasoning) && reasoning.GetString() == "读取配置\n检查路径");
            context.Response.ContentType = "text/event-stream";
            async Task Send(object delta, string? finish = null) => await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n");
            await Send(new { reasoning_content = first ? "读取配置\n" : "检查完成\n" });
            await Send(new { reasoning_content = first ? "检查路径" : "准备回答" });
            if (first) await Send(new { tool_calls = new[] { new { index = 0, id = "code", function = new { name = "execute_code", arguments = "{\"code\":\"print(1)\"}" } } } }, "tool_calls");
            else await Send(new { content = "完成" }, "stop");
            await context.Response.WriteAsync("data: [DONE]\n\n");
        });
        var config = new ApiConfig { ProviderId = "deepseek", Protocol = "openai", BaseUrl = server.Url, ApiKey = "fixture-key", DefaultModel = "deepseek-flash" };
        ReasoningSettings.SetEffort(config, "high");
        var conversation = new Conversation(); store.AddConversation(conversation);
        var ui = new TerminalUi(false, false); ui.SetModelContext(config);
        var catalog = new SkillCatalog(store, workspace.Root, workspace.Root, workspace.Root);
        await using var mcp = new McpToolService(store, new SkillExecutor());
        var console = new TestConsole(); console.Profile.Width = 80; console.Profile.Height = 24;
        using var screen = new FullScreenTerminal(store, catalog, mcp, ui, console);
        screen.Session(conversation.Id, config.DefaultModel);
        await using var codeAct = new CodeActProvider(new ToolRegistry(), new(workspace.Python), new Sandbox());
        var sawNextIteration = false; var sawFinalReasoning = false;
        await foreach (var evt in new AgentEngine(store, new OpenAiProvider(), codeAct, workspace.Options).RunAsync(conversation.Id, "检查配置", config))
        {
            Assert.IsNotType<AgentError>(evt);
            await ui.RenderAsync(evt, default);
            screen.SetActivity(ui.RenderActivity());
            var output = new TestConsole(); output.Profile.Width = 80; output.Profile.Height = 24;
            output.Write(screen.BuildFrame(80, 24));
            if (evt is AgentIteration { Current: 2 })
            {
                Assert.Matches("检查路径 +（2行）", output.Output);
                Assert.DoesNotContain("等待模型返回", output.Output); sawNextIteration = true;
            }
            if (evt is ThinkingDelta { Text: "准备回答" })
            {
                Assert.Matches("准备回答 +（4行）", output.Output); sawFinalReasoning = true;
            }
            Assert.Contains("思考强度 high", output.Output);
        }
        Assert.True(sawNextIteration); Assert.True(sawFinalReasoning); Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("""{"prompt_tokens":1200,"completion_tokens":12,"prompt_cache_hit_tokens":1024}""", 1024, null)]
    [InlineData("""{"prompt_tokens":1200,"completion_tokens":12,"prompt_tokens_details":{"cached_tokens":512,"cache_write_tokens":256}}""", 512, 256)]
    public async Task CompatibleChatUsageParsesDeepSeekCacheHitsAndCacheWrites(string usage, int cached, int? written)
    {
        await using var server = await LocalApiServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}],\"usage\":" + usage + "}\n\ndata: [DONE]\n\n");
        });
        var config = new ApiConfig { ProviderId = "openai", DefaultModel = "deepseek-chat", BaseUrl = server.Url, ApiKey = "fixture" };
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in new OpenAiProvider().StreamChatAsync(new(config, [new("user", "task")]))) chunks.Add(chunk);
        var reported = Assert.Single(chunks, chunk => chunk.Type == ChunkType.Usage).Usage!;
        Assert.Equal(1200, reported.InputTokens); Assert.Equal(12, reported.OutputTokens);
        Assert.Equal(cached, reported.CachedInputTokens); Assert.Equal(written, reported.CacheWriteInputTokens);
    }

    private sealed class Sandbox : ICodeSandbox
    {
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct) =>
            Task.FromResult(ToolResult.Success(new { stdout = "1", stderr = "", exitCode = 0 }));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
