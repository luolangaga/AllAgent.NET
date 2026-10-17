using System.ComponentModel;
using System.Runtime.InteropServices;
using FlaUI.Core.WindowsAPI;

namespace AllAgent.NET.ComputerUse;

/// <summary>Insert complete down/up pairs in a single Windows call, including Unicode text.
/// A killed Worker cannot leave a half-submitted modifier chord behind.</summary>
internal static class NativeInput
{
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort Key; public ushort Scan; public uint Flags; public uint Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X; public int Y; public uint Data; public uint Flags; public uint Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    public static void Chord(VirtualKeyShort[] keys)
    {
        EnsureModifiersReleased();
        Send(BuildChord(keys));
    }
    private static Input[] BuildChord(VirtualKeyShort[] keys) => keys.Select(key => Key((ushort)key, false)).Concat(keys.Reverse().Select(key => Key((ushort)key, true))).ToArray();
    public static void Text(string text)
    {
        EnsureModifiersReleased();
        Send(BuildText(text));
    }
    private static Input[] BuildText(string text)
    {
        var input = new List<Input>(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n' or '\t')
            {
                var key = c == '\t' ? VirtualKeyShort.TAB : VirtualKeyShort.RETURN;
                input.Add(Key((ushort)key, false)); input.Add(Key((ushort)key, true));
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else
            {
                input.Add(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyInput { Scan = c, Flags = 4 } } });
                input.Add(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyInput { Scan = c, Flags = 6 } } });
            }
        }
        return input.ToArray();
    }
    private static Input Key(ushort code, bool up) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyInput { Key = code,
        Flags = (up ? 2u : 0u) | (code is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0xA3 or 0xA5 ? 1u : 0u) } } };
    private static void EnsureModifiersReleased()
    {
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) throw new InvalidOperationException("A modifier key is held in the target desktop. No input was injected; do not alter the user's held keys.");
    }
    private static void Send(Input[] input)
    {
        if (input.Length == 0) return;
        var sent = SendInput((uint)input.Length, input, Marshal.SizeOf<Input>());
        if (sent != input.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows accepted {sent}/{input.Length} input events. Inspect the UI before repeating; UIPI or a noninteractive desktop may block input.");
    }
}
