using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services;
using LanAgent.Core.Services.AI;
using LanAgent.Core.Tools;
using LanAgent.Terminal;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace LanAgent.Tests;

public sealed class TerminalProductTests
{
    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData(".lanagent/AGENT.md")]
    public void ProjectInstructionsLoadOneHundredThousandCharactersWithoutClipping(string relative)
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var catalog = new SkillCatalog(store, workspace.Root, Path.Combine(workspace.Root, "data"), workspace.Root);
        var path = Path.Combine(workspace.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = new string('中', 99996) + "TAIL";
        File.WriteAllText(path, content);
        Assert.Contains("Project instructions from " + relative + ":\n" + content, TerminalApp.Instructions(workspace.Root, catalog, false));
        File.AppendAllText(path, "x");
        Assert.Contains("100000", Assert.Throws<ArgumentException>(() => TerminalApp.Instructions(workspace.Root, catalog, false)).Message);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char character = '\0', bool control = false) => new(character, key, false, false, control);
    [Fact]
    public void SlashMenuStartsSmallAndExpandsWhenTypingAnAdvancedPrefix()
    {
        var input = new InputState(); input.Set("/");
        var hints = input.Suggestions(CommandCatalog.Builtins);
        Assert.InRange(hints.Count, 1, 12);
        Assert.Equal("/help", hints[0].Text);
        Assert.Contains(hints, h => h.Text == "/model");
        Assert.DoesNotContain(hints, h => h.Text == "/agent" || h.Advanced);
        Assert.DoesNotContain(CommandCatalog.Builtins, h => h.Text == "/agent");
        input.Set("/sub");
        Assert.Contains(input.Suggestions(CommandCatalog.Builtins), h => h.Text == "/subagents");
        var args = TerminalArguments.Parse(["--subagent-model", "child-model", "--subagent-provider", "child-provider"]);
        Assert.Equal("chat", args.Command);
        Assert.Equal("child-model", args.Get("subagent-model"));
        Assert.Equal("child-provider", args.Get("subagent-provider"));
    }
    [Fact]
    public void ExplicitChildProviderUsesItsOwnModelInsteadOfSavedDefaults()
    {
        var current = new ApiConfig { DisplayName = "parent", DefaultModel = "parent-model" };
        var saved = new ApiConfig { DisplayName = "saved-child", DefaultModel = "saved-provider-model" };
        var chosen = new ApiConfig { DisplayName = "chosen-child", DefaultModel = "chosen-provider-model" };
        var configurations = new[] { current, saved, chosen };
        var inherited = TerminalApp.ResolveSubAgentConfig(current, configurations, null, null, null, null);
        Assert.Equal(current.Id, inherited.Id); Assert.Equal("parent-model", inherited.DefaultModel);
        var explicitProvider = TerminalApp.ResolveSubAgentConfig(current, configurations, null, "chosen-child", "saved-model-override", "saved-child");
        Assert.Equal(chosen.Id, explicitProvider.Id); Assert.Equal("chosen-provider-model", explicitProvider.DefaultModel);
        var explicitModel = TerminalApp.ResolveSubAgentConfig(current, configurations, "task-specific-model", null, "saved-model-override", "saved-child");
        Assert.Equal(saved.Id, explicitModel.Id); Assert.Equal("task-specific-model", explicitModel.DefaultModel);
        var defaults = TerminalApp.ResolveSubAgentConfig(current, configurations, null, null, "saved-model-override", "saved-child");
        Assert.Equal(saved.Id, defaults.Id); Assert.Equal("saved-model-override", defaults.DefaultModel);
        Assert.Equal("parent-model", current.DefaultModel);
    }
    [Fact]
    public void SlashMenuFiltersSelectsAndCompletes()
    {
        var input = new InputState(); input.Set("/mc");
        var hints = input.Suggestions(CommandCatalog.Builtins); Assert.True(hints.Count > 5); Assert.All(hints, h => Assert.StartsWith("/mcp", h.Text));
        input.Key(Key(ConsoleKey.DownArrow), hints, []); input.Key(Key(ConsoleKey.Tab), hints, []);
        Assert.Equal("/mcp list ", input.Text);
        Assert.Equal("/mcp list ", input.Key(Key(ConsoleKey.Enter), [], []));
    }
    [Fact]
    public void EditorSupportsMultilineHistoryAndDeletionWithoutSubmittingEarly()
    {
        var input = new InputState(); input.Set("first"); Assert.Null(input.Key(Key(ConsoleKey.J, control: true), [], [])); input.Insert("second");
        Assert.Equal("first\nsecond", input.Text); input.Key(Key(ConsoleKey.W, control: true), [], []); Assert.Equal("first\n", input.Text);
        input.Set(""); input.Key(Key(ConsoleKey.UpArrow), [], ["older", "latest"]); Assert.Equal("latest", input.Text);
        input.Key(Key(ConsoleKey.UpArrow), [], ["older", "latest"]); Assert.Equal("older", input.Text);
        input.Set("needle"); input.Key(Key(ConsoleKey.R, control: true), [], ["needle old", "other", "needle newest"]); Assert.Equal("needle newest", input.Text);
        input.Key(Key(ConsoleKey.R, control: true), [], ["needle old", "other", "needle newest"]); Assert.Equal("needle old", input.Text);
        Assert.Equal("needle old", input.Key(Key(ConsoleKey.Enter), [], []));
    }
    [Fact]
    public void SpectreRendersChineseCommandPanelWithUsageAndLiveStatus()
    {
        using var workspace = new TestWorkspace(); var input = new InputState(); input.Set("/skills");
        var editor = new InputEditor(Path.Combine(workspace.Root, "history.json"), workspace.Root, () => CommandCatalog.Builtins);
        var console = new TestConsole(); console.Profile.Width = 120;
        console.Write(editor.Render(input, input.Suggestions(CommandCatalog.Builtins), "测试模型 · 只读规划"));
        Assert.Contains("/skills import", console.Output); Assert.Contains("导入 SKILL.md", console.Output); Assert.Contains("Tab", console.Output); Assert.Contains("只读规划", console.Output);
    }
    [Fact]
    public async Task SkillsImportPreservesMetadataReferencesAndRuntimeDisabling()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var source = Path.Combine(w.Root, "import"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), "---\nname: sample\ndescription: >\n  Sample instructions\n  with folded text\ndisable-model-invocation: true\n---\n\nDo $ARGUMENTS carefully.\n");
        await File.WriteAllTextAsync(Path.Combine(source, "notes.md"), "reference proof");
        var catalog = new SkillCatalog(store, w.Root, Path.Combine(w.Root, "data"), w.Root); var skill = catalog.Import(source);
        Assert.False(skill.ModelInvocable); Assert.Contains("folded text", skill.Description); Assert.Contains("Do task carefully", catalog.Invoke("sample", "task"));
        Assert.Equal("reference proof", catalog.ReadReference("sample", "notes.md")); Assert.Throws<UnauthorizedAccessException>(() => catalog.ReadReference("sample", "../../notes.md"));
        IAgentToolSet tools = new SkillTools(catalog); Assert.False((await tools.InvokeAsync("skill_read", JsonSerializer.SerializeToElement(new { name = "sample" }), default)).Ok);
        catalog.Enable("sample", false); Assert.Throws<ArgumentException>(() => catalog.Invoke("sample", "task"));
        var reloaded = new SkillCatalog(store, w.Root, Path.Combine(w.Root, "data"), w.Root); Assert.False(reloaded.Get("sample").Enabled);
    }
    [Fact]
    public void SkillsProjectOverrideAndBundledRemovalPersist()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data")); var catalog = new SkillCatalog(store, w.Root, Path.Combine(w.Root, "data"), w.Root);
        catalog.SeedBundled(); Assert.Equal(5, catalog.List().Count(s => s.Scope == "user")); catalog.Remove("review"); catalog.SeedBundled(); Assert.DoesNotContain(catalog.List(), s => s.Name == "review");
        catalog.Create("sample", "Global", "Global instructions", "user"); catalog.Create("sample", "Project", "Project instructions");
        Assert.Equal("project", catalog.Get("sample").Scope); Assert.Equal("Project", catalog.Get("sample").Description);
        Assert.Throws<ArgumentException>(() => catalog.Create("../escape", "bad", "bad"));
    }
    [Fact]
    public void SessionBranchCreatesIndependentMessagesAndIdentifiers()
    {
        using var w = new TestWorkspace(); using var store = new DataStore(Path.Combine(w.Root, "data"));
        var original = SessionOperations.Get(store, null); store.AddMessage(original.Id, new Message { ConversationId = original.Id, Role = "user", Content = "original" });
        var branch = SessionOperations.Branch(store, original, "experiment"); branch.Messages[0].Content = "changed"; store.UpdateConversation(branch);
        Assert.Equal("original", store.GetMessages(original.Id)[0].Content); Assert.NotEqual(original.Messages[0].Id, branch.Messages[0].Id); Assert.Equal(branch.Id, branch.Messages[0].ConversationId);
    }
    [Fact]
    public void CustomProvidersUseExplicitProtocolAndNeverExposeKeysInDisplay()
    {
        var config = new ApiConfig { ProviderId = "my-private-provider", Protocol = "anthropic", ApiKey = "PRIVATE-KEY", DefaultModel = "my-model" };
        Assert.IsType<AnthropicProvider>(new AiProviderFactory().GetProvider(config));
        Assert.DoesNotContain("PRIVATE-KEY", JsonSerializer.Serialize(ModelManager.Safe(config)));
        Assert.True(ProviderCatalog.All.Count >= 15);
    }
    [Fact]
    public void QuotedArgumentsRemainLiteralData()
    {
        Assert.Equal(new[] { "C:\\program files\\server.exe", "arg with space", "", "$(whoami);&" }, CommandLineWords.Parse("\"C:\\program files\\server.exe\" 'arg with space' \"\" $(whoami);&"));
        Assert.Throws<ArgumentException>(() => CommandLineWords.Parse("'unfinished"));
    }
}
