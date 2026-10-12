namespace LanAgent.Terminal;

internal sealed class QueueShortcut
{
    private DateTime _deadline;
    // Ctrl+X remains cut when text is selected; otherwise Ctrl+X,Q opens the queue.
    public int Read(ConsoleKeyInfo key, bool hasSelection, bool timeline = false)
    {
        var armed = DateTime.UtcNow < _deadline; _deadline = default;
        if (armed && key.Key == ConsoleKey.Q && key.Modifiers == 0) return 2;
        if (armed && timeline && key.Key == ConsoleKey.G && key.Modifiers == 0) return 3;
        if (!hasSelection && key.Key == ConsoleKey.X && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        { _deadline = DateTime.UtcNow.AddSeconds(2); return 1; }
        return 0;
    }
}
