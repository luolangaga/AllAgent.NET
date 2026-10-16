using System.Text.Json;
using LanAgent.Core.CodeAct;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

public sealed class OutputBudgetTests
{
    [Fact]
    public async Task OversizedResultsKeepStructureAndFailureDetails()
    {
        var registry = new ToolRegistry();
        registry.Register(new Probe(ToolResult.Success(new { results = Enumerable.Range(0, 8).Select(i => new { ok = false, error = "denied", data = new { content = new string('x', 20_000) } }), hostCallSummary = new { total = 8, failed = 8 } })));
        var result = await registry.CallAsync("probe", JsonSerializer.SerializeToElement(new { }), default);
        var data = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        Assert.True(result.Truncated);
        Assert.True(result.ToJson().Length <= 64_000);
        Assert.Equal(8, data.GetProperty("hostCallSummary").GetProperty("failed").GetInt32());
        Assert.Equal(8, data.GetProperty("results").GetArrayLength());
        Assert.All(data.GetProperty("results").EnumerateArray(), r => Assert.Equal("denied", r.GetProperty("error").GetString()));
        Assert.True(result.OutputTruncation!.RemovedJsonCharacters > 0);
        Assert.Equal(result.OutputTruncation.OriginalJsonCharacters - result.OutputTruncation.RetainedJsonCharacters, result.OutputTruncation.RemovedJsonCharacters);
    }

    [Fact]
    public async Task StreamTruncationRetainsOtherFieldsAndReportsExactReduction()
    {
        var registry = new ToolRegistry(maxStreamCharacters: 1000);
        registry.Register(new Probe(ToolResult.Success(new { stdout = new string('x', 5000), exitCode = 7 })));
        var result = await registry.CallAsync("probe", JsonSerializer.SerializeToElement(new { }), default);
        var data = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
        Assert.Equal(1000, data.GetProperty("stdout").GetString()!.Length);
        Assert.Equal(7, data.GetProperty("exitCode").GetInt32());
        Assert.Equal(4000, result.OutputTruncation!.RemovedJsonCharacters);
    }

    [Fact]
    public void HugeNonStringArrayIsStillBounded()
    {
        var result = ToolResultBudget.Apply(ToolResult.Success(new { results = Enumerable.Range(0, 100_000) }), 4096, 1000);
        Assert.True(result.ToJson().Length <= 4096);
        Assert.True(result.Truncated);
        Assert.Equal(JsonValueKind.Array, JsonSerializer.SerializeToElement(result.Data).GetProperty("results").ValueKind);
    }

    [Fact]
    public async Task ConfiguredReadCanReturnOneHundredKilobytesThroughRegistry()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "large.txt"), new string('a', 100_000));
        var registry = new ToolRegistry(maxResultCharacters: 150_000);
        registry.Register(new SystemTools(new WorkspacePolicy(workspace.Root), new(workspace.Python, workspace.Pwsh, []), new Dictionary<string, string>(), fileReadMaxBytes: 110_000));
        var result = await registry.CallAsync("file_read", JsonSerializer.SerializeToElement(new { path = "large.txt", max_bytes = 110_000 }), default);
        Assert.True(result.Ok, result.ToJson());
        Assert.False(result.Truncated);
        Assert.Equal(100_000, JsonSerializer.SerializeToElement(result.Data).GetProperty("content").GetString()!.Length);
        var tiny = await registry.CallAsync("file_read", JsonSerializer.SerializeToElement(new { path = "large.txt", max_bytes = 1 }), default);
        Assert.False(tiny.Ok); // An empty fragment would otherwise leave the continuation cursor unchanged.
    }

    [Fact]
    public void DeepSeekReplaysEmptyReasoningForToolOnlyAssistant()
    {
        var config = new ApiConfig { ProviderId = "openai", ReasoningProtocol = "deepseek", DefaultModel = "deepseek-reasoner" };
        var request = new ChatRequest(config, [new("assistant", "", ToolCallsJson: "[]")], []);
        using var body = JsonDocument.Parse(OpenAiProvider.BuildRawRequestBody(config, request, true));
        Assert.Equal("", body.RootElement.GetProperty("messages")[0].GetProperty("reasoning_content").GetString());
    }

    [Fact]
    public async Task CodeSourceLimitIsConfigurable()
    {
        await using var provider = new CodeActProvider(new ToolRegistry(), new PythonCodeValidator("unused"), new SourceSandbox(), maxCodeCharacters: 100_000);
        var accepted = await provider.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code = new string('x', 80_000) }), (_, _, _) => new(ToolResult.Success(null)), default);
        Assert.True(accepted.Ok);
        var rejected = await provider.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code = new string('x', 100_001) }), (_, _, _) => new(ToolResult.Success(null)), default);
        Assert.False(rejected.Ok);
    }

    [Fact]
    public async Task ParallelAggregateAlsoHonorsRegistryBudget()
    {
        await using var provider = new CodeActProvider(new ToolRegistry(), new PythonCodeValidator("unused"), new LargeBatchSandbox());
        var result = await provider.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code = "fixture" }),
            (_, _, _) => new(ToolResult.Success(new { content = new string('x', 30_000) })), default);
        Assert.True(result.Ok);
    }

    private sealed class LargeBatchSandbox : ICodeSandbox
    {
        public async Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct)
        {
            var batch = await callback("parallel", JsonSerializer.SerializeToElement(new { calls = Enumerable.Range(0, 8).Select(_ => new { name = "probe", arguments = new { } }) }), ct);
            Assert.True(batch.Truncated);
            Assert.True(batch.ToJson().Length <= 64_000);
            Assert.Equal(8, JsonSerializer.SerializeToElement(batch.Data).GetProperty("results").GetArrayLength());
            return ToolResult.Success(new { stdout = "done" });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SourceSandbox : ICodeSandbox
    {
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct) => Task.FromResult(ToolResult.Success(new { stdout = "done" }));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Probe(ToolResult result) : IAgentToolSet
    {
        public IReadOnlyList<AgentToolDescriptor> Definitions => [new("probe", "fixture", JsonSerializer.SerializeToElement(new { type = "object" }), ToolRisk.Read)];
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement args, CancellationToken ct) => new(result);
    }
}
