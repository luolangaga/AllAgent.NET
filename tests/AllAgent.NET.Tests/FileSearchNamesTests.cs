using System.Text.Json;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>file_search(name mode) replaces the retired everything_search tool surface.</summary>
public sealed class FileSearchNamesTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private SystemTools Tools => _workspace.Tools;
    private string Root => _workspace.Root;

    public void Dispose() => _workspace.Dispose();

    private static JsonElement Data(ToolResult result)
    {
        Assert.True(result.Ok, result.Error);
        return JsonSerializer.SerializeToElement(result.Data, ToolResult.JsonOptions);
    }

    private static string[] Files(ToolResult result) =>
        Data(result).GetProperty("files").EnumerateArray().Select(item => item.GetString()!).ToArray();

    [Fact]
    public async Task NameModeListsMatchingFilesWithoutIgnoredEntriesOrDirectories()
    {
        Directory.CreateDirectory(Path.Combine(Root, "nested"));
        Directory.CreateDirectory(Path.Combine(Root, "folder.txt"));
        await File.WriteAllTextAsync(Path.Combine(Root, ".gitignore"), "ignored.txt\n");
        foreach (var name in new[] { "alpha.txt", "BETA.TXT", "ignored.txt", "other.log" })
            await File.WriteAllTextAsync(Path.Combine(Root, name), "");
        await File.WriteAllTextAsync(Path.Combine(Root, "nested", "gamma.txt"), "");

        var files = Files(await Tools.SearchFilesAsync("", names_only: true, file_pattern: "*.txt"));
        Assert.Contains("alpha.txt", files);
        Assert.Contains("BETA.TXT", files);
        Assert.Contains("nested/gamma.txt", files);
        Assert.DoesNotContain("ignored.txt", files);
        Assert.DoesNotContain("other.log", files);
        Assert.DoesNotContain(files, path => path.Contains("folder.txt", StringComparison.Ordinal));

        var withIgnored = Files(await Tools.SearchFilesAsync("", names_only: true, file_pattern: "*.txt", include_ignored: true));
        Assert.Contains("ignored.txt", withIgnored);
    }

    [Fact]
    public async Task NameModeFiltersNamesAndPagesResults()
    {
        foreach (var name in new[] { "report-a.txt", "report-b.txt", "notes.txt" })
            await File.WriteAllTextAsync(Path.Combine(Root, name), "");

        var literal = Files(await Tools.SearchFilesAsync("report", names_only: true, file_pattern: "*.txt"));
        Assert.Equal(new[] { "report-a.txt", "report-b.txt" }, literal.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "report-a.txt" },
            Files(await Tools.SearchFilesAsync("^report-a", regex: true, names_only: true, file_pattern: "*.txt")));

        var first = Data(await Tools.SearchFilesAsync("", names_only: true, file_pattern: "*.txt", limit: 2));
        Assert.Equal(2, first.GetProperty("files").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var second = Data(await Tools.SearchFilesAsync("", names_only: true, file_pattern: "*.txt", limit: 2,
            offset: first.GetProperty("nextOffset").GetInt32()));
        Assert.False(second.GetProperty("hasMore").GetBoolean());
        Assert.Single(second.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public async Task ContentModeStillRequiresAPattern()
    {
        var failure = await Tools.SearchFilesAsync("");
        Assert.False(failure.Ok);
        Assert.Contains("names_only", failure.Error);
    }
}
