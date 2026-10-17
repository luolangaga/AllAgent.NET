using System.Diagnostics;
using System.Text.Json;
using AllAgent.NET.Core.Runtime;
using AllAgent.NET.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace AllAgent.NET.Tests;

public sealed class EverythingSearchTests(ITestOutputHelper log) : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AllAgent.Everything.Tests", Guid.NewGuid().ToString("N"), "中文, folder")).FullName;
    private SystemTools Tools => new(new(_root), new(null, null, []), new Dictionary<string, string>());
    private static JsonElement Data(ToolResult result) { Assert.True(result.Ok, result.Error); return JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions); }

    [Fact]
    public async Task BundledEngineStartsIndexesPaginatesAndMonitorsWithoutInstalledEverything()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var file in new[] { "合同 a.txt", "合同 b.txt", "other.pdf" }) await File.WriteAllTextAsync(Path.Combine(_root, file), "fixture");
        var timer = Stopwatch.StartNew();
        var first = Data(await Tools.SearchEverythingAsync("*.txt", limit: 1));
        log.WriteLine($"Cold bundled startup/index: {timer.ElapsedMilliseconds}ms");
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.Single(first.GetProperty("paths").EnumerateArray());
        var second = Data(await Tools.SearchEverythingAsync("*.txt", limit: 1, offset: first.GetProperty("nextOffset").GetInt32()));
        Assert.False(second.GetProperty("hasMore").GetBoolean());
        Assert.Single(second.GetProperty("paths").EnumerateArray());
        Assert.NotEqual(first.GetProperty("paths")[0].GetString(), second.GetProperty("paths")[0].GetString());
        timer.Restart();
        Assert.Equal(2, Data(await Tools.SearchEverythingAsync("合同")).GetProperty("paths").GetArrayLength());
        log.WriteLine($"Warm filename search: {timer.ElapsedMilliseconds}ms");
        await File.WriteAllTextAsync(Path.Combine(_root, "新增.txt"), "new");
        var observed = false;
        for (var i = 0; i < 30 && !observed; i++)
        {
            observed = Data(await Tools.SearchEverythingAsync("新增")).GetProperty("paths").GetArrayLength() == 1;
            if (!observed) await Task.Delay(100);
        }
        Assert.True(observed, "Folder change monitoring did not discover the new file.");
        // Scope changes restart the owned process; an existing scope can be reused.
        var nested = Directory.CreateDirectory(Path.Combine(_root, "nested")).FullName;
        Assert.Empty(Data(await Tools.SearchEverythingAsync("*.txt", nested)).GetProperty("paths").EnumerateArray());
        await File.WriteAllTextAsync(Path.Combine(_root, "offline.txt"), "written while the root indexer is stopped");
        Assert.Equal(4, Data(await Tools.SearchEverythingAsync("*.txt")).GetProperty("paths").GetArrayLength());
    }

    [Fact]
    public async Task LiteralOperatorsAndSwitchesCannotChangeTheQueryMode()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var file in new[] { "-exit.txt", "wow!.txt", "a b.txt", "plain.txt" }) await File.WriteAllTextAsync(Path.Combine(_root, file), "");
        foreach (var query in new[] { "-exit", "wow!", "a b" })
            Assert.Single(Data(await Tools.SearchEverythingAsync(query)).GetProperty("paths").EnumerateArray());
        Assert.Empty(Data(await Tools.SearchEverythingAsync("plain|wow")).GetProperty("paths").EnumerateArray());
    }

    [Fact]
    public async Task ToolIsRegisteredAndEnforcesExternalDirectoryApprovalAndCancellation()
    {
        Assert.Contains(Tools.Definitions, t => t.Name == "everything_search" && t.Risk == ToolRisk.Read);
        Assert.DoesNotContain(Tools.Definitions, t => t.Name == "file_glob");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Tools.SearchEverythingAsync("*", ".."));
        var args = JsonSerializer.SerializeToElement(new { path = Path.GetDirectoryName(_root) });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await Tools.RequestAccessAsync("everything_search", args, null, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tools.SearchEverythingAsync("*", ct: cancellation.Token));
        Assert.False((await Tools.SearchEverythingAsync("", limit: 1)).Ok);
        Assert.False((await Tools.SearchEverythingAsync("*", limit: 0)).Ok);
        Assert.False((await Tools.SearchEverythingAsync("*", offset: -1)).Ok);
        Assert.False((await Tools.SearchEverythingAsync("content:secret")).Ok);
    }

    [Fact]
    public void StaleAndOutOfScopeResultsAreFilteredWithoutBreakingPagination()
    {
        var file = Path.Combine(_root, "present.txt"); File.WriteAllText(file, "");
        var output = new ProcessOutput(0, $"{Path.GetDirectoryName(_root)}\n{Path.Combine(_root, "missing.txt")}\n{file}\n", "", false);
        var data = Data(EverythingSearch.CreateResult(_root, "*", 2, 5, output, default));
        Assert.Empty(data.GetProperty("paths").EnumerateArray());
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        Assert.Equal(7, data.GetProperty("nextOffset").GetInt32());
    }

    [Fact]
    public void OutputBudgetPagesWithoutSkippingPaths()
    {
        var file = Path.Combine(_root, "预算.txt"); File.WriteAllText(file, "");
        var data = Data(EverythingSearch.CreateResult(_root, "*", 2000, 0,
            new(0, string.Concat(Enumerable.Repeat(file + "\r\n", 2001)), "", false), default));
        Assert.True(data.GetProperty("outputTruncated").GetBoolean());
        Assert.Equal(data.GetProperty("paths").GetArrayLength(), data.GetProperty("nextOffset").GetInt32());
        Assert.InRange(data.GetProperty("nextOffset").GetInt32(), 1, 1999);
    }

    [Fact]
    public async Task NativeErrorsAreReportedInsteadOfReturningAnEmptySuccessfulSearch()
    {
        var result = await EverythingSearch.SearchAsync(_root, "*", 50, 0, default,
            (_, _) => Task.FromResult(new ProcessOutput(8, "IPC missing", "", false)));
        Assert.False(result.Ok);
        Assert.Contains("could not start", result.Error);
    }

    public void Dispose()
    {
        EverythingIndexer.Stop();
        Directory.Delete(_root, true);
    }
}
