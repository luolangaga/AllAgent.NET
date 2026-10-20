using System.Text.Json;
using AllAgent.NET.CodeGraph;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>CodeGraph integration: skill seeding, prompt guidance, surface gating, CLI status/explore
/// and the shared declarative card contract.</summary>
public sealed class CodeGraphPluginTests
{
    private const string Skill = """
        ---
        name: code-graph
        description: CodeGraph skill
        tools: [codegraph_*]
        ---

        Use codegraph_explore.
        """;

    [Fact]
    public async Task PluginSeedsTheSkillAndInjectsPromptOnlyWhenTheIndexExists()
    {
        using var workspace = new TestWorkspace();
        var data = Path.Combine(workspace.Root, "data");
        var shared = Path.Combine(workspace.Root, "shared");
        using var store = new DataStore(data, false, shared);
        var catalog = new SkillCatalog(store, workspace.Root, shared);
        var settings = new PluginSettingsStore(Path.Combine(shared, "plugins"));
        var context = new PluginContext(Path.Combine(shared, "plugins", "plugin-data"), new Dictionary<Type, object> { [typeof(SkillCatalog)] = catalog }, "AllAgent.NET.CodeGraph", null, settings);
        var plugin = new CodeGraphPlugin();

        plugin.Register(context);
        var skillPath = Path.Combine(catalog.UserDirectory, "code-graph", "SKILL.md");
        Assert.True(File.Exists(skillPath));
        Assert.Contains("codegraph_*", File.ReadAllText(skillPath));

        var extension = Assert.Single(context.TurnExtensions);
        var indexed = new AgentTurnContext(Guid.NewGuid(), workspace.Root, false, "question", "system", [], catalog.List());
        Directory.CreateDirectory(Path.Combine(workspace.Root, ".codegraph"));
        File.WriteAllText(Path.Combine(workspace.Root, ".codegraph", "codegraph.db"), "");
        await extension.Configure(indexed, default);
        Assert.Contains("<allagent-codegraph>", indexed.VolatileSystemPrompt);
        Assert.Contains("skill_read", indexed.VolatileSystemPrompt);

        var disabled = new AgentTurnContext(Guid.NewGuid(), workspace.Root, false, "question", "system", [], []);
        await extension.Configure(disabled, default);
        Assert.DoesNotContain("<allagent-codegraph>", disabled.VolatileSystemPrompt);

        var noIndex = Path.Combine(workspace.Root, "other-workspace");
        Directory.CreateDirectory(noIndex);
        var missing = new AgentTurnContext(Guid.NewGuid(), noIndex, false, "question", "system", [], catalog.List());
        await extension.Configure(missing, default);
        Assert.Contains("no .codegraph/", missing.VolatileSystemPrompt);
        Assert.DoesNotContain("skill_read", missing.VolatileSystemPrompt);

        await plugin.DisposeAsync();
    }

    [Fact]
    public void ToolSurfaceHidesTheFamilyBeforeDisclosureAndKeepsItHiddenWithoutTheSkill()
    {
        var tools = new[]
        {
            new AgentToolDescriptor("codegraph_explore", "", JsonSerializer.SerializeToElement(new { type = "object" }), ToolRisk.Read),
            new AgentToolDescriptor("codegraph_status", "", JsonSerializer.SerializeToElement(new { type = "object" }), ToolRisk.Read),
        };
        var surface = new ToolSurface();
        surface.Sync(ToolSurface.Discover(null, tools));
        Assert.True(surface.IsHidden("codegraph_explore"));
        Assert.False(surface.IsAvailable("codegraph_explore"));
        Assert.Equal("code-graph", surface.Capabilities.Single().Hint);

        surface.Sync([new ToolSurface.Capability("code-graph", ["codegraph_*"])]);
        Assert.True(surface.IsHidden("codegraph_explore"));
        Assert.False(surface.IsAvailable("codegraph_explore"));
        Assert.Equal(["codegraph_*"], surface.Activate("code-graph"));
        Assert.True(surface.IsAvailable("codegraph_explore"));
        surface.Reset();
        Assert.False(surface.IsAvailable("codegraph_status"));
    }

    [Fact]
    public async Task ServiceReadsStatusAndExploresThroughTheCli()
    {
        using var workspace = new TestWorkspace();
        var fake = Path.Combine(workspace.Root, "fake-codegraph.cmd");
        await File.WriteAllTextAsync(fake, """
            @echo off
            if "%1"=="version" (echo 1.6.2) else if "%1"=="status" (echo Files: 12) else if "%1"=="explore" (echo EXPLORE:%2) else if "%1"=="sync" (echo SYNCED) else if "%1"=="init" (echo INITIALIZED) else (echo OK)
            """);
        var settings = new PluginSettings("AllAgent.NET.CodeGraph", new PluginSettingsStore(Path.Combine(workspace.Root, "shared", "plugins")));
        settings.Set("path", fake);
        await using var service = new CodeGraphService(Path.Combine(workspace.Root, "plugin-data"), settings);

        var status = await service.StatusAsync(workspace.Root, default);
        Assert.True(status.Installed);
        Assert.Equal("1.6.2", status.Version);
        Assert.False(status.Indexed);

        Directory.CreateDirectory(Path.Combine(workspace.Root, ".codegraph"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, ".codegraph", "codegraph.db"), "");
        status = await service.StatusAsync(workspace.Root, default);
        Assert.True(status.Indexed);
        Assert.Contains("Files: 12", status.Detail);

        var explore = await service.ExploreAsync(workspace.Root, "AgentEngine", default);
        Assert.Null(explore.Error);
        Assert.Contains("EXPLORE:AgentEngine", explore.Output);

        using (AllAgent.NET.Core.Runtime.AgentExecutionScope.Enter(false, Guid.NewGuid(), workspace.Root))
        {
            var tools = new CodeGraphTools(service);
            var toolStatus = await tools.StatusAsync(default);
            Assert.True(toolStatus.Ok);
            Assert.Equal("CODEGRAPH", toolStatus.Card?.Badge);
            var toolExplore = await tools.ExploreAsync("AgentEngine", default);
            Assert.True(toolExplore.Ok);
            Assert.Equal("CODEGRAPH", toolExplore.Card?.Badge);
            Assert.Contains("EXPLORE:AgentEngine", JsonSerializer.Serialize(toolExplore.Data, ToolResult.JsonOptions));
        }

        var operation = service.StartIndex(workspace.Root, IndexMode.Sync);
        var finished = await service.WaitAsync(operation.Id, default);
        Assert.Equal("done", finished.State);
        Assert.Contains("SYNCED", finished.Output);

        Assert.NotNull(CodeGraphService.ValidateQuery("-bad"));
        Assert.NotNull(CodeGraphService.ValidateQuery(""));
        Assert.Null(CodeGraphService.ValidateQuery("how does X work"));
    }

    [Fact]
    public async Task PluginCommandUsesTheCurrentWorkspaceAndTheConfiguredCli()
    {
        using var workspace = new TestWorkspace();
        var fake = Path.Combine(workspace.Root, "fake-codegraph.cmd");
        await File.WriteAllTextAsync(fake, """
            @echo off
            if "%1"=="version" (echo 1.6.2) else if "%1"=="status" (echo Files: 12) else if "%1"=="explore" (echo EXPLORE:%2) else if "%1"=="sync" (echo SYNCED) else if "%1"=="init" (echo INITIALIZED) else (echo OK)
            """);
        var data = Path.Combine(workspace.Root, "data");
        var shared = Path.Combine(workspace.Root, "shared");
        using var store = new DataStore(data, false, shared);
        var catalog = new SkillCatalog(store, workspace.Root, shared);
        var settings = new PluginSettingsStore(Path.Combine(shared, "plugins"));
        var context = new PluginContext(Path.Combine(shared, "plugins", "plugin-data"), new Dictionary<Type, object>
        {
            [typeof(SkillCatalog)] = catalog,
            [typeof(PluginWorkspaceTasks)] = new PluginWorkspaceTasks(() => [], (_, _) => throw new NotSupportedException()) { Current = () => workspace.Root },
        }, "AllAgent.NET.CodeGraph", null, settings);
        settings.Set("AllAgent.NET.CodeGraph", "path", fake);
        var plugin = new CodeGraphPlugin();
        plugin.Register(context);
        var command = Assert.Single(context.Commands);

        var status = await command.Execute(["status"], default);
        Assert.Contains("\"installed\": true", status);
        Assert.Contains("1.6.2", status);
        var path = await command.Execute(["path"], default);
        Assert.Contains(fake, path);

        Directory.CreateDirectory(Path.Combine(workspace.Root, ".codegraph"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, ".codegraph", "codegraph.db"), "");
        var explore = await command.Execute(["explore", "AgentEngine"], default);
        Assert.Contains("EXPLORE:AgentEngine", explore);
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task PluginHostLoadsToolsAndDeclaredSettings()
    {
        using var workspace = new TestWorkspace();
        var shared = Path.Combine(workspace.Root, "shared");
        var pluginRoot = Path.Combine(shared, "plugins");
        var install = Path.Combine(pluginRoot, "allagent.net.codegraph-1.0.0-test");
        Directory.CreateDirectory(install);
        var source = Path.Combine(AppContext.BaseDirectory, "codegraph-plugin");
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(install, Path.GetFileName(file)), true);
        var webSource = Path.Combine(source, "web");
        if (Directory.Exists(webSource))
            foreach (var file in Directory.GetFiles(webSource, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(install, "web", Path.GetRelativePath(webSource, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }

        using var store = new DataStore(Path.Combine(workspace.Root, "data"), false, shared);
        var catalog = new SkillCatalog(store, workspace.Root, shared);
        await using var host = new PluginHost(shared);
        host.ProvideService(catalog);
        host.Save(new InstalledPlugin("AllAgent.NET.CodeGraph", "1.0.0", Path.GetFileName(install)));
        await host.LoadEnabledAsync();

        var status = Assert.Single(host.Status);
        Assert.Null(status.Error);
        Assert.Contains("codegraph_explore", status.Tools!);
        Assert.Contains("codegraph_status", status.Tools!);
        Assert.Equal(1, status.Web?.Settings);
        Assert.True(host.SetSetting("AllAgent.NET.CodeGraph", "path", @"C:\tools\codegraph.exe"));
        Assert.Equal(@"C:\tools\codegraph.exe", host.SettingsSnapshot("AllAgent.NET.CodeGraph")["path"]);
        Assert.Throws<ArgumentException>(() => host.SetSetting("AllAgent.NET.CodeGraph", "not_declared", "x"));
        Assert.True(File.Exists(Path.Combine(catalog.UserDirectory, "code-graph", "SKILL.md")));
    }

    [Fact]
    public void TerminalPresentationUsesTheDeclarativeCard()
    {
        var card = new AgentToolCard(
            Badge: "CODEGRAPH",
            Title: "AgentEngine 如何选择工具？",
            Summary: "12 chars",
            Body: "code",
            Text: "src/App.cs:1\nsrc/App.cs:2",
            Fields: [new AgentToolCardField("工作区", "LanAgent")]);
        var result = ToolResult.Success(new { output = "x" }) with { Card = card };
        var parsed = ToolCardPresentation.Read(result.ToJson());
        Assert.NotNull(parsed);
        Assert.Equal("CODEGRAPH", ToolCardPresentation.Badge(parsed!, "fallback"));
        Assert.Equal("AgentEngine 如何选择工具？", ToolCardPresentation.Title(parsed!, "fallback"));
        Assert.Equal("12 chars", ToolCardPresentation.Summary(parsed!));
        Assert.Equal(["src/App.cs:1", "src/App.cs:2"], ToolCardPresentation.Lines(parsed!));
        Assert.Contains("工作区: LanAgent", ToolCardPresentation.Fields(parsed!));
        Assert.Null(ToolCardPresentation.Read(null));
    }

    [Fact]
    public void DeclarativeCardIsSerializedAndClipped()
    {
        var card = new AgentToolCard(
            Badge: "CODEGRAPH",
            Title: new string('t', 300),
            Summary: "summary",
            Body: "code",
            Text: new string('x', 5000),
            Fields: Enumerable.Range(0, 10).Select(index => new AgentToolCardField(new string('l', 300), "value")).ToArray());
        var result = ToolResult.Success(new { output = "text" }) with { Card = card };
        var json = result.ToJson();
        using var document = JsonDocument.Parse(json);
        Assert.Equal("CODEGRAPH", document.RootElement.GetProperty("card").GetProperty("badge").GetString());

        var limited = ToolResultBudget.Apply(result, 32_000, 8_000);
        Assert.NotNull(limited.Card);
        Assert.True(limited.Card!.Title!.Length <= 200);
        Assert.True(limited.Card.Text!.Length <= 4_000);
        Assert.True(limited.Card.Fields!.Count <= 8);
        Assert.All(limited.Card.Fields, field => Assert.True(field.Label.Length <= 200));
    }

    [Fact]
    public void SettingsStorePersistsAndValidatesKeys()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.Root, "plugins");
        var store = new PluginSettingsStore(root);
        store.Set("AllAgent.NET.CodeGraph", "path", @"C:\tools\codegraph.exe");
        store.Set("AllAgent.NET.CodeGraph", "autosync", "false");
        Assert.Equal(@"C:\tools\codegraph.exe", store.Get("AllAgent.NET.CodeGraph", "path"));
        Assert.Equal("false", store.GetAll("AllAgent.NET.CodeGraph")["autosync"]);
        Assert.Throws<ArgumentException>(() => store.Set("AllAgent.NET.CodeGraph", "Bad Key", "x"));

        var reloaded = new PluginSettingsStore(root);
        Assert.Equal(@"C:\tools\codegraph.exe", reloaded.Get("AllAgent.NET.CodeGraph", "path"));
        reloaded.Set("AllAgent.NET.CodeGraph", "path", null);
        Assert.Null(reloaded.Get("AllAgent.NET.CodeGraph", "path"));
    }
}
