using AllAgent.NET.Core.CodeAct;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Tools;
using AllAgent.NET.Terminal;
using Xunit;

namespace AllAgent.NET.Tests;

/// <summary>Guards the size of the always-on system prompt: no duplicated catalog, no always-on desktop schemas.</summary>
public sealed class PromptCompositionTests
{
    private const string SkillHeader = "Available instruction skills (load via call_tool('skill_read', name=...) only when relevant):";

    [Fact]
    public void HostProviderKeepsASingleSkillCatalog()
    {
        var hostInstructions = "Project instructions\n" + SkillHeader + "\nmarker: Marker skill";
        var composed = AgentService.ComposeInstructions(hostInstructions, "FALLBACK", "general: Implementation");
        Assert.Equal(1, Occurrences(composed, SkillHeader));
        Assert.StartsWith("Project instructions", composed);
        Assert.EndsWith("Available child agents:\ngeneral: Implementation", composed);

        var fallback = AgentService.ComposeInstructions(null, "FALLBACK\n" + SkillHeader + "\nmarker: Marker skill", "general: Implementation");
        Assert.Equal(1, Occurrences(fallback, SkillHeader));
        Assert.StartsWith("FALLBACK", fallback);
    }

    [Fact]
    public void TerminalInstructionsLeaveTheSkillCatalogToTheTailSnapshot()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var catalog = new SkillCatalog(store, workspace.Root, Path.Combine(workspace.Root, "data"), workspace.Root);
        catalog.Create("marker", "Marker skill", "Do nothing.");
        var text = TerminalApp.Instructions(workspace.Root, catalog, plan: false);
        // The catalog is appended by AgentEngine as a volatile tail snapshot; the stable host
        // instructions stay byte-identical when a skill is enabled or disabled.
        Assert.DoesNotContain(SkillHeader, text);
        Assert.DoesNotContain("marker: Marker skill", text);
        Assert.Contains("marker: Marker skill", catalog.Instructions);
    }

    [Fact]
    public async Task DesktopToolsStayBehindTheSkillInsteadOfTheCatalog()
    {
        using var workspace = new TestWorkspace();
        var registry = new ToolRegistry();
        var state = new ComputerUseState();
        registry.Register(new ComputerUseTools(state));
        await using var codeAct = new CodeActProvider(registry, new PythonCodeValidator(workspace.Python), new NoopSandbox());

        // Registered host tools remain callable from the sandbox; only the prompt surface shrinks.
        Assert.Contains(registry.Definitions, definition => definition.Name == "computer_windows");
        Assert.DoesNotContain("computer_windows", codeAct.Instructions);
        Assert.DoesNotContain("computer_", Assert.Single(codeAct.ModelTools).Description);

        // One short pointer replaces twelve schemas and the old always-on guidance.
        Assert.Contains("## On-demand capabilities", codeAct.Instructions);
        Assert.Contains("skill_read", codeAct.Instructions);
        Assert.Contains("computer-use", codeAct.Instructions);
        Assert.DoesNotContain("## Windows Computer Use", codeAct.Instructions);
        Assert.DoesNotContain("computer_tree", Assert.Single(codeAct.ModelTools).Description);
    }

    [Fact]
    public async Task CodeActInstructionsRequireShortProgressNarration()
    {
        var registry = new ToolRegistry();
        await using var codeAct = new CodeActProvider(registry, new PythonCodeValidator("python"), new NoopSandbox());

        Assert.Contains("## Progress narration", codeAct.Instructions);
        Assert.Contains("Report key progress at meaningful checkpoints, not every step", codeAct.Instructions);
        Assert.Contains("Routine reads, searches, small edits", codeAct.Instructions);
        Assert.Contains("never a Python comment", codeAct.Instructions);

        codeAct.SimpleModeProvider = () => true;
        Assert.Contains("Report key progress at meaningful checkpoints", codeAct.Instructions);
        Assert.Contains("routine operations need no narration", codeAct.Instructions);
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private sealed class NoopSandbox : ICodeSandbox
    {
        public Task<ToolResult> ExecuteAsync(string code, IReadOnlyList<AgentToolDescriptor> tools, HostToolCallback callback, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
