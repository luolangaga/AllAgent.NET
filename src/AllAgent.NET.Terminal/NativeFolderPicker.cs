using System.Runtime.InteropServices;

namespace AllAgent.NET.Terminal;

/// <summary>
/// Native folder picker for the browser workspace. Prefers the modern Windows dialog
/// (IFileOpenDialog with FOS_PICKFOLDERS, the Explorer-style picker) and falls back to
/// the legacy shell browser when the COM dialog cannot be created. Everything runs on a
/// dedicated STA thread, and the thread body never throws: an unhandled exception in a
/// raw thread would end the whole host process.
/// </summary>
internal static class NativeFolderPicker
{
    /// <summary>Reason the last pick failed, for host diagnostics.</summary>
    public static string? LastError { get; private set; }

    public static string? Pick(string title)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? selected = null;
        var thread = new Thread(() =>
        {
            try
            {
                selected = Show(title);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                selected = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        return string.IsNullOrEmpty(selected) ? null : selected;
    }

    private static string? Show(string title)
    {
        _ = CoInitializeEx(IntPtr.Zero, ApartmentThreaded);
        try
        {
            // GUI mode runs without a console; the picker is owned by the foreground window there.
            var owner = GetConsoleWindow();
            if (owner == IntPtr.Zero) owner = GetForegroundWindow();
            if (owner != IntPtr.Zero) SetForegroundWindow(owner);
            return TryModern(title, owner) ?? Legacy(title, owner);
        }
        finally { CoUninitialize(); }
    }

    // ── Modern dialog ─────────────────────────────────────────────────────────

    private const uint FilePickerFolders = 0x00000020;
    private const uint FilePickerForceFileSystem = 0x00000040;
    private const uint FilePickerPathMustExist = 0x00000800;
    private const uint FilePickerNoChangeDir = 0x00000008;

    private enum DisplayNameKind : uint { FileSystemPath = 0x80058000 }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    // One flat declaration: COM vtable order must match shobjidl_core.h exactly.
    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        void Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr filterSpec);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem selection);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int access);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid id, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(DisplayNameKind kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(out uint attributes);
        void Compare(IShellItem other, out int order);
    }

    private static string? TryModern(string title, IntPtr owner)
    {
        IFileOpenDialog? dialog = null;
        try
        {
            dialog = (IFileOpenDialog)new FileOpenDialog();
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FilePickerFolders | FilePickerForceFileSystem | FilePickerPathMustExist | FilePickerNoChangeDir);
            dialog.SetTitle(title);
            dialog.SetOkButtonLabel("选择此文件夹");
            // Cancel surfaces as a non-zero HRESULT from Show, so it is not an error.
            try { dialog.Show(owner); }
            catch (COMException) { return null; }
            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(DisplayNameKind.FileSystemPath, out var path);
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
        finally
        {
            if (dialog != null) Marshal.ReleaseComObject(dialog);
        }
    }

    // ── Legacy fallback ───────────────────────────────────────────────────────

    private const uint ReturnOnlyFileSystemDirectories = 0x00000001;
    private const uint AllowNewDialogStyle = 0x00000040;
    private const uint IncludeEditBox = 0x00000010;
    private const int MaxPath = 260;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr Owner;
        public IntPtr Root;
        public IntPtr DisplayName;
        public string Title;
        public uint Flags;
        public IntPtr Callback;
        public IntPtr Parameter;
        public int Image;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BrowseInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr idList, [Out] char[] path);

    private static string? Legacy(string title, IntPtr owner)
    {
        var display = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        try
        {
            Marshal.WriteInt16(display, 0, 0);
            var info = new BrowseInfo
            {
                Owner = owner,
                DisplayName = display,
                Title = title,
                Flags = ReturnOnlyFileSystemDirectories | AllowNewDialogStyle | IncludeEditBox,
            };
            var idList = SHBrowseForFolder(ref info);
            if (idList == IntPtr.Zero) return null;
            try
            {
                var buffer = new char[MaxPath];
                if (SHGetPathFromIDList(idList, buffer)) return new string(buffer).TrimEnd('\0');
                LastError = "无法读取所选目录路径。";
                return null;
            }
            finally { Marshal.FreeCoTaskMem(idList); }
        }
        finally { Marshal.FreeHGlobal(display); }
    }

    // ── Win32 ─────────────────────────────────────────────────────────────────

    private const uint ApartmentThreaded = 0x2;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}