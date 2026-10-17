using System.Runtime.InteropServices;

namespace AllAgent.NET.Terminal;

internal static class ConsoleModes
{
    private static uint? _old;
    private static uint? _oldInput;
    private static bool _leftDown;
    private static bool _rightDown;
    public static void EnableVirtualTerminal()
    {
        _leftDown = false;
        _rightDown = false;
        if (!OperatingSystem.IsWindows()) return;
        var handle = GetStdHandle(-11);
        if (GetConsoleMode(handle, out var mode)) { _old ??= mode; SetConsoleMode(handle, mode | 0x0004 | 0x0008); }
        var input = GetStdHandle(-10);
        // Preserve paste delimiters and terminal escape sequences for TerminalKeyReader.
        if (GetConsoleMode(input, out var inputMode)) { _oldInput ??= inputMode; SetConsoleMode(input, (inputMode | 0x0010 | 0x0080 | 0x0200) & ~0x0040u); }
    }
    public static void Restore() { if (OperatingSystem.IsWindows()) { if (_old is { } mode) SetConsoleMode(GetStdHandle(-11), mode); if (_oldInput is { } input) SetConsoleMode(GetStdHandle(-10), input); } _old = _oldInput = null; }
    [StructLayout(LayoutKind.Explicit, Size = 20)] private struct InputRecord
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public short X;
        [FieldOffset(6)] public short Y;
        [FieldOffset(8)] public uint ButtonState;
        [FieldOffset(12)] public uint ControlState;
        [FieldOffset(16)] public uint Flags;
    }
    internal static TerminalInput? ReadPointer()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = GetStdHandle(-10);
        for (var i = 0; i < 64; i++)
        {
            if (!PeekConsoleInput(handle, out var item, 1, out var count) || count == 0) return null;
            if (item.Type == 1) return null;
            // Consume motion/focus events ourselves: Console.KeyAvailable would otherwise
            // discard a wheel event queued behind them.
            if (!ReadConsoleInput(handle, out item, 1, out count) || count == 0) return null;
            if (item.Type == 2 && item.Flags == 4) return new TerminalInput(default, Wheel: (short)(item.ButtonState >> 16) > 0 ? 3 : -3, MouseX: item.X, MouseY: item.Y);
            if (item.Type == 2)
            {
                var right = (item.ButtonState & 2) != 0;
                if (right || _rightDown)
                {
                    var action = !right ? PointerAction.Up : _rightDown && item.Flags == 1 ? PointerAction.Drag : PointerAction.Down;
                    _rightDown = right;
                    return new TerminalInput(default, Pointer: new(item.X, item.Y, action, (item.ControlState & 0x10) != 0, PointerButton.Right));
                }
                var down = (item.ButtonState & 1) != 0;
                if (down || _leftDown)
                {
                    var action = !down ? PointerAction.Up : _leftDown && item.Flags == 1 ? PointerAction.Drag : PointerAction.Down;
                    _leftDown = down;
                    return new TerminalInput(default, Pointer: new(item.X, item.Y, action, (item.ControlState & 0x10) != 0));
                }
            }
        }
        return new TerminalInput(default); // Yield before inspecting more native input.
    }
    [DllImport("kernel32.dll", EntryPoint = "PeekConsoleInputW")] private static extern bool PeekConsoleInput(IntPtr handle, out InputRecord record, uint length, out uint count);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW")] private static extern bool ReadConsoleInput(IntPtr handle, out InputRecord record, uint length, out uint count);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
