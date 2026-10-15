using System.Text.Json;
using Xunit;

namespace LanAgent.Tests;

public sealed class FileSearchTests
{
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
        var data = JsonSerializer.SerializeToElement(first.Data, LanAgent.Core.Tools.ToolResult.JsonOptions);
        var match = Assert.Single(data.GetProperty("matches").EnumerateArray());
        Assert.Equal(2, match.GetProperty("line").GetInt32());
        Assert.Equal("before", match.GetProperty("context")[0].GetProperty("text").GetString());
        var second = await w.Tools.SearchFilesAsync("QuickMode|FastMode", regex: true, file_pattern: "*.cs", limit: 1, offset: 1);
        Assert.False(second.Truncated);
        var next = JsonSerializer.SerializeToElement(second.Data, LanAgent.Core.Tools.ToolResult.JsonOptions);
        Assert.Equal(3, next.GetProperty("matches")[0].GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task GlobDistinguishesSingleDirectoryAndRecursiveWildcards()
    {
        using var w = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(w.Root, "root.ts"), "");
        Directory.CreateDirectory(Path.Combine(w.Root, "src", "Strings"));
        await File.WriteAllTextAsync(Path.Combine(w.Root, "src", "Strings", "Resources.ts"), "");
        var top = await w.Tools.GlobFilesAsync("*.ts");
        var recursive = await w.Tools.GlobFilesAsync("**/*.ts", limit: 1);
        var specific = await w.Tools.GlobFilesAsync("**/Strings/*.ts");
        var data = JsonSerializer.SerializeToElement(top.Data, LanAgent.Core.Tools.ToolResult.JsonOptions);
        Assert.Equal("root.ts", Assert.Single(data.GetProperty("paths").EnumerateArray()).GetString());
        Assert.True(recursive.Truncated);
        var found = JsonSerializer.SerializeToElement(specific.Data, LanAgent.Core.Tools.ToolResult.JsonOptions);
        Assert.Equal("src/Strings/Resources.ts", Assert.Single(found.GetProperty("paths").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task SearchRejectsInvalidPatternsAndWorkspaceEscape()
    {
        using var w = new TestWorkspace();
        Assert.False((await w.Tools.SearchFilesAsync("[", regex: true)).Ok);
        Assert.True((await w.Tools.SearchFilesAsync("text", context_lines: 6)).Ok);
        Assert.False((await w.Tools.SearchFilesAsync("text", context_lines: -1)).Ok);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Tools.SearchFilesAsync("text", ".."));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Tools.GlobFilesAsync("*", ".."));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.Tools.SearchFilesAsync("text", ct: cancelled.Token));
    }
}
