using System.Text.Json;
using AllAgent.NET.CodeGraph;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>CodeGraph integration: skill seeding, prompt guidance, surface gating, the
/// in-process tree-sitter index, and the shared declarative card contract.</summary>
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
        var indexPath = CodeIndex.IndexPath(workspace.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        await File.WriteAllTextAsync(indexPath, "{}");
        await extension.Configure(indexed, default);
        Assert.Contains("<allagent-codegraph>", indexed.VolatileSystemPrompt);
        Assert.Contains("skill_read", indexed.VolatileSystemPrompt);
        Assert.Contains("refreshes automatically", indexed.VolatileSystemPrompt);

        var disabled = new AgentTurnContext(Guid.NewGuid(), workspace.Root, false, "question", "system", [], []);
        await extension.Configure(disabled, default);
        Assert.DoesNotContain("<allagent-codegraph>", disabled.VolatileSystemPrompt);

        var noIndex = Path.Combine(workspace.Root, "other-workspace");
        Directory.CreateDirectory(noIndex);
        var missing = new AgentTurnContext(Guid.NewGuid(), noIndex, false, "question", "system", [], catalog.List());
        await extension.Configure(missing, default);
        Assert.Contains("no CodeGraph index", missing.VolatileSystemPrompt);
        Assert.Contains("builds it automatically", missing.VolatileSystemPrompt);
        Assert.DoesNotContain("no .codegraph/", missing.VolatileSystemPrompt);

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
    public async Task ServiceBuildsTheIndexAndExploresCSharpTypeScriptAndPython()
    {
        using var workspace = new TestWorkspace();
        var source = Path.Combine(workspace.Root, "src");
        var web = Path.Combine(workspace.Root, "web");
        var tools = Path.Combine(workspace.Root, "tools");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(web);
        Directory.CreateDirectory(tools);
        await File.WriteAllTextAsync(Path.Combine(source, "Alpha.cs"), """
            namespace Demo;

            public sealed class Alpha
            {
                public void Run()
                {
                    Beta.Helper();
                }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(source, "Beta.cs"), """
            namespace Demo;

            public static class Beta
            {
                public static void Helper() { }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(web, "app.ts"), """
            export function renderCard(): void {
              helper();
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(web, "Card.tsx"), """
            export function Card(): JSX.Element {
              return <div className="card" />;
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(web, "legacy.js"), """
            export function renderLegacy() {
              helper();
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(tools, "x.py"), """
            def compute():
                return 1
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "notes.md"), "unique-marker");

        var settings = new PluginSettings("AllAgent.NET.CodeGraph", new PluginSettingsStore(Path.Combine(workspace.Root, "shared", "plugins")));
        await using var service = new CodeGraphService(settings);

        var status = await service.StatusAsync(workspace.Root, default);
        Assert.False(status.Indexed);

        var explore = await service.ExploreAsync(workspace.Root, "Alpha", default);
        Assert.Null(explore.Error);
        Assert.Contains("Alpha", explore.Output);
        Assert.Contains("Run", explore.Output);
        Assert.Contains("Beta.Helper", explore.Output);

        status = await service.StatusAsync(workspace.Root, default);
        Assert.True(status.Indexed);
        Assert.True(status.Files >= 6);
        Assert.True(status.Symbols >= 6);
        Assert.True(status.References >= 1);

        var ts = await service.ExploreAsync(workspace.Root, "renderCard", default);
        Assert.Null(ts.Error);
        Assert.Contains("renderCard", ts.Output);
        var tsx = await service.ExploreAsync(workspace.Root, "Card", default);
        Assert.Null(tsx.Error);
        Assert.Contains("Card", tsx.Output);
        var js = await service.ExploreAsync(workspace.Root, "renderLegacy", default);
        Assert.Null(js.Error);
        Assert.Contains("renderLegacy", js.Output);
        var py = await service.ExploreAsync(workspace.Root, "compute", default);
        Assert.Null(py.Error);
        Assert.Contains("compute", py.Output);
        var text = await service.ExploreAsync(workspace.Root, "unique-marker", default);
        Assert.Null(text.Error);
        Assert.Contains("notes.md", text.Output);
        var fileName = await service.ExploreAsync(workspace.Root, "notes.md", default);
        Assert.Null(fileName.Error);
        Assert.Contains("notes.md", fileName.Output);

        await File.WriteAllTextAsync(Path.Combine(source, "Alpha.cs"), """
            namespace Demo;

            public sealed class Alpha
            {
                public void Run()
                {
                    Beta.Helper();
                }

                public void Added() { }
            }
            """);
        var operation = service.StartIndex(workspace.Root, IndexMode.Sync);
        var finished = await service.WaitAsync(operation.Id, default);
        Assert.Equal("done", finished.State);
        var added = await service.ExploreAsync(workspace.Root, "Added", default);
        Assert.Null(added.Error);
        Assert.Contains("Added", added.Output);

        using (AllAgent.NET.Core.Runtime.AgentExecutionScope.Enter(false, Guid.NewGuid(), workspace.Root))
        {
            var tool = new CodeGraphTools(service);
            var toolStatus = await tool.StatusAsync(default);
            Assert.True(toolStatus.Ok);
            Assert.Equal("CODEGRAPH", toolStatus.Card?.Badge);
            var toolExplore = await tool.ExploreAsync("Alpha", default);
            Assert.True(toolExplore.Ok);
            Assert.Equal("CODEGRAPH", toolExplore.Card?.Badge);
            Assert.Contains("Alpha", JsonSerializer.Serialize(toolExplore.Data, ToolResult.JsonOptions));
        }

        Assert.NotNull(CodeGraphService.ValidateQuery("-bad"));
        Assert.NotNull(CodeGraphService.ValidateQuery(""));
        Assert.Null(CodeGraphService.ValidateQuery("how does X work"));
    }

    [Fact]
    public void FileWalkerRespectsGitIgnore()
    {
        using var workspace = new TestWorkspace();
        var source = Path.Combine(workspace.Root, "src");
        var vendor = Path.Combine(workspace.Root, "vendor");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(vendor);
        File.WriteAllText(Path.Combine(workspace.Root, ".gitignore"), "vendor/\n*.generated.cs\n");
        File.WriteAllText(Path.Combine(source, "App.cs"), "public class App { }");
        File.WriteAllText(Path.Combine(source, "Ignored.generated.cs"), "public class Generated { }");
        File.WriteAllText(Path.Combine(vendor, "Lib.cs"), "public class Vendor { }");

        var files = CodeIndex.EnumerateFiles(workspace.Root, out var truncated);
        Assert.False(truncated);
        Assert.Contains(files, file => file.EndsWith("App.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, file => file.EndsWith("Ignored.generated.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, file => file.Replace('\\', '/').Contains("/vendor/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PluginCommandUsesTheCurrentWorkspaceAndTheBuiltInIndex()
    {
        using var workspace = new TestWorkspace();
        var source = Path.Combine(workspace.Root, "src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "Alpha.cs"), """
            namespace Demo;

            public sealed class Alpha
            {
                public void Run() { }
            }
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
        var plugin = new CodeGraphPlugin();
        plugin.Register(context);
        var command = Assert.Single(context.Commands);

        var status = await command.Execute(["status"], default);
        Assert.Contains("\"indexed\": false", status);
        var explore = await command.Execute(["explore", "Alpha"], default);
        Assert.Contains("Alpha", explore);
        var rebuild = await command.Execute(["rebuild"], default);
        Assert.Contains("完成", rebuild);
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
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(install, relative);
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
        Assert.True(host.SetSetting("AllAgent.NET.CodeGraph", "autosync", "false"));
        Assert.Equal("false", host.SettingsSnapshot("AllAgent.NET.CodeGraph")["autosync"]);
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
        store.Set("AllAgent.NET.CodeGraph", "autosync", "false");
        store.Set("AllAgent.NET.CodeGraph", "extra", "true");
        Assert.Equal("false", store.Get("AllAgent.NET.CodeGraph", "autosync"));
        Assert.Equal("true", store.GetAll("AllAgent.NET.CodeGraph")["extra"]);
        Assert.Throws<ArgumentException>(() => store.Set("AllAgent.NET.CodeGraph", "Bad Key", "x"));

        var reloaded = new PluginSettingsStore(root);
        Assert.Equal("false", reloaded.Get("AllAgent.NET.CodeGraph", "autosync"));
        reloaded.Set("AllAgent.NET.CodeGraph", "autosync", null);
        Assert.Null(reloaded.Get("AllAgent.NET.CodeGraph", "autosync"));
    }
}
