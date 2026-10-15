using System.Security.Cryptography;
using System.Text;

namespace LanAgent.Core.Data;

/// <summary>Synchronous, thread-affine lock covering a complete disk read/modify/commit.</summary>
internal sealed class StorageTransaction : IDisposable
{
    private readonly Mutex _mutex;
    public StorageTransaction(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) fullPath = fullPath.ToUpperInvariant();
        _mutex = new Mutex(false, "lanagent-storage-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath))));
        try
        {
            try { if (!_mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new IOException("数据保存锁等待超时。"); }
            catch (AbandonedMutexException) { }
        }
        catch { _mutex.Dispose(); throw; }
    }
    public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
