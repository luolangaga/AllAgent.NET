using System.Runtime.CompilerServices;
using System.Text.Json;
using LanAgent.Core.Services;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class BackgroundTaskTests
{
    [Fact]
    public async Task DefaultStartsFortyJobsTogetherWithoutQueueOrAdmissionCap()
    {
        await using var jobs = new BackgroundTasks();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.Increment(ref started) == 40) allStarted.TrySetResult();
            await release.Task.WaitAsync(ct); yield return new AgentDone("stop");
        }
        var ids = Enumerable.Range(0, 40).Select(i => jobs.Start($"parallel-{i}", Guid.NewGuid(), "shell", Run)).ToArray();
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(40, started);
            Assert.All(jobs.List, job => Assert.Equal(BackgroundTaskState.Running, job.State));
        }
        finally { release.TrySetResult(); }
        Assert.All(await Task.WhenAll(ids.Select(id => jobs.WaitAsync(id))), job => Assert.Equal(BackgroundTaskState.Completed, job.State));
    }

    [Fact]
    public async Task ConcurrencyIsUnlimitedByDefaultAndExplicitLimitsHaveNoEightSlotCeiling()
    {
        Assert.Equal(0, TerminalArguments.Parse([]).BackgroundConcurrency);
        Assert.Equal(0, TerminalArguments.Parse(["--background-concurrency", "0"]).BackgroundConcurrency);
        Assert.Equal(64, TerminalArguments.Parse(["--background-concurrency", "64"]).BackgroundConcurrency);
        Assert.Throws<ArgumentException>(() => TerminalArguments.Parse(["--background-concurrency", "-1"]));
        Assert.Throws<ArgumentException>(() => new BackgroundTasks(concurrency: -1));
        await using var jobs = new BackgroundTasks(concurrency: 64);
    }

    [Fact]
    public async Task ReadOnlySnapshotDoesNotInterruptOrRewriteRunningJobs()
    {
        using var w = new TestWorkspace(); var record = new BackgroundTaskInfo("live", "live job", Guid.NewGuid(), "background", BackgroundTaskState.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var path = Path.Combine(w.Root, "background-tasks.json"); var bytes = JsonSerializer.Serialize(record); await File.WriteAllTextAsync(path, "[" + bytes + "]"); var before = await File.ReadAllTextAsync(path);
        await using var snapshot = new BackgroundTasks(w.Root, readOnly: true); Assert.Equal(BackgroundTaskState.Running, snapshot.Get("live").State); Assert.Equal(before, await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { record with { State = BackgroundTaskState.Completed } })); Assert.Equal(BackgroundTaskState.Completed, snapshot.Get("live").State);
        Assert.Throws<InvalidOperationException>(() => snapshot.Start("not allowed", Guid.NewGuid(), "background", (_, _) => throw new InvalidOperationException()));
    }
    [Fact]
    public async Task JobWaitsForExplicitApprovalAndAnswerThenPersistsResult()
    {
        using var w = new TestWorkspace(); await using (var jobs = new BackgroundTasks(w.Root))
        {
            async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
            {
                Assert.True(await jobs.ApproveAsync(id, new("file_write", ToolRisk.Write, JsonSerializer.SerializeToElement(new { path = "test" })), ct));
                Assert.Equal("human answer", await jobs.AskAsync(id, new AgentQuestion("Question", []), ct));
                yield return new TokenDelta("result proof"); yield return new AgentDone("stop");
            }
            var id = jobs.Start("test", Guid.NewGuid(), "agent", Run); await Until(() => jobs.Get(id).State == BackgroundTaskState.AwaitingApproval); Assert.NotNull(jobs.Interaction(id)?.Approval); jobs.Respond(id, "allow");
            await Until(() => jobs.Get(id).State == BackgroundTaskState.AwaitingAnswer); jobs.Respond(id, "human answer"); var done = await jobs.WaitAsync(id); Assert.Equal(BackgroundTaskState.Completed, done.State); Assert.Equal("result proof", done.Output);
        }
        await using var loaded = new BackgroundTasks(w.Root); Assert.Equal(BackgroundTaskState.Completed, Assert.Single(loaded.List).State);
    }
    [Fact]
    public async Task ConcurrencyQueueCancellationAndExitInterruptionAreBounded()
    {
        using var w = new TestWorkspace(); await using var jobs = new BackgroundTasks(w.Root, 1);
        async IAsyncEnumerable<AgentEvent> Wait(string id, [EnumeratorCancellation] CancellationToken ct) { await Task.Delay(5000, ct); yield return new AgentDone("stop"); }
        var first = jobs.Start("first", Guid.NewGuid(), "background", Wait); await Until(() => jobs.Get(first).State == BackgroundTaskState.Running);
        var second = jobs.Start("second", Guid.NewGuid(), "background", Wait); Assert.Equal(BackgroundTaskState.Queued, jobs.Get(second).State); jobs.Cancel(second); Assert.Equal(BackgroundTaskState.Cancelled, (await jobs.WaitAsync(second)).State);
        jobs.Cancel(first); Assert.Equal(BackgroundTaskState.Cancelled, (await jobs.WaitAsync(first)).State);
        await File.WriteAllTextAsync(Path.Combine(w.Root, "background-tasks.json"), JsonSerializer.Serialize(new[] { new BackgroundTaskInfo("old", "interrupted", Guid.NewGuid(), "agent", BackgroundTaskState.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) }));
        await using var reopened = new BackgroundTasks(w.Root); Assert.Equal(BackgroundTaskState.Interrupted, Assert.Single(reopened.List).State);
    }
    private static async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(5000); while (!condition()) await Task.Delay(10, timeout.Token); }
}
