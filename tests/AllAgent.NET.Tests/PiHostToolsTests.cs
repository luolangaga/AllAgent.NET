using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class PiHostToolsTests
{
    private static JsonElement Data(ToolResult result)
    { Assert.True(result.Ok, result.ToJson()); return JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions); }

    [Fact]
    public async Task ReadPagesByLineAndConnectsToSearchLineNumbers()
    {
        using var w = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(w.Root, "file.txt"), "first\r\nneedle\r\nlast\r\n", new UTF8Encoding(true));
        var search = Data(await w.Tools.SearchFilesAsync("needle", "file.txt"));
        var line = search.GetProperty("matches")[0].GetProperty("line").GetInt32();
        var read = await w.Tools.ReadAsync("file.txt", offset: line, limit: 1); var data = Data(read);
        Assert.Equal("needle", data.GetProperty("content").GetString()); Assert.Equal(3, data.GetProperty("nextOffset").GetInt32());
        Assert.True(read.Truncated);
        var last = await w.Tools.ReadAsync("file.txt", offset: 3, limit: 1);
        Assert.Equal("last", Data(last).GetProperty("content").GetString()); Assert.False(last.Truncated);
        Assert.False((await w.Tools.ReadAsync("file.txt", offset: 0)).Ok);
        Assert.False((await w.Tools.ReadAsync("file.txt", offset: 4)).Ok);
    }

    [Fact]
    public async Task ReadStopsAtCompleteLinesForByteAndLineBudgets()
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "file.txt");
        var line = new string('中', 3000); await File.WriteAllTextAsync(target, line + "\n" + line + "\n" + line);
        var first = await w.Tools.ReadAsync("file.txt"); var data = Data(first);
        Assert.Equal(line + "\n" + line, data.GetProperty("content").GetString());
        Assert.Equal(3, data.GetProperty("nextOffset").GetInt32()); Assert.Equal("bytes", data.GetProperty("truncatedBy").GetString());
        await File.WriteAllTextAsync(target, string.Join('\n', Enumerable.Range(1, 2100)));
        data = Data(await w.Tools.ReadAsync("file.txt")); Assert.Equal(2000, data.GetProperty("outputLines").GetInt32());
        Assert.Equal(2001, data.GetProperty("nextOffset").GetInt32()); Assert.Equal("lines", data.GetProperty("truncatedBy").GetString());
    }

    [Fact]
    public async Task OversizedUnicodeLineCanBeReconstructedWithoutBrokenCharacters()
    {
        using var w = new TestWorkspace();
        var original = string.Concat(Enumerable.Repeat("中😀", 7000));
        await File.WriteAllTextAsync(Path.Combine(w.Root, "line.txt"), original);
        var restored = new StringBuilder(); var column = 0;
        while (true)
        {
            var result = await w.Tools.ReadAsync("line.txt", column_offset: column); var data = Data(result);
            var fragment = data.GetProperty("content").GetString()!; Assert.DoesNotContain('\ufffd', fragment);
            Assert.True(Encoding.UTF8.GetByteCount(fragment) <= 20000); restored.Append(fragment);
            if (data.GetProperty("nextColumnOffset").ValueKind == JsonValueKind.Null) break;
            var next = data.GetProperty("nextColumnOffset").GetInt32(); Assert.True(next > column); column = next;
        }
        Assert.Equal(original, restored.ToString());
    }

    [Fact]
    public async Task ReadDetectsImageBytesAndKeepsThemOutOfJson()
    {
        using var w = new TestWorkspace();
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC");
        await File.WriteAllBytesAsync(Path.Combine(w.Root, "image.bin"), bytes);
        var result = await w.Tools.ReadAsync("image.bin"); var data = Data(result);
        Assert.Equal("image/png", data.GetProperty("mediaType").GetString());
        Assert.Equal(Convert.ToBase64String(bytes), Assert.Single(result.Images).Base64);
        Assert.DoesNotContain(Convert.ToBase64String(bytes), result.ToJson());
    }

    [Fact]
    public async Task ReadHandlesEmptyFilesAndRejectsBinaryText()
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "file.txt");
        await File.WriteAllTextAsync(target, ""); Assert.Equal("", Data(await w.Tools.ReadAsync("file.txt")).GetProperty("content").GetString());
        await File.WriteAllBytesAsync(target, [65, 0, 66, 0]); Assert.False((await w.Tools.ReadAsync("file.txt")).Ok);
    }

    [Fact]
    public async Task SearchAndGlobHonorNestedIgnoresNegationsAndExplicitOptIn()
    {
        using var w = new TestWorkspace(); Directory.CreateDirectory(Path.Combine(w.Root, ".git"));
        Directory.CreateDirectory(Path.Combine(w.Root, "src")); Directory.CreateDirectory(Path.Combine(w.Root, "src", "obj"));
        await File.WriteAllTextAsync(Path.Combine(w.Root, ".gitignore"), "ignored.cs\n*.generated.cs\n!keep.generated.cs\n");
        await File.WriteAllTextAsync(Path.Combine(w.Root, "src", ".gitignore"), "nested.cs\n");
        foreach (var file in new[] { "source.cs", "ignored.cs", "omit.generated.cs", "keep.generated.cs", "src/nested.cs", "src/visible.cs", "src/obj/build.cs" })
            await File.WriteAllTextAsync(Path.Combine(w.Root, file), "needle");
        var found = Data(await w.Tools.SearchFilesAsync("needle", file_pattern: "*.cs"));
        var paths = found.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("path").GetString()).ToArray();
        Assert.Equal(new[] { "keep.generated.cs", "source.cs", "src/visible.cs" }, paths);
        Assert.Equal(7, Data(await w.Tools.SearchFilesAsync("needle", file_pattern: "*.cs", include_ignored: true)).GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public async Task SearchIgnoresWithoutGitDirectoryAndPathFiltersAreSegmentAware()
    {
        using var w = new TestWorkspace(); Directory.CreateDirectory(Path.Combine(w.Root, "src", "nested"));
        await File.WriteAllTextAsync(Path.Combine(w.Root, ".gitignore"), "ignored.txt\n");
        await File.WriteAllTextAsync(Path.Combine(w.Root, "ignored.txt"), "needle");
        await File.WriteAllTextAsync(Path.Combine(w.Root, "src", "file.txt"), "needle");
        await File.WriteAllTextAsync(Path.Combine(w.Root, "src", "nested", "file.txt"), "needle");
        Assert.Equal(1, Data(await w.Tools.SearchFilesAsync("needle", file_pattern: "src/*.txt")).GetProperty("matches").GetArrayLength());
        Assert.Equal(2, Data(await w.Tools.SearchFilesAsync("needle", file_pattern: "src/**/*.txt")).GetProperty("matches").GetArrayLength());
        Assert.DoesNotContain("ignored.txt", (await w.Tools.SearchFilesAsync("needle")).ToJson());
    }

    [Fact]
    public async Task SearchOutputBudgetPagesMatchesAndReportsClippedLinesSeparately()
    {
        using var w = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(w.Root, "file.txt"), string.Join('\n', Enumerable.Range(0, 40).Select(i => $"needle-{i:D2} " + new string('中', 1200))));
        var seen = new List<int>(); var offset = 0; var pages = 0;
        while (true)
        {
            var result = await w.Tools.SearchFilesAsync("needle", context_lines: 0, limit: 100, offset: offset); var data = Data(result);
            Assert.False(data.GetProperty("scanTruncated").GetBoolean()); Assert.True(data.GetProperty("linesTruncated").GetBoolean());
            var matches = data.GetProperty("matches").EnumerateArray().ToArray(); seen.AddRange(matches.Select(m => m.GetProperty("line").GetInt32()));
            var next = data.GetProperty("nextOffset").GetInt32(); Assert.True(next > offset); offset = next;
            Assert.True(++pages < 41);
            if (!data.GetProperty("outputTruncated").GetBoolean() && !data.GetProperty("matchLimitReached").GetBoolean()) break;
        }
        Assert.Equal(Enumerable.Range(1, 40), seen);
    }

    [Fact]
    public async Task RegexAndLiteralPatternsArePassedAsDataIncludingLeadingDash()
    {
        using var w = new TestWorkspace(); await File.WriteAllTextAsync(Path.Combine(w.Root, "file.txt"), "--needle\na.b\naxb\n");
        Assert.Single(Data(await w.Tools.SearchFilesAsync("--needle")).GetProperty("matches").EnumerateArray());
        Assert.Single(Data(await w.Tools.SearchFilesAsync("a.b")).GetProperty("matches").EnumerateArray());
        Assert.Equal(2, Data(await w.Tools.SearchFilesAsync("a.b", regex: true)).GetProperty("matches").GetArrayLength());
        Assert.False((await w.Tools.SearchFilesAsync("[", regex: true)).Ok);
    }

    [Theory]
    [InlineData("tail", 30, false)]
    [InlineData("tail-wait", 10, true)] // Allow the SDK worker to start under full-suite load, then time out its five-minute wait.
    public async Task ShellKeepsFinalErrorAndFullLogIncludingOnTimeout(string command, int timeout, bool timedOut)
    {
        using var w = new TestWorkspace();
        var result = await w.Tools.RunShellAsync("probe", [command], timeout_seconds: timeout);
        Assert.False(result.Ok); Assert.True(result.Truncated); var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(timedOut, data.GetProperty("timedOut").GetBoolean());
        var output = data.GetProperty("stdout").GetString()!; Assert.Contains("FINAL-ERROR-MARKER", output); Assert.DoesNotContain("START-MARKER", output);
        Assert.DoesNotContain('\ufffd', output); Assert.True(Encoding.UTF8.GetByteCount(output) <= 20000);
        var log = data.GetProperty("stdoutPath").GetString()!; var full = await File.ReadAllTextAsync(log);
        Assert.Contains("START-MARKER", full); Assert.Contains("FINAL-ERROR-MARKER", full);
        Assert.Equal(3002, full.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("START-MARKER", Data(await w.Tools.ReadAsync(log, limit: 2)).GetProperty("content").GetString());
    }

    [Fact]
    public async Task ShellDoesNotCreateLogsForShortOutputAndCancellationStopsExecution()
    {
        using var w = new TestWorkspace();
        var data = Data(await w.Tools.RunShellAsync("probe", ["short"])); Assert.Equal(JsonValueKind.Null, data.GetProperty("stdoutPath").ValueKind);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.Tools.RunShellAsync("probe", ["wait"], ct: cancel.Token));
    }

    [Fact]
    public async Task UnicodeEscapingCannotReplaceStructuredResultWithGenericSummary()
    {
        using var w = new TestWorkspace(); var registry = new ToolRegistry((_, _) => ValueTask.FromResult(true)); registry.Register(w.Tools);
        var result = await registry.CallAsync("run_shell", JsonSerializer.SerializeToElement(new { exe = "probe", args = new[] { "emoji-noisy" } }), default);
        var data = Data(result); Assert.True(result.Truncated); Assert.True(result.ToJson().Length < 64000);
        Assert.Contains("STDOUT-END", data.GetProperty("stdout").GetString()); Assert.Contains("STDERR-END", data.GetProperty("stderr").GetString());
        Assert.True(File.Exists(data.GetProperty("stdoutPath").GetString())); Assert.True(File.Exists(data.GetProperty("stderrPath").GetString()));
        await File.WriteAllTextAsync(Path.Combine(w.Root, "emoji.txt"), string.Concat(Enumerable.Repeat("😀", 12000)));
        result = await registry.CallAsync("file_read", JsonSerializer.SerializeToElement(new { path = "emoji.txt" }), default);
        Assert.NotEmpty(Data(result).GetProperty("content").GetString()!); Assert.True(result.ToJson().Length < 64000);
    }

    [Fact]
    public async Task DirectoryPagesDoNotLoseEntries()
    {
        using var w = new TestWorkspace(); foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) await File.WriteAllTextAsync(Path.Combine(w.Root, name), "");
        var list = Data(await w.Tools.ListAsync(limit: 1)); var rest = await w.Tools.ListAsync(offset: list.GetProperty("nextOffset").GetInt32(), limit: 2);
        Assert.Equal(2, Data(rest).GetProperty("entries").GetArrayLength()); Assert.False(rest.Truncated);
    }
}
