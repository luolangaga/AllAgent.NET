using Spectre.Console;
using Spectre.Console.Rendering;

namespace AllAgent.NET.Terminal;

/// <summary>Standard Spectre prompts with consistent Escape cancellation and paste-as-data.</summary>
internal sealed class SpectrePromptConsole(IAnsiConsole console, TerminalKeyReader? reader = null, string? initialText = null) : IAnsiConsole
{
    public Profile Profile => console.Profile;
    public IAnsiConsoleCursor Cursor => console.Cursor;
    public IAnsiConsoleInput Input { get; } = new PromptInput(reader ?? new TerminalKeyReader(console.Input.IsKeyAvailable, () => console.Input.ReadKey(true) ?? default), initialText);
    public IExclusivityMode ExclusivityMode => console.ExclusivityMode;
    public RenderPipeline Pipeline => console.Pipeline;
    public void Clear(bool home) => console.Clear(home);
    public void Write(IRenderable renderable) => console.Write(renderable);
    public void WriteAnsi(Action<AnsiWriter> action) => console.WriteAnsi(action);

    private sealed class PromptInput(TerminalKeyReader reader, string? initialText) : IAnsiConsoleInput
    {
        // Unicode characters are text, not virtual key codes (which must be <= 255).
        private readonly Queue<ConsoleKeyInfo> _paste = new((initialText ?? "").Select(character => new ConsoleKeyInfo(character, ConsoleKey.A, false, false, false)));
        public bool IsKeyAvailable() => _paste.Count > 0 || reader.HasInput;
        public ConsoleKeyInfo? ReadKey(bool intercept) => ReadKeyAsync(intercept, CancellationToken.None).GetAwaiter().GetResult();
        public async Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
        {
            if (_paste.TryDequeue(out var queued)) return queued;
            var next = await reader.ReadAsync(cancellationToken);
            if (next.Wheel != 0)
            {
                var direction = next.Wheel > 0 ? ConsoleKey.UpArrow : ConsoleKey.DownArrow;
                for (var index = 1; index < Math.Min(12, Math.Abs(next.Wheel)); index++) _paste.Enqueue(new('\0', direction, false, false, false));
                return new('\0', direction, false, false, false);
            }
            if (next.Pointer is { Button: PointerButton.Right, Action: PointerAction.Down }) next = next with { Paste = reader.ReadClipboard() };
            if (next.Key.Key == ConsoleKey.Escape && next.Paste == null || next.Key.Key == ConsoleKey.C && next.Key.Modifiers.HasFlag(ConsoleModifiers.Control))
                throw new OperationCanceledException("已返回。");
            if (next.Paste != null)
            {
                // A configuration field is one line. Embedded newlines never become submit keystrokes.
                foreach (var character in TerminalSafeText.Clean(next.Paste).Replace('\n', ' ').Replace('\r', ' '))
                    _paste.Enqueue(new(character, ConsoleKey.A, false, false, false));
                return _paste.TryDequeue(out queued) ? queued : default(ConsoleKeyInfo);
            }
            return next.Key;
        }
    }
}
