using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Services;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class BridgeStreamJournalTests
{
    [Fact]
    public void ReconnectSnapshotCoalescesTextAndBoundsOutputWithoutLosingToolLifecycle()
    {
        var journal = new BridgeStreamJournal([], "read") { StreamId = "s" };
        journal.Append(new TokenDelta("中"));
        journal.Append(new TokenDelta("文"));
        journal.Append(new ToolCallStarted("tool", "file_read", "{}"));
        for (var i = 0; i < 1000; i++) journal.Append(new ToolCallOutput("tool", i.ToString(), false));
        journal.Append(new ToolCallCompleted("tool", "file_read", "{\"ok\":true}"));
        journal.Append(new TokenDelta("done"));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(journal.Snapshot(evt => evt.GetType().Name, evt => evt), JsonOptions.Default));
        var events = document.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(404, events.Length);
        Assert.Equal("中文", events[0].GetProperty("data").GetProperty("text").GetString());
        Assert.Equal(2, events[0].GetProperty("sequence").GetInt64());
        Assert.Equal("ToolCallStarted", events[1].GetProperty("eventName").GetString());
        Assert.Equal("600", events[2].GetProperty("data").GetProperty("line").GetString());
        Assert.Equal("ToolCallCompleted", events[^2].GetProperty("eventName").GetString());
        Assert.Equal("done", events[^1].GetProperty("data").GetProperty("text").GetString());
    }
}
