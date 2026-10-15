using System.Text.Json;
using LanAgent.Core.Tools;
using Xunit;

namespace LanAgent.Tests;

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

}
