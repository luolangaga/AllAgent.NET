using System.Text.Json;
using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>Progressive disclosure: hidden schemas, a named owner, and a disclosure that unlocks both.</summary>
public sealed class ToolSurfaceTests
{
    [Fact]
    public void PrefixPatternsGateWholeFamilies()
    {
        var surface = new ToolSurface();
        surface.Sync([new ToolSurface.Capability("computer-use", ["computer_*", "browser"])]);
        Assert.True(surface.IsHidden("computer_tree"));
        Assert.True(surface.IsHidden("browser"));
        Assert.True(surface.IsHidden("computer_status"));
        Assert.False(surface.IsHidden("file_read"));
        Assert.False(surface.IsAvailable("computer_tree"));
        var declared = surface.Version;
        Assert.Equal(["computer_*", "browser"], surface.Activate("computer-use"));
        Assert.True(surface.IsAvailable("computer_tree"));
        Assert.True(surface.Version > declared);
        // A second disclosure must not invalidate prompt caches again.
        var stable = surface.Version;
        surface.Activate("computer-use");
        Assert.Equal(stable, surface.Version);
        surface.Reset();
        Assert.False(surface.IsAvailable("computer_tree"));
    }

    [Fact]
    public void DisablingTheOwningSkillWithdrawsItsDisclosure()
    {
        var surface = new ToolSurface();
        surface.Sync([new ToolSurface.Capability("computer-use", ["computer_*"])]);
        surface.Activate("computer-use");
        surface.Sync([new ToolSurface.Capability("computer-use", ["computer_*"])]);
        Assert.True(surface.IsAvailable("computer_status"));
        // Disabling the skill keeps the family hidden, now without an owner to disclose it.
        surface.Sync([new ToolSurface.Capability(null, ["computer_*"])]);
        Assert.True(surface.IsHidden("computer_status"));
        Assert.False(surface.IsAvailable("computer_status"));
    }

    [Fact]
    public async Task HiddenCapabilityNamesItsSkillThenDisclosesContractAndSchemas()
    {
        using var workspace = new TestWorkspace();
        var registry = new ToolRegistry();
        registry.Register(new StubTools(
            Tool("computer_status"),
            Tool("computer_tree"),
            Tool("skill_read", "name")));
        var surface = new ToolSurface();
        surface.Sync([new ToolSurface.Capability("computer-use", ["computer_*"])]);
        await using var provider = new CodeActProvider(registry, new(workspace.Python), new NoSandbox(), surface: surface);

        var instructions = provider.Instructions;
        Assert.Contains("## On-demand capabilities", instructions);
        Assert.Contains("computer-use", instructions);
        // The schema stays out of the catalog; only the pointer names the family.
        Assert.DoesNotContain("computer_status", Catalog(instructions));

        var denied = await provider.CallToolAsync("computer_status", Empty(), default);
        Assert.False(denied.Ok);
        Assert.Contains("computer-use", denied.Error);
        Assert.Contains("skill_read", denied.Error);

        var loaded = await provider.CallToolAsync("skill_read", JsonSerializer.SerializeToElement(new { name = "computer-use" }), default);
        Assert.True(loaded.Ok);
        var activated = JsonSerializer.SerializeToElement(loaded.Data, ToolResult.JsonOptions).GetProperty("activatedTools").EnumerateArray().Select(name => name.GetString()!).ToArray();
        Assert.Equal(["computer_status", "computer_tree"], activated);
        // Loading discloses the schemas for the next request, and the tool runs from here on.
        Assert.Contains("computer_status", Catalog(provider.Instructions));
        Assert.True((await provider.CallToolAsync("computer_status", Empty(), default)).Ok);
        // The disclosure survives the provider instance: hosts share one surface per conversation.
        await using var nextTurn = new CodeActProvider(registry, new(workspace.Python), new NoSandbox(), surface: surface);
        Assert.Contains("computer_status", Catalog(nextTurn.Instructions));
    }

    [Fact]
    public async Task RoleAllowlistStillNarrowsADisclosedCapability()
    {
        using var workspace = new TestWorkspace();
        var registry = new ToolRegistry();
        registry.Register(new StubTools(Tool("computer_status"), Tool("file_read"), Tool("skill_read", "name")));
        var surface = new ToolSurface();
        surface.Sync([new ToolSurface.Capability("computer-use", ["computer_*"])]);
        await using var provider = new CodeActProvider(registry, new(workspace.Python), new NoSandbox(), surface: surface);
        await provider.CallToolAsync("skill_read", JsonSerializer.SerializeToElement(new { name = "computer-use" }), default);

        provider.HostToolsProvider = () => ["file_read"];
        var catalog = Catalog(provider.Instructions);
        Assert.DoesNotContain("computer_status", catalog);
        Assert.Contains("file_read", catalog);
    }

    [Fact]
    public async Task CapabilityWithoutOwnerIsDeniedInsteadOfGuessed()
    {
        using var workspace = new TestWorkspace();
        var registry = new ToolRegistry();
        registry.Register(new StubTools(Tool("computer_status")));
        var surface = new ToolSurface();
        // The owning skill is disabled or absent: nothing can disclose the contract.
        surface.Sync([new ToolSurface.Capability(null, ["computer_*"])]);
        await using var provider = new CodeActProvider(registry, new(workspace.Python), new NoSandbox(), surface: surface);

        Assert.DoesNotContain("computer_status", Catalog(provider.Instructions));
        var denied = await provider.CallToolAsync("computer_status", Empty(), default);
        Assert.False(denied.Ok);
        Assert.Contains("no enabled skill", denied.Error);
    }

    [Fact]
    public void FrontmatterDeclaresWhichToolsASkillDiscloses()
    {
        var root = Path.Combine(Path.GetTempPath(), "AllAgent.NET.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string Write(string body)
            {
                var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".md");
                File.WriteAllText(path, body);
                return path;
            }
            var declared = SkillCatalog.Parse(Write("---\nname: demo\ndescription: demo skill\ntools: [computer_*, browser]\n---\n\nBody"), "project");
            Assert.Equal(["computer_*", "browser"], declared.DeclaredTools);
            var guidanceOnly = SkillCatalog.Parse(Write("---\nname: plain\ndescription: guidance only\n---\n\nBody"), "project");
            Assert.Empty(guidanceOnly.DeclaredTools);
            Assert.Throws<ArgumentException>(() => SkillCatalog.Parse(Write("---\nname: bad\ndescription: bad pattern\ntools: [NOT A TOOL]\n---\n\nBody"), "project"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InstallsWithoutTheToolsFieldStillGetALoadableBuiltin()
    {
        var root = Path.Combine(Path.GetTempPath(), "AllAgent.NET.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new DataStore(Path.Combine(root, "data"));
            var catalog = new SkillCatalog(store, root, Path.Combine(root, "data"), root);
            var capabilities = ToolSurface.Discover(catalog, [Tool("computer_status"), Tool("browser")]);
            // browser-use ships in code, so its bridge is loadable even when the browser tool is the only one;
            // the desktop family has no seeded skill here and therefore stays disclosed by nobody.
            Assert.Contains(capabilities, capability => capability.Skill == "browser-use" && capability.Patterns.SequenceEqual(new[] { "browser" }));
            Assert.Contains(capabilities, capability => capability.Skill == null && capability.Patterns.SequenceEqual(new[] { "computer_*" }));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Catalog(string instructions)
    {
        var start = instructions.IndexOf("## Host tool catalog", StringComparison.Ordinal);
        Assert.True(start >= 0, "The catalog section must exist.");
        var end = instructions.IndexOf("\n## ", start + 4, StringComparison.Ordinal);
        return end < 0 ? instructions[start..] : instructions[start..end];
    }

    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new { });

    private static AgentToolDescriptor Tool(string name, params string[] required) => new(name, $"{name} test tool",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false,
            properties = required.ToDictionary(property => property, _ => new { type = "string" }),
            required
        }, ToolResult.JsonOptions), ToolRisk.Read);

    private sealed class StubTools(params AgentToolDescriptor[] definitions) : IAgentToolSet
    {
        public IReadOnlyList<AgentToolDescriptor> Definitions { get; } = definitions;
        public ValueTask<ToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct) => ValueTask.FromResult(
            name == "skill_read"
                ? ToolResult.Success(new { skill = new { name = ToolArguments.Get(arguments, "name", "") }, content = "contract", nextOffset = 8, totalCharacters = 8 })
                : ToolResult.Success(new { tool = name }));
    }

    private sealed class NoSandbox : ICodeSandbox
    {
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct) =>
            throw new NotSupportedException("These tests exercise the tool surface, not the guest runtime.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
