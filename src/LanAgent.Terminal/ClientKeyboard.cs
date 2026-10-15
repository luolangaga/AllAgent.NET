using System.Runtime.InteropServices;
using System.Text;

namespace LanAgent.Terminal;

/// <summary>Preserves Windows key metadata instead of losing modifiers through ReadFile.</summary>
internal static class ClientKeyboard
{
    [StructLayout(LayoutKind.Explicit, Size = 20)] private struct Record
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public int Down;
        [FieldOffset(8)] public ushort Repeat;
        [FieldOffset(10)] public ushort VirtualKey;
        [FieldOffset(12)] public ushort Scan;
        [FieldOffset(14)] public ushort Character;
        [FieldOffset(16)] public uint KeyControl;
        [FieldOffset(4)] public short X;
        [FieldOffset(6)] public short Y;
        [FieldOffset(8)] public uint Buttons;
        [FieldOffset(12)] public uint MouseControl;
        [FieldOffset(16)] public uint MouseFlags;
    }
    public static byte[] Read(out bool detach)
    {
        detach = false;
        if (!ReadConsoleInput(GetStdHandle(-10), out var record, 1, out var count) || count == 0) throw new IOException("读取终端键盘失败。");
        if (record.Type == 1)
        {
            detach = record.Down != 0 && (record.Character == 29 || record.VirtualKey == 221 && (record.KeyControl & 12) != 0);
            // The documented win32-input-mode sequence is understood by ConPTY.
            return Encoding.ASCII.GetBytes($"\u001b[{record.VirtualKey};{record.Scan};{record.Character};{(record.Down != 0 ? 1 : 0)};{record.KeyControl};{record.Repeat}_");
        }
        if (record.Type != 2) return [];
        var modifiers = ((record.MouseControl & 16) != 0 ? 4 : 0) | ((record.MouseControl & 3) != 0 ? 8 : 0) | ((record.MouseControl & 12) != 0 ? 16 : 0);
        var button = record.MouseFlags == 4 ? (short)(record.Buttons >> 16) > 0 ? 64 : 65
            : (record.Buttons & 1) != 0 ? 0 : (record.Buttons & 2) != 0 ? 2 : 3;
        if (record.MouseFlags == 1) button |= 32;
        return Encoding.ASCII.GetBytes($"\u001b[<{button | modifiers};{record.X + 1};{record.Y + 1}{(record.Buttons == 0 && record.MouseFlags != 4 ? 'm' : 'M')}");
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)] private static extern bool ReadConsoleInput(IntPtr handle, out Record record, uint length, out uint count);
}
