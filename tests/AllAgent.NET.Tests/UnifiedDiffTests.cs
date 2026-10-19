using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class UnifiedDiffTests
{
    [Fact]
    public void ParsesHunksIntoNumberedRows()
    {
        var patch = string.Join("\n",
            "diff --git a/src/app.ts b/src/app.ts",
            "index 1111111..2222222 100644",
            "--- a/src/app.ts",
            "+++ b/src/app.ts",
            "@@ -1,4 +1,5 @@",
            " const a = 1",
            "-const b = 2",
            "+const b = 3",
            "+const c = 4",
            " const d = 5");
        var parsed = UnifiedDiff.Parse(patch);

        Assert.False(parsed.Truncated);
        Assert.False(parsed.Binary);
        Assert.Equal(2, parsed.Added);
        Assert.Equal(1, parsed.Removed);
        Assert.Equal(5, parsed.Rows.Count);

        Assert.Equal("context", parsed.Rows[0].Kind);
        Assert.Equal(1, parsed.Rows[0].OldLine);
        Assert.Equal(1, parsed.Rows[0].NewLine);
        Assert.Equal("const a = 1", parsed.Rows[0].Text);

        Assert.Equal("removed", parsed.Rows[1].Kind);
        Assert.Equal(2, parsed.Rows[1].OldLine);
        Assert.Null(parsed.Rows[1].NewLine);
        Assert.Equal("const b = 2", parsed.Rows[1].Text);

        Assert.Equal("added", parsed.Rows[2].Kind);
        Assert.Null(parsed.Rows[2].OldLine);
        Assert.Equal(2, parsed.Rows[2].NewLine);

        Assert.Equal("context", parsed.Rows[4].Kind);
        Assert.Equal(3, parsed.Rows[4].OldLine);
        Assert.Equal(4, parsed.Rows[4].NewLine);
    }

    [Fact]
    public void NumbersMultipleHunksIndependently()
    {
        var patch = string.Join("\n",
            "@@ -10,2 +10,2 @@",
            " same",
            "-old",
            "+new",
            "@@ -40,1 +40,2 @@",
            " keep",
            "+extra");
        var parsed = UnifiedDiff.Parse(patch);

        Assert.Equal(5, parsed.Rows.Count);
        Assert.Equal(10, parsed.Rows[0].OldLine);
        Assert.Equal(11, parsed.Rows[1].OldLine);
        Assert.Null(parsed.Rows[1].NewLine);
        Assert.Equal(11, parsed.Rows[2].NewLine);
        Assert.Equal(40, parsed.Rows[3].OldLine);
        Assert.Equal(41, parsed.Rows[4].NewLine);
        Assert.Equal(2, parsed.Added);
        Assert.Equal(1, parsed.Removed);
    }

    [Fact]
    public void StopsAtTheNextFileHeaderAndSkipsNoNewlineMarkers()
    {
        var patch = string.Join("\n",
            "@@ -1,2 +1,2 @@",
            "-before",
            "+after",
            "\\ No newline at end of file",
            "diff --git a/next b/next",
            "index 1111111..2222222 100644",
            "--- a/next",
            "+++ b/next",
            "new file mode 100644");
        var parsed = UnifiedDiff.Parse(patch);

        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal(1, parsed.Added);
        Assert.Equal(1, parsed.Removed);
    }

    [Fact]
    public void DetectsBinaryPatches()
    {
        var parsed = UnifiedDiff.Parse(string.Join("\n",
            "diff --git a/logo.png b/logo.png",
            "index 1111111..2222222 100644",
            "Binary files a/logo.png and b/logo.png differ"));
        Assert.True(parsed.Binary);
        Assert.Empty(parsed.Rows);
    }

    [Fact]
    public void TruncatesHugePatches()
    {
        var lines = new List<string> { "@@ -0,0 +1,5000 @@" };
        for (var index = 0; index < 5000; index++) lines.Add($"+line {index}");
        var parsed = UnifiedDiff.Parse(string.Join("\n", lines));

        Assert.True(parsed.Truncated);
        Assert.Equal(UnifiedDiff.MaxRows, parsed.Rows.Count);
        Assert.Equal(UnifiedDiff.MaxRows, parsed.Added);
    }

    [Fact]
    public void ParseAddedProducesAllAddedRows()
    {
        var parsed = UnifiedDiff.ParseAdded("first\nsecond\n");
        Assert.Equal(2, parsed.Rows.Count);
        Assert.All(parsed.Rows, row => Assert.Equal("added", row.Kind));
        Assert.Equal(2, parsed.Added);
        Assert.Equal(0, parsed.Removed);
        Assert.Equal(1, parsed.Rows[0].NewLine);
        Assert.Equal(2, parsed.Rows[1].NewLine);
        Assert.Equal("first", parsed.Rows[0].Text);

        var noTrailingNewline = UnifiedDiff.ParseAdded("only");
        Assert.Single(noTrailingNewline.Rows);
        Assert.Equal("only", noTrailingNewline.Rows[0].Text);

        Assert.Empty(UnifiedDiff.ParseAdded(string.Empty).Rows);
    }
}
