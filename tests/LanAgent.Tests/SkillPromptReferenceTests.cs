using LanAgent.Core.Data;
using LanAgent.Core.Services;
using LanAgent.Terminal;
using Xunit;

namespace LanAgent.Tests;

public sealed class SkillPromptReferenceTests
{
    [Theory]
    [InlineData("codex", "Run npm install -g @openai/codex` and example @missing.png")]
    [InlineData("design-html", "Install @chenglou/pretext\" and example @missing.txt")]
    public async Task SkillInstructionsAreNotScannedForUserFileReferences(string name, string instructions)
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var catalog = new SkillCatalog(store, w.Root, Path.Combine(w.Root, "data"), w.Root);
        catalog.Create(name, "fixture", instructions);
        var prompt = await PromptReferences.ExpandSkillAsync(catalog, name, "", w.Root);
        Assert.Contains(instructions, prompt.Text); Assert.Empty(prompt.Images);
    }

    [Fact]
    public async Task SkillArgumentsStillAttachFilesAndImagesWithoutRescanningTheirContents()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var catalog = new SkillCatalog(store, w.Root, Path.Combine(w.Root, "data"), w.Root);
        catalog.Create("fixture", "fixture", "Use @openai/codex. Task: $ARGUMENTS");
        await File.WriteAllTextAsync(Path.Combine(w.Root, "some file.txt"), "FILE-PROOF @another-missing.txt");
        await File.WriteAllBytesAsync(Path.Combine(w.Root, "pixel.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC"));
        var prompt = await PromptReferences.ExpandSkillAsync(catalog, "fixture", "Review @\"some file.txt\" @pixel.png", w.Root);
        Assert.Contains("FILE-PROOF @another-missing.txt", prompt.Text); Assert.Contains("@openai/codex", prompt.Text);
        Assert.Single(prompt.Images);
        await Assert.ThrowsAsync<FileNotFoundException>(() => PromptReferences.ExpandSkillAsync(catalog, "fixture", "@missing.txt", w.Root));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PromptReferences.ExpandSkillAsync(catalog, "fixture", "@../outside.txt", w.Root));
    }
}
