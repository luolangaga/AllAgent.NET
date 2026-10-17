using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class PastedImageTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC";
    [Fact]
    public async Task VtShiftInsertReadsClipboardAsPaste()
    {
        var keys = new Queue<ConsoleKeyInfo>("\u001b[2;2~".Select(c => new ConsoleKeyInfo(c, (ConsoleKey)0, false, false, false)));
        var reader = new TerminalKeyReader(() => keys.Count > 0, () => keys.Dequeue(), () => "clipboard\r\ntext");
        Assert.Equal("clipboard\ntext", (await reader.ReadAsync(default)).Paste);
    }
    [Theory]
    [InlineData("\u001b[A", ConsoleKey.UpArrow, false, false)]
    [InlineData("\u001b[1;5D", ConsoleKey.LeftArrow, false, true)]
    [InlineData("\u001b[1;2C", ConsoleKey.RightArrow, true, false)]
    [InlineData("\u001bOP", ConsoleKey.F1, false, false)]
    [InlineData("\u001b[24~", ConsoleKey.F12, false, false)]
    [InlineData("\u001b[Z", ConsoleKey.Tab, true, false)]
    [InlineData("\r", ConsoleKey.Enter, false, false)]
    [InlineData("\u001a", ConsoleKey.Z, false, true)]
    public async Task WindowsVtKeysRetainShortcuts(string sequence, ConsoleKey expected, bool shift, bool ctrl)
    {
        var keys = new Queue<ConsoleKeyInfo>(sequence.Select(c => new ConsoleKeyInfo(c, (ConsoleKey)0, false, false, false)));
        var reader = new TerminalKeyReader(() => keys.Count > 0, () => keys.Dequeue());
        var key = (await reader.ReadAsync(default)).Key;
        Assert.Equal(expected, key.Key); Assert.Equal(shift, key.Modifiers.HasFlag(ConsoleModifiers.Shift));
        Assert.Equal(ctrl, key.Modifiers.HasFlag(ConsoleModifiers.Control)); Assert.False(reader.HasInput);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VtPasteDelimitersDoNotLeakIntoText(bool folded)
    {
        var keys = new Queue<ConsoleKeyInfo>();
        foreach (var segment in new[] { "\u001b[200~", "quoted path.png\n/exit", "\u001b[201~" })
        {
            var input = segment;
            if (folded && input.StartsWith("\u001b[")) { keys.Enqueue(new('[', ConsoleKey.Oem4, false, true, false)); input = input[2..]; }
            foreach (var c in input) keys.Enqueue(new(c, (ConsoleKey)0, false, false, false));
        }
        var reader = new TerminalKeyReader(() => keys.Count > 0, () => keys.Dequeue());
        Assert.Equal("quoted path.png\n/exit", (await reader.ReadAsync(default)).Paste); Assert.False(reader.HasInput);
    }
    [Fact]
    public async Task EscapeBackspaceMapsToAttachmentRemovalInsteadOfClearingDraft()
    {
        var keys = new Queue<ConsoleKeyInfo>([new('\u001b', ConsoleKey.Escape, false, false, false), new('\b', ConsoleKey.Backspace, false, false, false)]);
        var reader = new TerminalKeyReader(() => keys.Count > 0, () => keys.Dequeue());
        var input = await reader.ReadAsync(default);
        var state = new InputState(); state.Set("Explain @photo.png"); state.Key(input.Key, [], []);
        Assert.Equal("Explain ", state.Text); Assert.False(reader.HasInput);
    }
    [Theory]
    [InlineData("single")]
    [InlineData("uri")]
    [InlineData("multiple")]
    [InlineData("multiline")]
    public async Task PasteStagesImmutableImageBytesAndSupportsUndo(string format)
    {
        using var workspace = new TestWorkspace();
        var first = Path.Combine(workspace.Root, "first image.png");
        var second = Path.Combine(workspace.Root, "second.png");
        await File.WriteAllBytesAsync(first, Convert.FromBase64String(Png));
        await File.WriteAllBytesAsync(second, Convert.FromBase64String(Png));
        var input = format switch {
            "uri" => new Uri(first).AbsoluteUri,
            "multiple" => $"\"{first}\" \"{second}\"",
            "multiline" => $"\"{first}\"\n\"{second}\"",
            _ => first
        };
        var prepared = await PastedImages.PrepareAsync(input, workspace.Root);
        Assert.NotNull(prepared.Notice);
        var count = format is "multiple" or "multiline" ? 2 : 1;
        Assert.Equal(count, PromptReferences.ImageReferences(prepared.Text).Length);
        await File.WriteAllTextAsync(first, "original changed after paste");
        var expanded = await PromptReferences.ExpandWithImagesAsync(prepared.Text, workspace.Root, allowExplicitExternalImages: true);
        Assert.Equal(count, expanded.Images.Count);
        Assert.All(expanded.Images, image => Assert.Equal(Png, image.Base64));
        var state = new InputState(); state.Set("Explain "); state.InsertPaste(prepared.Text, prepared.Notice);
        Assert.Contains($"图片 {count}", PromptReferences.ImageDraftLabel(state.Text));
        var draft = state.Text;
        Assert.Null(state.Key(new('\b', ConsoleKey.Backspace, false, true, false), [], []));
        Assert.Equal(count - 1, PromptReferences.ImageReferences(state.Text).Length);
        Assert.StartsWith("Explain ", state.Text);
        state.Key(new('\u001a', ConsoleKey.Z, false, false, true), [], []);
        Assert.Equal(draft, state.Text);
        state.Key(new('\u001a', ConsoleKey.Z, false, false, true), [], []);
        Assert.Equal("Explain ", state.Text);
        state.Key(new('\u0019', ConsoleKey.Y, false, false, true), [], []);
        Assert.Equal(draft, state.Text);
    }
    [Fact]
    public async Task MixedTextUrlsAndBrokenImagesKeepOriginalPaste()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "photo.png"); await File.WriteAllBytesAsync(path, Convert.FromBase64String(Png));
        foreach (var input in new[] { "ordinary pasted text", "https://example.com/photo.png", $"Explain \"{path}\"", $"\"{path}\" missing.png" })
        {
            var result = await PastedImages.PrepareAsync(input, workspace.Root);
            Assert.Equal(input, result.Text); Assert.Null(result.Notice);
        }
        var bad = Path.Combine(workspace.Root, "broken.png"); await File.WriteAllTextAsync(bad, "invalid image");
        var combined = $"\"{path}\" \"{bad}\"";
        var failed = await PastedImages.PrepareAsync(combined, workspace.Root);
        Assert.Equal(combined, failed.Text); Assert.NotNull(failed.Notice);
    }
}
