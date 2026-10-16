using LanAgent.Core.Data;
using LanAgent.Core.Services;
using Xunit;

namespace LanAgent.Tests;

public sealed class BundledSkillConcurrencyTests
{
    [Fact]
    public async Task SimultaneousHostsSeedCompleteSkillsWithoutOverwritingEdits()
    {
        using var workspace = new TestWorkspace();
        var data = Path.Combine(workspace.Root, "data");
        using var store = new DataStore(data);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            new SkillCatalog(store, workspace.Root, data, workspace.Root).SeedBundled();
        })).ToArray();
        start.SetResult(); await Task.WhenAll(writers);
        var catalog = new SkillCatalog(store, workspace.Root, data, workspace.Root);
        var skills = catalog.List().Where(skill => skill.Scope == "user").ToArray();
        Assert.Equal(6, skills.Length); Assert.Empty(catalog.Diagnostics);
        Assert.All(skills, skill => Assert.False(string.IsNullOrWhiteSpace(skill.Instructions)));
        Assert.Empty(Directory.GetFiles(catalog.UserDirectory, "*.tmp", SearchOption.AllDirectories));
        var path = catalog.Get("computer-use").Path;
        File.AppendAllText(path, "\nUSER-CUSTOMIZATION");
        catalog.SeedBundled(); Assert.EndsWith("USER-CUSTOMIZATION", File.ReadAllText(path));
    }
}
