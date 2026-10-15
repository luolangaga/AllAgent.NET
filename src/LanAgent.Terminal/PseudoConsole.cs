using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LanAgent.Terminal;

/// <summary>Owns the real interactive host, independently of the terminal clients.</summary>
internal sealed class PseudoConsole : IDisposable
{
    private IntPtr _console, _process;
    public FileStream Input { get; private set; } = null!;
    public FileStream Output { get; private set; } = null!;
    public int ProcessId { get; private set; }

    public PseudoConsole(string command, short width, short height)
    {
        IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero, outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        try
        {
            Check(CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0));
            Check(CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0));
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new(width, height), inputRead, outputWrite, 0, out _console));
            Input = new(new SafeFileHandle(inputWrite, true), FileAccess.Write, 1); inputWrite = IntPtr.Zero;
            Output = new(new SafeFileHandle(outputRead, true), FileAccess.Read); outputRead = IntPtr.Zero;
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016, _console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
            // Prevent redirected broker/test stdio handles from overriding the ConPTY console.
            var startup = new StartupInfoEx { StartupInfo = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x00000100,
                Input = new IntPtr(-1), Output = new IntPtr(-1), Error = new IntPtr(-1) }, Attributes = attributes };
            Check(CreateProcess(null, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, null, ref startup, out var process));
            _process = process.Process; ProcessId = process.ProcessId; CloseHandle(process.Thread);
        }
        catch { Dispose(); throw; }
        finally
        {
            foreach (var handle in new[] { inputRead, inputWrite, outputRead, outputWrite }) if (handle != IntPtr.Zero) CloseHandle(handle);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
        }
    }
    public void Resize(short width, short height) => Marshal.ThrowExceptionForHR(ResizePseudoConsole(_console, new(width, height)));
    public bool HasExited => WaitForSingleObject(_process, 0) == 0;
    public int ExitCode { get { Check(GetExitCodeProcess(_process, out var code)); return unchecked((int)code); } }
    public void CloseSession()
    {
        var console = Interlocked.Exchange(ref _console, IntPtr.Zero);
        if (console != IntPtr.Zero) ClosePseudoConsole(console);
    }
    public void Dispose()
    {
        CloseSession();
        Input?.Dispose(); Output?.Dispose();
        if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
    }
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, CharactersX, CharactersY, Fill, Flags;
        public short Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr security, int size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
}
