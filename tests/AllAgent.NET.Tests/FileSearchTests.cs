using System.Text.Json;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class FileSearchTests
{
    [Fact]
    public async Task ZeroLimitReturnsAllMatchesAndSupportsOffset()
    {
        using var w = new TestWorkspace();
        await File.WriteAllLinesAsync(Path.Combine(w.Root, "hits.txt"), Enumerable.Repeat("needle", 60));
        var result = await w.Tools.SearchFilesAsync("needle", limit: 0, context_lines: 0);
        Assert.True(result.Ok);
        Assert.False(result.Truncated);
        var data = JsonSerializer.SerializeToElement(result.Data, AllAgent.NET.Core.Tools.ToolResult.JsonOptions);
        Assert.Equal(60, data.GetProperty("matches").GetArrayLength());
        var tail = await w.Tools.SearchFilesAsync("needle", limit: 0, context_lines: 0, offset: 55);
        Assert.True(tail.Ok);
        Assert.Equal(5, JsonSerializer.SerializeToElement(tail.Data, AllAgent.NET.Core.Tools.ToolResult.JsonOptions).GetProperty("matches").GetArrayLength());
        Assert.False((await w.Tools.SearchFilesAsync("needle", limit: -1)).Ok);
    }

    [Fact]
    public async Task ZeroLimitStillProvidesOutputBudgetPagination()
    {
        using var w = new TestWorkspace();
        await File.WriteAllLinesAsync(Path.Combine(w.Root, "hits.txt"), Enumerable.Repeat("needle " + new string('x', 200), 100));
        var offset = 0;
        while (offset < 100)
        {
            var result = await w.Tools.SearchFilesAsync("needle", limit: 0, context_lines: 0, offset: offset);
            Assert.True(result.Ok);
            var data = JsonSerializer.SerializeToElement(result.Data, AllAgent.NET.Core.Tools.ToolResult.JsonOptions);
            var next = data.GetProperty("nextOffset").GetInt32();
            Assert.True(next > offset);
            Assert.Equal(next < 100, result.Truncated);
            Assert.False(data.GetProperty("matchLimitReached").GetBoolean());
            offset = next;
        }
        Assert.Equal(100, offset);
    }

    [Fact]
    public async Task FullAccessCanSearchAnExplicitAncestorDirectory()
    {
        using var w = new TestWorkspace();
        var tools = new AllAgent.NET.Core.Tools.SystemTools(new(w.Root, fullAccess: true), new(w.Python, w.Pwsh, []), new Dictionary<string, string>());
        // A fixture-owned ancestor proves the restriction is gone without scanning user directories.
        var nested = Path.Combine(w.Root, "project"); Directory.CreateDirectory(nested);
        var scopedTools = new AllAgent.NET.Core.Tools.SystemTools(new(nested, fullAccess: true), new(w.Python, w.Pwsh, []), new Dictionary<string, string>());
        await File.WriteAllTextAsync(Path.Combine(w.Root, "local.txt"), "workspace proof");
        Assert.True((await scopedTools.SearchFilesAsync("workspace proof", w.Root)).Ok);
        Assert.True((await tools.SearchFilesAsync("workspace proof")).Ok);
    }
    [Fact]
    public async Task SearchSupportsRegexContextFiltersAndPagination()
    {
        using var w = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(w.Root, "a.cs"), "before\nQuickMode\nFastMode\nafter");
        await File.WriteAllTextAsync(Path.Combine(w.Root, "b.txt"), "QuickMode");
        Directory.CreateDirectory(Path.Combine(w.Root, "obj"));
        await File.WriteAllTextAsync(Path.Combine(w.Root, "obj", "hidden.cs"), "QuickMode");
        var first = await w.Tools.SearchFilesAsync("QuickMode|FastMode", regex: true, file_pattern: "*.cs", limit: 1);
        Assert.True(first.Ok); Assert.True(first.Truncated);
        var data = JsonSerializer.SerializeToElement(first.Data, AllAgent.NET.Core.Tools.ToolResult.JsonOptions);
        var match = Assert.Single(data.GetProperty("matches").EnumerateArray());
        Assert.Equal("a.cs", match.GetProperty("path").GetString());
        Assert.Equal(2, match.GetProperty("line").GetInt32());
        Assert.Equal("before", match.GetProperty("context")[0].GetProperty("text").GetString());
        var second = await w.Tools.SearchFilesAsync("QuickMode|FastMode", regex: true, file_pattern: "*.cs", limit: 1, offset: 1);
        Assert.False(second.Truncated);
        var next = JsonSerializer.SerializeToElement(second.Data, AllAgent.NET.Core.Tools.ToolResult.JsonOptions);
        Assert.Equal(3, next.GetProperty("matches")[0].GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task SearchRejectsInvalidPatternsAndWorkspaceEscape()
    {
        using var w = new TestWorkspace();
        Assert.False((await w.Tools.SearchFilesAsync("[", regex: true)).Ok);
        Assert.True((await w.Tools.SearchFilesAsync("text", context_lines: 6)).Ok);
        Assert.False((await w.Tools.SearchFilesAsync("text", context_lines: -1)).Ok);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Tools.SearchFilesAsync("text", ".."));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.Tools.SearchFilesAsync("text", ct: cancelled.Token));
    }
}
