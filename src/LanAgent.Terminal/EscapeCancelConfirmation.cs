namespace LanAgent.Terminal;

internal sealed class EscapeCancelConfirmation
{
    private long? _first;
    internal const int WindowMilliseconds = 5000;
    public bool Armed => _first is { } first && Environment.TickCount64 - first < WindowMilliseconds;
    public void Reset() => _first = null;
    public bool Read(ConsoleKeyInfo key, long? now = null)
    {
        // Some Windows VT input backends report the ESC control byte with Control.
        if (key.Key != ConsoleKey.Escape) { Reset(); return false; }
        var current = now ?? Environment.TickCount64;
        if (_first is { } first && current - first < WindowMilliseconds) { Reset(); return true; }
        _first = current; return false;
    }
}
