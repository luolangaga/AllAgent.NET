using System.Text.Json;
using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ParallelToolCallTests
{
    private static JsonElement Batch(int count) => JsonSerializer.SerializeToElement(new
    { calls = Enumerable.Range(0, count).Select(i => new { name = "probe", arguments = new { index = i } }) });

    [Fact]
    public async Task RunsEightConcurrentlyAndPreservesInputOrder()
    {
        var active = 0;
        var started = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var execution = ParallelToolCalls.ExecuteAsync(Batch(16), async (_, args, ct) =>
        {
            Assert.InRange(Interlocked.Increment(ref active), 1, 8);
            if (Interlocked.Increment(ref started) == 8) ready.TrySetResult();
            try { await release.Task.WaitAsync(ct); return ToolResult.Success(args.GetProperty("index").GetInt32()); }
            finally { Interlocked.Decrement(ref active); }
        }, timeout.Token).AsTask();
        try { await ready.Task.WaitAsync(timeout.Token); Assert.Equal(8, Volatile.Read(ref started)); }
        finally { release.TrySetResult(); }
        var result = await execution;
        Assert.True(result.Ok);
        var results = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions).GetProperty("results");
        Assert.Equal(Enumerable.Range(0, 16), results.EnumerateArray().Select(r => r.GetProperty("data").GetInt32()));
    }

    [Theory]
    [InlineData("{\"calls\":[]}")]
    [InlineData("{\"calls\":[{\"name\":\"probe\",\"arguments\":{}},{\"name\":\"parallel\",\"arguments\":{}}]}")]
    [InlineData("{\"calls\":[{\"name\":\"probe\",\"arguments\":{}},{\"name\":\"probe\",\"arguments\":1}]}")]
    public async Task InvalidBatchHasNoSideEffects(string json)
    {
        var invoked = false;
        var result = await ParallelToolCalls.ExecuteAsync(JsonSerializer.Deserialize<JsonElement>(json),
            (_, _, _) => { invoked = true; return new(ToolResult.Success(1)); }, default);
        Assert.False(result.Ok);
        Assert.False(invoked);
    }

    [Fact]
    public async Task CancellationJoinsRunningCalls()
    {
        using var cancel = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var execution = ParallelToolCalls.ExecuteAsync(Batch(16), async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref active) == 8) ready.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return ToolResult.Success(0); }
            finally { Interlocked.Decrement(ref active); }
        }, cancel.Token).AsTask();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(0, Volatile.Read(ref active));
    }

    [Fact]
    public async Task ProviderExpandsCallsAndCountsIndividualFailures()
    {
        await using var provider = new CodeActProvider(new ToolRegistry(), new PythonCodeValidator("unused"), new BatchSandbox());
        var result = await provider.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code = "fixture" }),
            (_, args, _) => new(args.GetProperty("index").GetInt32() == 1 ? ToolResult.Failure("denied") : ToolResult.Success(0)), default);
        var summary = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions).GetProperty("hostCallSummary");
        Assert.Equal(8, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());
        Assert.Equal("probe", summary.GetProperty("failureNames")[0].GetString());
        Assert.Equal("denied", summary.GetProperty("failures")[0].GetProperty("error").GetString());
    }

    [Fact]
    public async Task EveryCallRetainsRegistryApprovalAndDenials()
    {
        var approvals = 0;
        var invoked = 0;
        var registry = new ToolRegistry((_, _) =>
        {
            Interlocked.Increment(ref approvals);
            return ValueTask.FromResult(false);
        });
        registry.Register(new WriteProbe(() => Interlocked.Increment(ref invoked)));
        var result = await ParallelToolCalls.ExecuteAsync(Batch(16), registry.CallAsync, default);
        Assert.False(result.Ok);
        Assert.Equal(16, approvals);
        Assert.Equal(0, invoked);
        var results = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions).GetProperty("results");
        Assert.All(results.EnumerateArray(), r => Assert.Contains("denied", r.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task InvalidBatchIsVisibleEvenWhenGuestDoesNotPrintIt()
    {
        await using var provider = new CodeActProvider(new ToolRegistry(), new PythonCodeValidator("unused"), new InvalidBatchSandbox());
        var result = await provider.ExecuteAsync("execute_code", JsonSerializer.SerializeToElement(new { code = "fixture" }),
            (_, _, _) => throw new InvalidOperationException("Must not be invoked"), default);
        var summary = JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions).GetProperty("hostCallSummary");
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());
        Assert.Contains("forbidden", summary.GetProperty("failures")[0].GetProperty("error").GetString());
    }

    private sealed class InvalidBatchSandbox : ICodeSandbox
    {
        public async Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct)
        {
            await callback("parallel", JsonSerializer.Deserialize<JsonElement>("""{"calls":[{"name":"ask_user","arguments":{}}]}"""), ct);
            return ToolResult.Success(new { stdout = "" });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WriteProbe(Action invoked) : IAgentToolSet
    {
        public IReadOnlyList<AgentToolDescriptor> Definitions => [new("probe", "fixture",
            JsonSerializer.Deserialize<JsonElement>("{}"), ToolRisk.Write)];
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement args, CancellationToken ct)
        { invoked(); return new(ToolResult.Success(0)); }
    }

    private sealed class BatchSandbox : ICodeSandbox
    {
        public async Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct)
        {
            Assert.Contains(tools, t => t.Name == "parallel");
            var result = await callback("parallel", Batch(8), ct);
            Assert.False(result.Ok);
            Assert.Equal(8, JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions).GetProperty("results").GetArrayLength());
            return ToolResult.Success(new { stdout = "finished" });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
