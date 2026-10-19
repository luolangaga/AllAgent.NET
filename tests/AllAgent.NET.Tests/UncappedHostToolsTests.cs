using System.Text.Json;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class UncappedHostToolsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(900)]
    [InlineData(100000)]
    [InlineData(int.MaxValue)]
    public async Task ShellAcceptsLongOrInfiniteTimeout(int seconds)
    {
        using var w = new TestWorkspace();
        var result = await w.Tools.RunShellAsync("probe", ["quick"], timeout_seconds: seconds);
        Assert.True(result.Ok, result.ToJson());
        var schema = w.Tools.Definitions.Single(d => d.Name == "run_shell").Parameters;
        Assert.DoesNotContain(schema.GetProperty("required").EnumerateArray(), p => p.GetString() == "timeout_seconds");
    }

    [Fact]
    public async Task LargeSearchRequestsReturnPagesInsteadOfArgumentFailures()
    {
        using var w = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(w.Root, "file.txt"), string.Join('\n', Enumerable.Range(0, 12000).Select(i => $"needle {i}")));
        var result = await w.Tools.SearchFilesAsync("needle", context_lines: int.MaxValue, limit: int.MaxValue);
        Assert.True(result.Ok, result.ToJson());
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.NotEmpty(data.GetProperty("matches").EnumerateArray()); Assert.True(data.GetProperty("contextTruncated").GetBoolean());
        result = await w.Tools.SearchFilesAsync("needle", offset: 11000, limit: 500, context_lines: 0);
        Assert.True(result.Ok, result.ToJson()); data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(11001, data.GetProperty("matches")[0].GetProperty("line").GetInt32());
        Assert.True((await w.Tools.ListAsync(limit: int.MaxValue, offset: 11000)).Ok);
        Assert.True((await w.Tools.ReadAsync("file.txt", limit: int.MaxValue)).Ok);
    }

    [Fact]
    public async Task LargeTextFilesCanBeWrittenReadSearchedEditedAndRestored()
    {
        using var w = new TestWorkspace();
        var content = string.Concat(Enumerable.Repeat("unchanged source line\n", 60000)) + "unique old marker\n";
        var checkpoints = new WorkspaceCheckpoints(new(w.Root), Path.Combine(w.Root, "data"));
        var tools = new SystemTools(new(w.Root), new(w.Python, w.Pwsh, []), new Dictionary<string, string>(), checkpoints);
        Assert.True((await tools.WriteAsync("large.txt", content)).Ok);
        var search = await tools.SearchFilesAsync("unique old marker", "large.txt"); Assert.True(search.Ok, search.ToJson());
        Assert.Single(JsonSerializer.SerializeToElement(search.Data).GetProperty("matches").EnumerateArray());
        var session = Guid.NewGuid(); var turn = checkpoints.Begin(session, "edit large file");
        var edit = await tools.EditAsync("large.txt", [new("unique old marker", "unique new marker")]); Assert.True(edit.Ok, edit.ToJson());
        checkpoints.End(session);
        Assert.Contains("unique new marker", JsonSerializer.SerializeToElement((await tools.ReadAsync("large.txt", offset: 60001)).Data).GetProperty("content").GetString());
        checkpoints.Restore(session, turn.Id, false); Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(w.Root, "large.txt")));
        checkpoints.Restore(session, turn.Id, true); Assert.EndsWith("unique new marker\n", await File.ReadAllTextAsync(Path.Combine(w.Root, "large.txt")));
    }

    [Fact]
    public async Task BatchLargerThan128IsAccepted()
    {
        using var w = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(w.Root, "batch.txt"), string.Join('\n', Enumerable.Range(0, 150).Select(i => $"old-{i:D3}!")));
        var edits = Enumerable.Range(0, 150).Select(i => new FileEdit($"old-{i:D3}!", $"new-{i:D3}!")).ToArray();
        var result = await w.Tools.EditAsync("batch.txt", edits); Assert.True(result.Ok, result.ToJson());
        Assert.DoesNotContain("old-", await File.ReadAllTextAsync(Path.Combine(w.Root, "batch.txt")));
    }

    [Fact]
    public async Task CheckpointCanTrackMoreThan128FilesAndUndoEveryWrite()
    {
        using var w = new TestWorkspace();
        var checkpoints = new WorkspaceCheckpoints(new(w.Root), Path.Combine(w.Root, "data"));
        var tools = new SystemTools(new(w.Root), new(w.Python, w.Pwsh, []), new Dictionary<string, string>(), checkpoints);
        var session = Guid.NewGuid(); var turn = checkpoints.Begin(session, "many files");
        for (var i = 0; i < 130; i++) Assert.True((await tools.WriteAsync($"file-{i}.txt", "new")).Ok);
        checkpoints.End(session); Assert.Equal(130, Assert.Single(checkpoints.List(session)).Files.Count);
        checkpoints.Restore(session, turn.Id, false); Assert.Empty(Directory.GetFiles(w.Root, "file-*.txt"));
        checkpoints.Restore(session, turn.Id, true); Assert.Equal(130, Directory.GetFiles(w.Root, "file-*.txt").Length);
    }

    [Fact]
    public async Task ShellAcceptsMoreAndLongerLiteralArguments()
    {
        using var w = new TestWorkspace();
        var args = Enumerable.Repeat("literal", 110).Append(new string('x', 8001)).ToArray();
        var result = await w.Tools.RunShellAsync("probe", args); Assert.True(result.Ok, result.ToJson());
        var output = JsonSerializer.SerializeToElement(result.Data).GetProperty("stdout").GetString();
        Assert.Equal(args, JsonSerializer.Deserialize<string[]>(output!));
    }

    [Fact]
    public async Task ReadFileBeyondFormer20MbLimit()
    {
        using var w = new TestWorkspace(); var path = Path.Combine(w.Root, "huge.txt");
        await using (var writer = new StreamWriter(path))
        {
            var line = new string('a', 1000);
            for (var i = 0; i < 21000; i++) await writer.WriteLineAsync(line);
            await writer.WriteLineAsync("TAIL-EVIDENCE");
        }
        var result = await w.Tools.ReadAsync("huge.txt", offset: 21001, limit: int.MaxValue);
        Assert.True(result.Ok, result.ToJson()); Assert.Equal("TAIL-EVIDENCE", JsonSerializer.SerializeToElement(result.Data).GetProperty("content").GetString());
    }
}
