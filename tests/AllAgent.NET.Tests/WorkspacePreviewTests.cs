using System.Text;
using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>The read-only workspace browser: one directory level per call plus content classification.</summary>
public sealed class WorkspacePreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allagent-preview-" + Guid.NewGuid().ToString("N"));
    private readonly BridgeDispatcher _dispatcher = new();

    public WorkspacePreviewTests()
    {
        Directory.CreateDirectory(_root);
        new WorkspacePreviewBridge(_root).Register(_dispatcher);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort cleanup */ }
    }

    private async Task<JsonElement> InvokeAsync(string method, object? parameters = null)
    {
        var json = await _dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString("N"), method, @params = parameters }));
        using var document = JsonDocument.Parse(json);
        var error = document.RootElement.GetProperty("error");
        if (error.ValueKind == JsonValueKind.String) throw new InvalidOperationException(error.GetString());
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task<string> InvokeExpectingErrorAsync(string method, object? parameters = null)
    {
        var json = await _dispatcher.InvokeAsync(JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString("N"), method, @params = parameters }));
        using var document = JsonDocument.Parse(json);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(JsonValueKind.String, error.ValueKind);
        return error.GetString()!;
    }

    [Fact]
    public async Task TreeListsOneLevelSortedWithDirectoriesFirst()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, ".hidden"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "app.ts"), "export {}");
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.md"), "# readme");
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitignore"), "bin/");

        var tree = await InvokeAsync("workspace.tree", new { path = "." });
        var entries = tree.GetProperty("entries").EnumerateArray().ToArray();
        Assert.False(tree.GetProperty("truncated").GetBoolean());
        Assert.Equal(4, entries.Length);
        // Directories first (ordinal-ignore-case), then files; the nested file is not listed here.
        Assert.Equal(new[] { ".hidden", "src", ".gitignore", "readme.md" }, entries.Select(entry => entry.GetProperty("name").GetString()).ToArray());
        Assert.True(entries[0].GetProperty("hidden").GetBoolean());
        Assert.True(entries[0].GetProperty("directory").GetBoolean());
        Assert.False(entries[3].GetProperty("directory").GetBoolean());
        Assert.Equal("src/app.ts", (await InvokeAsync("workspace.tree", new { path = "src" })).GetProperty("entries").EnumerateArray().Single().GetProperty("path").GetString());
    }

    [Fact]
    public async Task TreeAndReadRejectPathsOutsideTheWorkspace()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret");
        try
        {
            Assert.Contains("workspace", await InvokeExpectingErrorAsync("workspace.tree", new { path = Path.GetRelativePath(_root, outside) }));
            Assert.Contains("workspace", await InvokeExpectingErrorAsync("workspace.readFile", new { path = Path.Combine(outside, "secret.txt") }));
            Assert.Contains("路径为空", await InvokeExpectingErrorAsync("workspace.readFile", new { path = "  " }));
            Assert.Contains("文件不存在", await InvokeExpectingErrorAsync("workspace.readFile", new { path = "missing.txt" }));
            Assert.Contains("目录", await InvokeExpectingErrorAsync("workspace.readFile", new { path = "." }));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task ReadFileClassifiesTextImagesAndBinary()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "note.txt"), "你好 world\n");
        await File.WriteAllBytesAsync(Path.Combine(_root, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "raw.dat"), [0x01, 0x00, 0x02, 0x00]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "garbled.bin"), [0xFF, 0xFE, 0xFD]);

        var text = await InvokeAsync("workspace.readFile", new { path = "note.txt" });
        Assert.Equal("text", text.GetProperty("kind").GetString());
        Assert.Equal("你好 world\n", text.GetProperty("text").GetString());
        Assert.False(text.GetProperty("truncated").GetBoolean());

        var image = await InvokeAsync("workspace.readFile", new { path = "logo.png" });
        Assert.Equal("image", image.GetProperty("kind").GetString());
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3], Convert.FromBase64String(image.GetProperty("base64").GetString()!));

        Assert.Equal("binary", (await InvokeAsync("workspace.readFile", new { path = "raw.dat" })).GetProperty("kind").GetString());
        Assert.Equal("binary", (await InvokeAsync("workspace.readFile", new { path = "garbled.bin" })).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ReadFileTruncatesLargeTextAtTheByteBudget()
    {
        var line = new string('x', 1023) + "\n";
        var content = new StringBuilder();
        while (content.Length < 300 * 1024) content.Append(line);
        await File.WriteAllTextAsync(Path.Combine(_root, "big.log"), content.ToString());

        var result = await InvokeAsync("workspace.readFile", new { path = "big.log" });
        Assert.Equal("text", result.GetProperty("kind").GetString());
        Assert.True(result.GetProperty("truncated").GetBoolean());
        var text = result.GetProperty("text").GetString()!;
        Assert.True(text.Length > 200_000);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 256 * 1024);
    }
}
