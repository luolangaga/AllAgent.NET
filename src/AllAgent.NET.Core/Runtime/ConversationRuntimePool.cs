namespace AllAgent.NET.Core.Runtime;

/// <summary>All views of one data directory share workers and disclosures. Each host owns a lease;
/// closing a browser view cannot dispose a terminal's or another workspace's active runtime.</summary>
internal static class ConversationRuntimePool
{
    private sealed class Entry
    {
        internal readonly ConversationRuntimeFactory Factory = new();
        internal int References;
    }
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    internal static Lease Acquire(string dataDirectory)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var entry)) Entries.Add(key, entry = new());
            entry.References++;
            return new(key, entry.Factory);
        }
    }

    internal sealed class Lease(string key, ConversationRuntimeFactory factory) : IAsyncDisposable
    {
        internal ConversationRuntimeFactory Factory { get; } = factory;
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var last = false;
            lock (Gate)
                if (Entries.TryGetValue(key, out var entry) && ReferenceEquals(entry.Factory, Factory) && --entry.References == 0)
                { Entries.Remove(key); last = true; }
            if (last) await Factory.DisposeAsync();
        }
    }
}
