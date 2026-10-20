using System.Text;

namespace AllAgent.NET.Terminal;

internal enum PointerAction { Down, Drag, Up }
internal enum PointerButton { Left, Right }
internal readonly record struct TerminalPointer(int X, int Y, PointerAction Action, bool Extend = false, PointerButton Button = PointerButton.Left);
internal readonly record struct TerminalInput(ConsoleKeyInfo Key, string? Paste = null, int Wheel = 0, TerminalPointer? Pointer = null, int? MouseX = null, int? MouseY = null);

/// <summary>Single, cancellable key reader. Escape and pasted newlines cannot be confused with submit commands.</summary>
internal sealed class TerminalKeyReader
{
    private readonly Queue<TerminalInput> _pending = new();
    private readonly Queue<ConsoleKeyInfo> _replay = new();
    private readonly Func<bool> _available;
    private readonly Func<ConsoleKeyInfo> _read;
    private readonly Func<string?> _clipboard;
    private readonly bool _native;
    private readonly bool _burst;
    private TerminalInput? _deferred;
    private bool _discardMouseTail;
    public bool HasInput
    {
        get
        {
            if (_deferred != null || _pending.Count > 0 || _replay.Count > 0) return true;
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
    public TerminalKeyReader(Func<bool>? available = null, Func<ConsoleKeyInfo>? read = null, Func<string?>? clipboard = null, bool? detectPastes = null)
    {
        _native = available == null;
        _available = available ?? (() => Console.KeyAvailable);
        _read = read ?? (() => Console.ReadKey(true));
        _clipboard = clipboard ?? ClipboardText.Read;
        // Bracketed paste only exists in modern terminals. Classic conhost delivers a paste
        // as one fast block of ordinary key records, so the native reader recognizes that
        // block itself; injected readers stay deterministic unless a test opts in.
        _burst = detectPastes ?? _native;
    }
    private bool Available() => _replay.Count > 0 || _available();
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
        var key = _replay.Count > 0 ? _replay.Dequeue() : _read();
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
        var body = sequence[2..];
        // A stalled read can leave a bare prefix ("\u001b["); treat it as an unfinished sequence, not a crash.
        if (body.Length == 0) return null;
        var ending = body[^1]; var parts = body[..^1].Split(';');
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
                while (!Available()) { ct.ThrowIfCancellationRequested(); await Task.Delay(2, ct); }
                var tail = ReadKey().KeyChar;
                if (tail is 'M' or 'm') { _discardMouseTail = false; return new(default); }
                if (!(char.IsAsciiDigit(tail) || tail is ';')) { _discardMouseTail = false; return await KeyInput(new ConsoleKeyInfo(tail, (ConsoleKey)0, false, false, false), ct); }
            }
        }
        if (_pending.TryDequeue(out var pending)) return pending;
        while (!HasInput) { ct.ThrowIfCancellationRequested(); await Task.Delay(16, ct); }
        if (_pending.TryDequeue(out var mouse)) return mouse;
        var key = ReadKey();
        if (await PasteBurstAsync(key, ct) is { } burst) return burst;
        // Windows Console.ReadKey folds ESC+[ into an Alt+[ key in VT input mode.
        var altBracket = key.KeyChar == '[' && key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        if (!altBracket && (key.Key != ConsoleKey.Escape || key.Modifiers.HasFlag(ConsoleModifiers.Alt))) return await KeyInput(key, ct);
        var prefix = new StringBuilder(altBracket ? "\u001b[" : "\u001b");
        var deadline = DateTime.UtcNow.AddMilliseconds(altBracket ? 250 : 40);
        var consumed = new List<ConsoleKeyInfo>();
        // SGR mouse events are terminal input, never text or an Escape cancellation.
        while (prefix.Length < 3 && DateTime.UtcNow < deadline)
        {
            if (!Available()) { await Task.Delay(2, ct); continue; }
            var next = ReadKey(); consumed.Add(next); prefix.Append(next.KeyChar);
            if (prefix.Length == 2 && next.KeyChar is '[' or 'O') deadline = DateTime.UtcNow.AddMilliseconds(1500);
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
                if (!Available()) { await Task.Delay(2, ct); continue; }
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
            // A truncated mouse report must never complete a cancelled read.
            ct.ThrowIfCancellationRequested();
            return new(default);
        }
        while (prefix.Length >= 3 && prefix.Length < 32 && prefix[^1] is not (>= '@' and <= '~') && DateTime.UtcNow < deadline)
        {
            if (!Available()) { await Task.Delay(2, ct); continue; }
            var next = ReadKey(); consumed.Add(next); prefix.Append(next.KeyChar);
        }
        if (SequenceKey(prefix.ToString()) is { } decoded) return await KeyInput(decoded, ct);
        if (prefix.ToString() != "\u001b[200~") { foreach (var item in consumed) _pending.Enqueue(new(item)); return new(key); }
        var buffer = new StringBuilder(); var suffix = ""; var truncated = false;
        while (true)
        {
            while (!Available()) { ct.ThrowIfCancellationRequested(); await Task.Delay(4, ct); }
            var next = ReadKey(); var character = next.Key == ConsoleKey.Escape ? '\u001b' : next.Key == ConsoleKey.Enter ? '\n' : next.KeyChar;
            var sequence = next.KeyChar == '[' && next.Modifiers.HasFlag(ConsoleModifiers.Alt) ? "\u001b[" : character.ToString();
            if (suffix.Length > 0 || sequence[0] == '\u001b')
            {
                suffix += sequence;
                if (suffix == "\u001b[201~") return new(key, FinishPaste(buffer, truncated, buffer.Length == 0 ? await Task.Run(ReadClipboard, ct) : null));
                if ("\u001b[201~".StartsWith(suffix, StringComparison.Ordinal)) continue;
                if (buffer.Length < 32000) buffer.Append(suffix); else truncated = true;
                suffix = ""; continue;
            }
            if (buffer.Length < 32000) buffer.Append(character); else truncated = true;
        }
    }
    // Classic conhost has no bracketed paste: a pasted block arrives as a burst of ordinary
    // key records, including Enter for every newline. Recognizing that burst keeps newlines
    // inside the draft and inserts the text in one step instead of one render per character.
    private const int BurstLookaheadMilliseconds = 30;
    private const int BurstQuietMilliseconds = 12;
    private static bool IsPasteText(ConsoleKeyInfo key) =>
        !key.Modifiers.HasFlag(ConsoleModifiers.Control) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt) &&
        (key.Key is ConsoleKey.Enter or ConsoleKey.Tab || !char.IsControl(key.KeyChar));
    private static bool IsPastePrint(ConsoleKeyInfo key) =>
        IsPasteText(key) && key.Key is not (ConsoleKey.Enter or ConsoleKey.Tab) && key.KeyChar >= ' ';
    private static char PasteCharacter(ConsoleKeyInfo key) => key.Key == ConsoleKey.Enter ? '\n' : key.Key == ConsoleKey.Tab ? '\t' : key.KeyChar;
    private async Task<TerminalInput?> PasteBurstAsync(ConsoleKeyInfo first, CancellationToken ct)
    {
        if (!_burst || first.Key == ConsoleKey.Escape || !IsPasteText(first) || !Available()) return null;
        var lookahead = new List<ConsoleKeyInfo> { first };
        // A typed key is never followed by two more buffered keys; a pasted block is.
        // Newlines only join the burst once enough printable text proves it is a paste.
        var deadline = Environment.TickCount64 + BurstLookaheadMilliseconds;
        while (lookahead.Count < 3 && Available() && Environment.TickCount64 < deadline) lookahead.Add(ReadKey());
        if (lookahead.Count(IsPastePrint) < 2) { foreach (var key in lookahead.Skip(1)) _replay.Enqueue(key); return null; }
        var buffer = new StringBuilder(); var truncated = false; var multiline = false; var index = 0;
        var quiet = Environment.TickCount64 + BurstQuietMilliseconds;
        while (true)
        {
            ConsoleKeyInfo next;
            if (index < lookahead.Count) next = lookahead[index++];
            else if (Available()) next = ReadKey();
            else if (Environment.TickCount64 < quiet) { await Task.Delay(2, ct); continue; }
            else break;
            if (next.Key == ConsoleKey.Enter && next.Modifiers == 0 && !multiline)
            {
                // A trailing Enter after a single line is the user's submit (including scripted
                // input). Inside multiline text it is paste data and must never send the draft.
                var wait = Environment.TickCount64 + BurstQuietMilliseconds;
                while (index >= lookahead.Count && !Available() && Environment.TickCount64 < wait) await Task.Delay(2, ct);
                if (index >= lookahead.Count && !Available()) { _replay.Enqueue(next); break; }
            }
            if (!IsPasteText(next)) { _replay.Enqueue(next); break; }
            var character = PasteCharacter(next); multiline |= character == '\n';
            if (buffer.Length < PasteCapacity) buffer.Append(character); else truncated = true;
            quiet = Environment.TickCount64 + BurstQuietMilliseconds;
        }
        return new(first, FinishPaste(buffer, truncated, null));
    }
    // Silent truncation looked like a lost paste; the marker makes the loss visible in the draft.
    private const int PasteCapacity = 32000;
    private static string FinishPaste(StringBuilder buffer, bool truncated, string? fallback) =>
        ((buffer.Length == 0 ? fallback ?? "" : buffer.ToString()) + (truncated ? "\n[粘贴内容超过 " + PasteCapacity + " 字符，已截断]" : ""))
            .Replace("\r\n", "\n").Replace('\r', '\n');
}
