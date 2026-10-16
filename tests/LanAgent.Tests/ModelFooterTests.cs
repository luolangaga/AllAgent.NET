using LanAgent.Core.Extensions;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public class ModelFooterTests
{
    [Fact]
    public async Task TokenTotalsAccumulateAcrossRequestsAndResetAtNextTurn()
    {
        var ui = new TerminalUi(false, false);
        await ui.RenderAsync(new AgentTokenUsage(100, 10), default);
        await ui.RenderAsync(new AgentTokenUsage(200, 20), default);
        Assert.Equal(300, ui.TurnInputTokens);
        Assert.Equal(30, ui.TurnOutputTokens);
        await ui.RunAgentAsync(Empty(), default);
        Assert.Equal(0, ui.TurnInputTokens);
        Assert.Contains("等待模型上报", ui.TurnTokenLabel);
    }
    private static async IAsyncEnumerable<AgentEvent> Empty() { await Task.CompletedTask; yield break; }

    [Fact]
    public async Task SwitchingModelDiscardsInflightOldContributionAndCachesNewOne()
    {
        var old = new TaskCompletionSource<IReadOnlyList<ModelFooterItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var footer = new ModelFooter(() => [new("quota", _ => true, async (context, _) =>
        {
            Interlocked.Increment(ref calls);
            if (context.ModelId == "old") { started.TrySetResult(); return await old.Task; }
            return [new("积分", "42")];
        })]);
        var config = new ApiConfig { DefaultModel = "old" };
        Assert.Equal("", footer.Read(config));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        config.DefaultModel = "new";
        Assert.Equal("", footer.Read(config));
        old.SetResult([new("旧额度", "99")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!footer.Read(config).Contains("42")) await Task.Delay(10, timeout.Token);
        Assert.DoesNotContain("99", footer.Read(config));
        for (var i = 0; i < 100; i++) footer.Read(config);
        Assert.Equal(2, calls);
    }
}
