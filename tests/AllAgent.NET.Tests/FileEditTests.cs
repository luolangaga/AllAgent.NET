using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class FileEditTests
{
    [Fact]
    public async Task BatchUsesOriginalSnapshotAndReturnsOneDiff()
    {
        using var w = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(w.Root, "file.txt"), "first\nkeep\nsecond\n");
        var registry = new ToolRegistry((_, _) => ValueTask.FromResult(true)); registry.Register(w.Tools);
        var result = await registry.CallAsync("file_edit", JsonSerializer.SerializeToElement(new { path = "file.txt", edits = new[] { new FileEdit("first", "second"), new FileEdit("second", "third") } }), default);
        Assert.True(result.Ok, result.ToJson());
        Assert.Equal("second\nkeep\nthird\n", await File.ReadAllTextAsync(Path.Combine(w.Root, "file.txt")));
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(2, data.GetProperty("replacements").GetInt32());
        Assert.Equal(2, data.GetProperty("diff").GetProperty("added").GetInt32());
    }

    [Theory]
    [InlineData("first\nsecond\n", "first\nsecond", "changed\nnext", false)]
    [InlineData("first\r\nsecond\r\n", "first\nsecond", "changed\nnext", false)]
    [InlineData("first  \r\nsecond\t\r\n", "first\nsecond", "changed\nnext", true)]
    [InlineData("say “hello”—yes\n", "say \"hello\"-yes", "changed", true)]
    [InlineData("ＡＢＣ\n", "ABC", "changed", true)]
    public async Task MatchingRecoversFormattingAndPreservesBomAndUntouchedLines(string block, string search, string replacement, bool fuzzy)
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "file.txt");
        var ending = block.Contains("\r\n") ? "\r\n" : "\n";
        var header = "untouched “quote”  " + ending; var footer = "untouched — tail\t" + ending;
        await File.WriteAllTextAsync(target, header + block + footer, new UTF8Encoding(true));
        var result = await w.Tools.EditAsync("file.txt", [new(search, replacement)]);
        Assert.True(result.Ok, result.ToJson());
        Assert.Equal(fuzzy, JsonSerializer.SerializeToElement(result.Data).GetProperty("fuzzyMatch").GetBoolean());
        Assert.Equal(header + replacement.Replace("\n", ending) + ending + footer, await File.ReadAllTextAsync(target));
        Assert.True((await File.ReadAllBytesAsync(target)).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }));
    }

    [Fact]
    public async Task LargeLocalEditDoesNotUseWriteRequestLimit()
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "large.txt");
        var prefix = string.Concat(Enumerable.Repeat("保持这行\n", 24000));
        await File.WriteAllTextAsync(target, prefix + "old\n");
        var result = await w.Tools.EditAsync("large.txt", [new("old", "new")]);
        Assert.True(result.Ok, result.ToJson()); Assert.Equal(prefix + "new\n", await File.ReadAllTextAsync(target));
        Assert.True((await w.Tools.ReadAsync("large.txt")).Truncated);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("same")]
    public async Task AnyFailedEditLeavesEntireBatchUnchanged(string search)
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "file.txt");
        var content = "unique\nsame\nsame\n"; await File.WriteAllTextAsync(target, content);
        var result = await w.Tools.EditAsync("file.txt", [new("unique", "changed"), new(search, "replacement")]);
        Assert.False(result.Ok); Assert.Contains("edits[1]", result.Error);
        Assert.Contains(search == "same" ? "found 2" : "found 0", result.Error);
        Assert.Equal(content, await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task RejectsOverlappingMatchesAndOverlappingEdits()
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "file.txt");
        await File.WriteAllTextAsync(target, "aaa\nunique block\n");
        var result = await w.Tools.EditAsync("file.txt", [new("aa", "b")]);
        Assert.False(result.Ok); Assert.Contains("found 2", result.Error);
        result = await w.Tools.EditAsync("file.txt", [new("unique block", "x"), new("block", "y")]);
        Assert.False(result.Ok); Assert.Contains("overlap", result.Error);
        Assert.Equal("aaa\nunique block\n", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task FuzzyEditsOnSameLineAndDifferentLinesPreserveUntouchedText()
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "file.txt");
        await File.WriteAllTextAsync(target, "keep “q”  \nfirst “one” and second “two”  \nkeep —  \nthird “three”\n");
        var result = await w.Tools.EditAsync("file.txt", [new("first \"one\"", "1"), new("second \"two\"", "2"), new("third \"three\"", "3")]);
        Assert.True(result.Ok, result.ToJson());
        Assert.Equal("keep “q”  \n1 and 2\nkeep —  \n3\n", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task RejectsNoopAndEmptyButAcceptsLargeResult()
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "file.txt");
        await File.WriteAllTextAsync(target, "old");
        Assert.False((await w.Tools.EditAsync("file.txt", [])).Ok);
        Assert.False((await w.Tools.EditAsync("file.txt", [new("", "new")])).Ok);
        Assert.False((await w.Tools.EditAsync("file.txt", [new("old", "old")])).Ok);
        Assert.True((await w.Tools.EditAsync("file.txt", [new("old", new string('中', 340000))])).Ok);
        Assert.Equal(new string('中', 340000), await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(w.Root, "*.tmp"));
    }

    [Fact]
    public async Task FuzzyNoopDoesNotNormalizeFileAndExactHitCannotHideNormalizedDuplicates()
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "file.txt");
        await File.WriteAllTextAsync(target, "“quote”  \n");
        var result = await w.Tools.EditAsync("file.txt", [new("\"quote\"", "\"quote\"")]);
        Assert.False(result.Ok); Assert.Contains("No changes", result.Error);
        Assert.Equal("“quote”  \n", await File.ReadAllTextAsync(target));
        await File.WriteAllTextAsync(target, "\"quote\"\n“quote”\n");
        result = await w.Tools.EditAsync("file.txt", [new("\"quote\"", "new")]);
        Assert.False(result.Ok); Assert.Contains("found 2", result.Error);
        Assert.Equal("\"quote\"\n“quote”\n", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task BatchEditsCanBeUndoneAndRedoneWithOriginalBytes()
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "file.txt");
        await File.WriteAllTextAsync(target, "one\r\nkeep\r\ntwo\r\n", new UTF8Encoding(true));
        var original = await File.ReadAllBytesAsync(target);
        var checkpoints = new AllAgent.NET.Core.Services.WorkspaceCheckpoints(new(w.Root), Path.Combine(w.Root, "data"));
        var session = Guid.NewGuid(); var turn = checkpoints.Begin(session, "edit");
        var tools = new SystemTools(new(w.Root), new(w.Python, w.Pwsh, []), new Dictionary<string, string>(), checkpoints);
        var result = await tools.EditAsync("file.txt", [new("one", "1"), new("two", "2")]);
        Assert.True(result.Ok, result.ToJson()); checkpoints.End();
        var changed = await File.ReadAllBytesAsync(target);
        checkpoints.Restore(session, turn.Id, false); Assert.Equal(original, await File.ReadAllBytesAsync(target));
        checkpoints.Restore(session, turn.Id, true); Assert.Equal(changed, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task CancelledAndDeniedBatchDoesNotWrite()
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "file.txt");
        await File.WriteAllTextAsync(target, "old");
        var registry = new ToolRegistry(); registry.Register(w.Tools);
        var denied = await registry.CallAsync("file_edit", JsonSerializer.SerializeToElement(new { path = "file.txt", edits = new[] { new FileEdit("old", "new") } }), default);
        Assert.False(denied.Ok);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.Tools.EditAsync("file.txt", [new("old", "new")], cancel.Token));
        Assert.Equal("old", await File.ReadAllTextAsync(target)); Assert.Empty(Directory.GetFiles(w.Root, "*.tmp"));
    }

    [Fact]
    public async Task DeletionWorksAndUnsupportedEncodingIsNotRewritten()
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "file.txt");
        await File.WriteAllTextAsync(target, "first\nremove\nlast\n");
        Assert.True((await w.Tools.EditAsync("file.txt", [new("remove\n", "")])).Ok);
        Assert.Equal("first\nlast\n", await File.ReadAllTextAsync(target));
        await File.WriteAllTextAsync(target, "first\nlast\n", Encoding.Unicode);
        var original = await File.ReadAllBytesAsync(target);
        Assert.False((await w.Tools.EditAsync("file.txt", [new("first", "new")])).Ok);
        Assert.Equal(original, await File.ReadAllBytesAsync(target));
    }

    [Theory]
    [InlineData("{\"path\":\"file.txt\",\"old_string\":\"old\",\"new_string\":\"new\"}")]
    [InlineData("{\"path\":\"file.txt\",\"edits\":[{\"old_string\":\"old\"}]}")]
    [InlineData("{\"path\":\"file.txt\",\"edits\":[{\"old_string\":1,\"new_string\":\"new\"}]}")]
    [InlineData("{\"path\":\"file.txt\",\"edits\":[{\"old_string\":\"old\",\"new_string\":\"new\",\"extra\":true}]}")]
    public async Task InvalidOrLegacyArgumentsFailBeforeApproval(string json)
    {
        using var w = new TestWorkspace(); var approvals = 0;
        var registry = new ToolRegistry((_, _) => { approvals++; return ValueTask.FromResult(true); }); registry.Register(w.Tools);
        var result = await registry.CallAsync("file_edit", JsonSerializer.Deserialize<JsonElement>(json), default);
        Assert.False(result.Ok); Assert.Equal(0, approvals);
    }

    [Fact]
    public async Task ConcurrentToolSetsDoNotLoseSeparateChanges()
    {
        using var w = new TestWorkspace(); var target = Path.Combine(w.Root, "file.txt");
        await File.WriteAllTextAsync(target, string.Join('\n', Enumerable.Range(0, 32).Select(i => $"entry-{i:D2}!")));
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => w.Tools.EditAsync("file.txt", [new($"entry-{i:D2}!", $"updated-{i:D2}!")])));
        Assert.All(results, r => Assert.True(r.Ok, r.ToJson()));
        var content = await File.ReadAllTextAsync(target);
        Assert.DoesNotContain("entry-", content); Assert.Equal(32, content.Split('\n').Length);
    }

    [Fact]
    public void ApprovalPreviewShowsEveryReplacement()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "file.txt", edits = new[] { new FileEdit("first", "1"), new FileEdit("second", "2") } });
        var preview = ApprovalPreview.Create(new("file_edit", ToolRisk.Write, args), Path.GetTempPath());
        Assert.Contains(preview.Lines, l => l.Text == "- first"); Assert.Contains(preview.Lines, l => l.Text == "+ 2");
    }
}
