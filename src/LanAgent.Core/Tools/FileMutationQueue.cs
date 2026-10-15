namespace LanAgent.Core.Tools;

// Shared by all tool sets: child agents and separate registries use the same queue.
internal static class FileMutationQueue
{
    private sealed class Entry { public readonly SemaphoreSlim Gate = new(1, 1); public int Users; }
    private static readonly Dictionary<string, Entry> Entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public static async Task<IDisposable> EnterAsync(string path, CancellationToken ct)
    {
        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(path, out entry!)) Entries.Add(path, entry = new());
            entry.Users++;
        }
        try { await entry.Gate.WaitAsync(ct); }
        catch { Release(path, entry, false); throw; }
        return new Lease(() => Release(path, entry, true));
    }
    private static void Release(string path, Entry entry, bool acquired)
    {
        lock (Entries)
        {
            if (acquired) entry.Gate.Release();
            if (--entry.Users == 0) { Entries.Remove(path); entry.Gate.Dispose(); }
        }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
