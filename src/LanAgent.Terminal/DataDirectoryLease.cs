namespace LanAgent.Terminal;

/// <summary>One terminal writer per data directory. Read-only task inspection does not acquire this lease.</summary>
internal sealed class DataDirectoryLease : IDisposable
{
    private readonly FileStream _lease;
    public DataDirectoryLease(string directory)
    {
        Directory.CreateDirectory(directory);
        try { _lease = new FileStream(Path.Combine(directory, "terminal.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("该数据目录已有 LanAgent 终端正在使用；请在原界面操作、关闭后重试，或用 --data-dir 选择其他目录。tasks list/show 可同时查看任务。", ex); }
        _lease.SetLength(0); using var writer = new StreamWriter(_lease, leaveOpen: true); writer.Write(Environment.ProcessId); writer.Flush();
    }
    public void Dispose() => _lease.Dispose();
}
