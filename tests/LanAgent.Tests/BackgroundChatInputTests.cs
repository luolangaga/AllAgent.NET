using System.Runtime.CompilerServices;
using LanAgent.Core.Services;
using Xunit;

namespace LanAgent.Tests;

public sealed class BackgroundChatInputTests
{
    [Fact]
    public async Task ChatTargetsOnlyItsOwnLiveTaskAndCanContinueAfterCompletion()
    {
        await using var tasks = new BackgroundTasks();
        var session = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = false;
        var runs = 0;
        async IAsyncEnumerable<AgentEvent> Run(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            runs++;
            pending = false;
            yield return new AgentDone("stop");
            if (runs == 1) { started.SetResult(); await release.Task.WaitAsync(ct); }
        }
        var job = tasks.Start("chat", session, "agent", Run, Guid.NewGuid(), () => pending);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(tasks.IsSessionRunning(session));
            Assert.False(tasks.IsSessionRunning(Guid.NewGuid()));
            Assert.False(tasks.TryPostInput(Guid.NewGuid(), () => throw new Exception("unrelated chat")));
            Assert.True(tasks.TryPostInput(session, () => pending = true));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(BackgroundTaskState.Completed, (await tasks.WaitAsync(job)).State);
        Assert.Equal(2, runs);
        Assert.False(tasks.IsSessionRunning(session));
        Assert.False(tasks.TryPostInput(session, () => throw new Exception("start a new foreground turn")));
    }
}
