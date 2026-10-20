using System.Text.Json;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class AgentQuestionTests
{
    [Fact]
    public async Task MultiSelectReturnsAnArrayIncludingUnicode()
    {
        var tools = new AgentInteractionTools((_, _) => Task.FromResult<string?>(JsonSerializer.Serialize(new[] { "通知", "小岛" })));
        var result = await tools.AskAsync("功能？", ["通知", "小岛"], true);
        using var json = JsonDocument.Parse(result.ToJson());
        var answer = json.RootElement.GetProperty("data").GetProperty("answer");
        Assert.Equal(JsonValueKind.Array, answer.ValueKind);
        Assert.Equal("小岛", answer[1].GetString());
    }

    [Fact]
    public async Task UnansweredQuestionTimesOutAndIsSkipped()
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = new AgentInteractionTools(async (_, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(true); }
            return null;
        }, TimeSpan.FromMilliseconds(50));

        var result = await tools.AskAsync("Continue?", []);

        Assert.False(result.Ok);
        Assert.Contains("skipped", result.Error);
        Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

}
