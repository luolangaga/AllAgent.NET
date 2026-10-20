using System.Security.Cryptography;
using System.Text;

namespace AllAgent.NET.Core.Services;

internal sealed record InstructionBlock(string Id, string Source, string Text, bool Stable = true);
internal sealed record PromptBlockInfo(string Id, string Source, string Version, int Characters, bool Stable);
internal sealed record PromptManifest(string Version, IReadOnlyList<PromptBlockInfo> Blocks);
internal sealed record ComposedPrompt(string Text, PromptManifest Manifest);

/// <summary>One deterministic composition path. Diagnostics contain identities and sizes, never prompt text.</summary>
internal static class PromptComposer
{
    private static readonly AsyncLocal<List<InstructionBlock>?> Sources = new();
    internal static void NoteSource(string id, string source, string text) => Sources.Value?.Add(new(id, source, text));

    internal static AI.ChatMessage GuidanceMessage(string session, Func<string> configured)
    {
        var previous = Sources.Value;
        Sources.Value = [];
        try
        {
            var text = Guidance(session, configured());
            return new("system", text) { InstructionSources = Sources.Value.Where(block => text.Contains(block.Text, StringComparison.Ordinal)).Distinct().ToArray() };
        }
        finally { Sources.Value = previous; }
    }
    internal static ComposedPrompt Compose(IEnumerable<InstructionBlock> blocks)
    {
        var unique = new Dictionary<string, InstructionBlock>(StringComparer.Ordinal);
        foreach (var block in blocks.Where(block => !string.IsNullOrWhiteSpace(block.Text)))
        {
            if (unique.TryGetValue(block.Id, out var previous) && previous != block)
                throw new InvalidOperationException("Conflicting instruction block: " + block.Id);
            unique.TryAdd(block.Id, block);
        }
        var merged = unique.Values.GroupBy(block => block.Text, StringComparer.Ordinal)
            .Select(group => group.First() with { Source = string.Join(" | ", group.Select(block => block.Source).Distinct(StringComparer.Ordinal)) }).ToArray();
        var entries = merged.Select(block => new PromptBlockInfo(block.Id, block.Source, Hash(block.Text), block.Text.Length, block.Stable)).ToArray();
        return new(string.Join("\n\n", merged.Select(block => block.Text)),
            new(Hash(string.Join('|', entries.Select(entry => entry.Id + ":" + entry.Version))), entries));
    }

    internal static ComposedPrompt FromSections(IEnumerable<string> sections)
    {
        var dynamic = false;
        return Compose(sections.Select((text, index) =>
        {
            if (text.StartsWith("## Sandbox runtime", StringComparison.Ordinal) ||
                text.StartsWith("## On-demand capabilities", StringComparison.Ordinal) ||
                text.StartsWith("## Host tool catalog", StringComparison.Ordinal) ||
                text.StartsWith("Available PowerShell modules", StringComparison.Ordinal)) dynamic = true;
            var source = text.StartsWith("## Host tool catalog", StringComparison.Ordinal) ? "ToolRegistry / ToolSurface" :
                text.StartsWith("## Sandbox runtime", StringComparison.Ordinal) ? "sandbox runtime" :
                text.StartsWith("CURRENT HOST MODE", StringComparison.Ordinal) ? "AgentOptions.CurrentPermission" :
                text.StartsWith("CURRENT ROLE", StringComparison.Ordinal) ? "AgentOptions.ReadOnlyProvider" :
                text.StartsWith("Verified host environment", StringComparison.Ordinal) ? "EnvironmentDoctor" : "CodeActInstructions / host";
            return new InstructionBlock("codeact:" + index, source, text, !dynamic);
        }).ToArray());
    }

    internal static string Join(params string?[] sections) => Compose(sections
        .Where(section => !string.IsNullOrWhiteSpace(section)).Distinct(StringComparer.Ordinal)
        .Select((text, index) => new InstructionBlock("host:" + index, "host/project/role", text!))).Text;

    internal static string Guidance(string session, string configured) => Compose([
        new("session", "conversation.SystemPrompt / turn extensions", session),
        new("configured", "host/project/role/skills", configured)
    ]).Text;

    internal static void ProtectRequest(CodeAct.CodeActProvider codeAct, IList<AI.ChatMessage> messages)
    {
        var stable = codeAct.StableInstructions;
        for (var index = messages.Count - 1; index >= 0; index--)
            if (messages[index].IsHostContract ||
                messages[index].Role == "system" && (messages[index].Content == stable || messages[index].Content == codeAct.Instructions))
                messages.RemoveAt(index);
        messages.Insert(0, new("system", stable) { IsHostContract = true });
        codeAct.CaptureRequestManifest(messages);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
