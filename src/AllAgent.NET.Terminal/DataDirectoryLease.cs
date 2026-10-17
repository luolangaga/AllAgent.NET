namespace AllAgent.NET.Terminal;

/// <summary>One terminal writer per data directory. Read-only task inspection does not acquire this lease.</summary>
internal sealed class DataDirectoryLease : IDisposable
{
    private readonly FileStream _lease;
    public DataDirectoryLease(string directory)
    {
        Directory.CreateDirectory(directory);
        try { _lease = new FileStream(Path.Combine(directory, "terminal.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("该数据目录已有 AllAgent 终端正在使用；请在原界面操作、关闭后重试，或用 --data-dir 选择其他目录。tasks list/show 可同时查看任务。", ex); }
        _lease.SetLength(0); using var writer = new StreamWriter(_lease, leaveOpen: true); writer.Write(Environment.ProcessId); writer.Flush();
    }
    public static void EnsureNoLegacyWriter(string directory)
    {
        var path = Path.Combine(directory, "terminal.lock");
        if (!File.Exists(path)) return;
        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException ex)
        { throw new InvalidOperationException("旧版 AllAgent 仍在使用该目录，请先结束旧进程再重启；也可用 allagent attach 临时接入旧后台。", ex); }
    }
    public void Dispose() => _lease.Dispose();
}
