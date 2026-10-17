using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ExecutionPresentationTests
{
    [Fact]
    public void DiffPreservesUnchangedLinesBetweenSeparateEdits()
    {
        var diff = JsonSerializer.SerializeToElement(FileChangePreview.Create("old\nkeep\nold2", "new\nkeep\nnew2"), ToolResult.JsonOptions);
        Assert.Equal(2, diff.GetProperty("added").GetInt32());
        Assert.Equal(2, diff.GetProperty("removed").GetInt32());
        Assert.Contains(diff.GetProperty("lines").EnumerateArray(), line => line.GetProperty("kind").GetString() == "context" && line.GetProperty("text").GetString() == "keep");
    }
    [Fact]
    public async Task FileResultsDescribeActualChangesAndKeepPreviewBounded()
    {
        using var w = new TestWorkspace();
        await w.Tools.WriteAsync("file.txt", "header\nold\nfooter");
        var result = await w.Tools.EditAsync("file.txt", [new("old", "new")]);
        var diff = JsonDocument.Parse(result.ToJson()).RootElement.GetProperty("data").GetProperty("diff");
        Assert.Equal(1, diff.GetProperty("added").GetInt32());
        Assert.Equal(1, diff.GetProperty("removed").GetInt32());
        Assert.Contains(diff.GetProperty("lines").EnumerateArray(), l => l.GetProperty("kind").GetString() == "removed" && l.GetProperty("oldLine").GetInt32() == 2 && l.GetProperty("text").GetString() == "old");
        Assert.Equal("header\nnew\nfooter", await File.ReadAllTextAsync(Path.Combine(w.Root, "file.txt")));
        var large = await w.Tools.WriteAsync("large.txt", string.Join('\n', Enumerable.Repeat("line", 500)));
        var preview = JsonDocument.Parse(large.ToJson()).RootElement.GetProperty("data").GetProperty("diff");
        Assert.True(preview.GetProperty("truncated").GetBoolean());
        Assert.True(preview.GetProperty("lines").GetArrayLength() <= 120);
    }

    [Fact]
    public async Task BackgroundShellReturnsImmediatelyAndCanBeCancelledOrTimeOut()
    {
        using var w = new TestWorkspace(); await using var jobs = new BackgroundTasks();
        var tools = new SystemTools(new(w.Root), new(w.Python, w.Pwsh, []), new Dictionary<string, string> { ["probe"] = Path.Combine(AppContext.BaseDirectory, "probe", "AllAgent.NET.ProcessProbe.exe") }, jobs: jobs);
        var started = await tools.RunShellAsync("probe", ["wait"], timeout_seconds: 300, background: true);
        var id = JsonDocument.Parse(started.ToJson()).RootElement.GetProperty("data").GetProperty("taskId").GetString()!;
        Assert.True(started.Ok);
        Assert.True(jobs.Get(id).State is BackgroundTaskState.Queued or BackgroundTaskState.Running);
        await tools.CancelShellAsync(id);
        Assert.Equal(BackgroundTaskState.Cancelled, (await jobs.WaitAsync(id)).State);
        var timeout = await tools.RunShellAsync("probe", ["wait"], timeout_seconds: 1, background: true);
        var timeoutId = JsonDocument.Parse(timeout.ToJson()).RootElement.GetProperty("data").GetProperty("taskId").GetString()!;
        Assert.Equal(BackgroundTaskState.Failed, (await jobs.WaitAsync(timeoutId)).State);
        Assert.Contains("exceeded", jobs.Get(timeoutId).Error);
    }

    [Fact]
    public async Task BackgroundShellRetainsOutputAndApprovalIsStillRequired()
    {
        using var w = new TestWorkspace(); await using var jobs = new BackgroundTasks();
        var tools = new SystemTools(new(w.Root), new(w.Python, w.Pwsh, []), new Dictionary<string, string> { ["probe"] = Path.Combine(AppContext.BaseDirectory, "probe", "AllAgent.NET.ProcessProbe.exe") }, jobs: jobs);
        var started = await tools.RunShellAsync("probe", ["proof"], background: true);
        var id = JsonDocument.Parse(started.ToJson()).RootElement.GetProperty("data").GetProperty("taskId").GetString()!;
        Assert.Contains("proof", (await jobs.WaitAsync(id)).Output);
        var approvals = 0;
        var registry = new ToolRegistry((_, _) => { approvals++; return ValueTask.FromResult(false); });
        registry.Register(tools);
        var denied = await registry.CallAsync("run_shell", JsonSerializer.SerializeToElement(new { exe = w.Python, args = new[] { "--version" }, background = true }), default);
        Assert.False(denied.Ok); Assert.Equal(1, approvals); Assert.Single(jobs.List);
    }
}
