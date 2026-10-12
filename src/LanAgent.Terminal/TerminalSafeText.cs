using System.Text.RegularExpressions;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

internal static partial class TerminalSafeText
{
    [GeneratedRegex(@"\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(?:\x07|\x1b\\)|[@-_])")]
    private static partial Regex EscapeSequences();
    public static string Clean(string text) => new(EscapeSequences().Replace(text, "").Where(c => c is '\n' or '\r' or '\t' || c >= 32 && c != 127 && c is not (>= '\u0080' and <= '\u009f')).ToArray());
}

internal sealed class SafeTerminalRenderable(IRenderable content) : IRenderable, IVersionedRenderable
{
    public int Version => content is IVersionedRenderable versioned ? versioned.Version : 0;
    public Measurement Measure(RenderOptions options, int maxWidth) => content.Measure(options, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => content.Render(options, maxWidth).Select(s => new Segment(TerminalSafeText.Clean(s.Text), s.Style));
}
