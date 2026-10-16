using System.Text;

namespace LanAgent.Terminal;

internal enum PointerAction { Down, Drag, Up }
internal enum PointerButton { Left, Right }
internal readonly record struct TerminalPointer(int X, int Y, PointerAction Action, bool Extend = false, PointerButton Button = PointerButton.Left);
internal readonly record struct TerminalInput(ConsoleKeyInfo Key, string? Paste = null, int Wheel = 0, TerminalPointer? Pointer = null, int? MouseX = null, int? MouseY = null);

/// <summary>Single, cancellable key reader. Escape and pasted newlines cannot be confused with submit commands.</summary>
internal sealed class TerminalKeyReader
{
    private readonly Queue<TerminalInput> _pending = new();
    private readonly Func<bool> _available;
    private readonly Func<ConsoleKeyInfo> _read;
    private readonly Func<string?> _clipboard;
    private readonly bool _native;
    private TerminalInput? _deferred;
    private bool _discardMouseTail;
    public bool HasInput
    {
        get
        {
            if (_deferred != null || _pending.Count > 0) return true;
            if (_native && ConsoleModes.ReadPointer() is { } mouse)
            {
                // Collapse a burst of native wheel records into one viewport update, without eating keys or selection events.
                if (mouse.Wheel != 0)
                {
                    for (var count = 0; count < 64 && ConsoleModes.ReadPointer() is { } next; count++)
                    {
                        if (next.Wheel == 0 || Math.Sign(next.Wheel) != Math.Sign(mouse.Wheel) || next.MouseX != mouse.MouseX || next.MouseY != mouse.MouseY) { _pending.Enqueue(mouse); _pending.Enqueue(next); return true; }
                        mouse = mouse with { Wheel = mouse.Wheel + next.Wheel };
                    }
                }
                _pending.Enqueue(mouse); return true;
            }
            return _available();
        }
    }
    public TerminalKeyReader(Func<bool>? available = null, Func<ConsoleKeyInfo>? read = null, Func<string?>? clipboard = null)
    { _native = available == null; _available = available ?? (() => Console.KeyAvailable); _read = read ?? (() => Console.ReadKey(true)); _clipboard = clipboard ?? ClipboardText.Read; }
    private async Task<TerminalInput> KeyInput(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.V || key.Modifiers.HasFlag(ConsoleModifiers.Shift) && key.Key == ConsoleKey.Insert)
        {
            var clipboard = await Task.Run(ReadClipboard, ct);
            if (clipboard != null) return new(key, clipboard.Replace("\r\n", "\n").Replace('\r', '\n'));
        }
        return new(key);
    }
    internal string? ReadClipboard() => (_native ? ClipboardText.ReadImageReference() : null) ?? _clipboard();
    private ConsoleKeyInfo ReadKey()
    {
        var key = _read();
        if (key.Key != (ConsoleKey)0) return key;
        var character = key.KeyChar;
        var mapped = character switch {
            '\u001b' => ConsoleKey.Escape, '\r' => ConsoleKey.Enter, '\n' => ConsoleKey.J,
            '\t' => ConsoleKey.Tab, ' ' => ConsoleKey.Spacebar, '\b' or '\u007f' => ConsoleKey.Backspace,
            >= '\u0001' and <= '\u001a' => ConsoleKey.A + (character - 1),
            >= 'a' and <= 'z' => ConsoleKey.A + (character - 'a'),
            >= 'A' and <= 'Z' => ConsoleKey.A + (character - 'A'),
            >= '0' and <= '9' => ConsoleKey.D0 + (character - '0'), _ => (ConsoleKey)0
        };
        return new(character, mapped, key.Modifiers.HasFlag(ConsoleModifiers.Shift), key.Modifiers.HasFlag(ConsoleModifiers.Alt),
            key.Modifiers.HasFlag(ConsoleModifiers.Control) || character is >= '\u0001' and <= '\u001a' && character is not ('\r' or '\t' or '\b'));
    }
    private static ConsoleKeyInfo? SequenceKey(string sequence)
    {
        if (!(sequence.StartsWith("\u001b[") || sequence.StartsWith("\u001bO"))) return null;
        var body = sequence[2..]; var ending = body[^1]; var parts = body[..^1].Split(';');
        var modifier = parts.Length > 1 && int.TryParse(parts[^1], out var value) ? value - 1 : 0;
        ConsoleKey? key = ending switch {
            'A' => ConsoleKey.UpArrow, 'B' => ConsoleKey.DownArrow, 'C' => ConsoleKey.RightArrow, 'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home, 'F' => ConsoleKey.End, 'Z' => ConsoleKey.Tab,
            'P' => ConsoleKey.F1, 'Q' => ConsoleKey.F2, 'R' => ConsoleKey.F3, 'S' => ConsoleKey.F4,
            '~' => parts[0] switch { "1" or "7" => ConsoleKey.Home, "2" => ConsoleKey.Insert, "3" => ConsoleKey.Delete,
                "4" or "8" => ConsoleKey.End, "5" => ConsoleKey.PageUp, "6" => ConsoleKey.PageDown,
                "11" => ConsoleKey.F1, "12" => ConsoleKey.F2, "13" => ConsoleKey.F3, "14" => ConsoleKey.F4,
                "15" => ConsoleKey.F5, "17" => ConsoleKey.F6, "18" => ConsoleKey.F7, "19" => ConsoleKey.F8,
                "20" => ConsoleKey.F9, "21" => ConsoleKey.F10, "23" => ConsoleKey.F11, "24" => ConsoleKey.F12, _ => null },
            _ => null
        };
        return key == null ? null : new ConsoleKeyInfo('\0', key.Value, ending == 'Z' || (modifier & 1) != 0, (modifier & 2) != 0, (modifier & 4) != 0);
    }
    public async Task<TerminalInput> ReadAsync(CancellationToken ct)
    {
        var input = _deferred ?? await ReadOneAsync(ct); _deferred = null;
        var dragging = input.Pointer is { Action: PointerAction.Drag };
        if (input.Wheel == 0 && !dragging) return input;
        for (var count = 0; count < 512 && HasInput; count++)
        {
            var next = await ReadOneAsync(ct);
            if (dragging)
            {
                if (next.Pointer is not { Action: PointerAction.Drag } pointer || pointer.Button != input.Pointer!.Value.Button || pointer.Extend != input.Pointer.Value.Extend) { _deferred = next; break; }
                input = next; continue;
            }
            if (next.Wheel == 0 || Math.Sign(next.Wheel) != Math.Sign(input.Wheel) || next.MouseX != input.MouseX || next.MouseY != input.MouseY) { _deferred = next; break; }
            input = input with { Wheel = input.Wheel + next.Wheel };
        }
        return input;
    }
    private async Task<TerminalInput> ReadOneAsync(CancellationToken ct)
    {
        // A cancelled prompt may have consumed only the start of an SGR report.
        // Keep ownership of its remaining bytes across prompt cancellation.
        if (_discardMouseTail)
        {
            while (true)
            {
                while (!_available()) { ct.ThrowIfCancellationRequested(); await Task.Delay(2, ct); }
                var tail = ReadKey().KeyChar;
                if (tail is 'M' or 'm') { _discardMouseTail = false; return new(default); }
                if (!(char.IsAsciiDigit(tail) || tail is ';')) { _discardMouseTail = false; return await KeyInput(new ConsoleKeyInfo(tail, (ConsoleKey)0, false, false, false), ct); }
            }
        }
        if (_pending.TryDequeue(out var pending)) return pending;
        while (!HasInput) { ct.ThrowIfCancellationRequested(); await Task.Delay(16, ct); }
        if (_pending.TryDequeue(out var mouse)) return mouse;
        var key = ReadKey();
        // Windows Console.ReadKey folds ESC+[ into an Alt+[ key in VT input mode.
        var altBracket = key.KeyChar == '[' && key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        if (!altBracket && (key.Key != ConsoleKey.Escape || key.Modifiers.HasFlag(ConsoleModifiers.Alt))) return await KeyInput(key, ct);
        var prefix = new StringBuilder(altBracket ? "\u001b[" : "\u001b");
        var deadline = DateTime.UtcNow.AddMilliseconds(altBracket ? 250 : 40);
        var consumed = new List<ConsoleKeyInfo>();
        // SGR mouse events are terminal input, never text or an Escape cancellation.
        while (prefix.Length < 3 && DateTime.UtcNow < deadline)
        {
            if (!_available()) { await Task.Delay(2, ct); continue; }
            var next = ReadKey(); consumed.Add(next); prefix.Append(next.KeyChar);
            if (prefix.Length == 2 && next.KeyChar is '[' or 'O') deadline = DateTime.UtcNow.AddMilliseconds(250);
            if (prefix.Length == 2 && next.KeyChar is not ('[' or 'O')) break;
        }
        if (!altBracket && consumed.Count == 1 && consumed[0].Key == ConsoleKey.Backspace)
        {
            var backspace = consumed[0];
            return new(new ConsoleKeyInfo(backspace.KeyChar, ConsoleKey.Backspace,
                backspace.Modifiers.HasFlag(ConsoleModifiers.Shift), true, backspace.Modifiers.HasFlag(ConsoleModifiers.Control)));
        }
        if (!altBracket && consumed.Count == 1 && consumed[0].KeyChar is not ('[' or 'O' or '\u001b'))
        {
            var next = consumed[0];
            _pending.Enqueue(new(next)); return new(key);
        }
        if (prefix.ToString() == "\u001b[<")
        {
            _discardMouseTail = true;
            while (prefix.Length < 40 && DateTime.UtcNow < deadline)
            {
                if (!_available()) { await Task.Delay(2, ct); continue; }
                var next = ReadKey(); consumed.Add(next); prefix.Append(next.KeyChar);
                if (next.KeyChar is 'M' or 'm')
                {
                    _discardMouseTail = false;
                    var parts = prefix.ToString()[3..^1].Split(';');
                    if (parts.Length == 3 && int.TryParse(parts[0], out var button) && int.TryParse(parts[1], out var x) && int.TryParse(parts[2], out var y))
                    {
                        if ((button & 64) != 0) return new(default, Wheel: (button & 1) == 0 ? 3 : -3, MouseX: x - 1, MouseY: y - 1);
                        if ((button & 3) is 0 or 2) return new(default, Pointer: new(x - 1, y - 1, next.KeyChar == 'm' ? PointerAction.Up : (button & 32) != 0 ? PointerAction.Drag : PointerAction.Down, (button & 4) != 0, (button & 3) == 2 ? PointerButton.Right : PointerButton.Left));
                        return new(default);
                    }
                    break;
                }
            }
            return new(default);
        }
        while (prefix.Length >= 3 && prefix.Length < 32 && prefix[^1] is not (>= '@' and <= '~') && DateTime.UtcNow < deadline)
        {
            if (!_available()) { await Task.Delay(2, ct); continue; }
            var next = ReadKey(); consumed.Add(next); prefix.Append(next.KeyChar);
        }
        if (SequenceKey(prefix.ToString()) is { } decoded) return await KeyInput(decoded, ct);
        if (prefix.ToString() != "\u001b[200~") { foreach (var item in consumed) _pending.Enqueue(new(item)); return new(key); }
        var buffer = new StringBuilder(); var suffix = "";
        while (true)
        {
            while (!_available()) { ct.ThrowIfCancellationRequested(); await Task.Delay(4, ct); }
            var next = ReadKey(); var character = next.Key == ConsoleKey.Escape ? '\u001b' : next.Key == ConsoleKey.Enter ? '\n' : next.KeyChar;
            var sequence = next.KeyChar == '[' && next.Modifiers.HasFlag(ConsoleModifiers.Alt) ? "\u001b[" : character.ToString();
            if (suffix.Length > 0 || sequence[0] == '\u001b')
            {
                suffix += sequence;
                if (suffix == "\u001b[201~") return new(key, (buffer.Length == 0 ? await Task.Run(ReadClipboard, ct) ?? "" : buffer.ToString()).Replace("\r\n", "\n").Replace('\r', '\n'));
                if ("\u001b[201~".StartsWith(suffix, StringComparison.Ordinal)) continue;
                if (buffer.Length < 32000) buffer.Append(suffix); suffix = ""; continue;
            }
            if (buffer.Length < 32000) buffer.Append(character);
        }
    }
}
