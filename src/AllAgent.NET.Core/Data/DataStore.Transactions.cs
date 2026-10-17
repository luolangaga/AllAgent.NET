namespace AllAgent.NET.Core.Data;

public partial class DataStore
{
    private readonly HashSet<Guid> _externallyChanged = [];

    private StorageTransaction BeginTransaction()
    {
        var transaction = new StorageTransaction(_filePath);
        try { RefreshFromDisk(); return transaction; }
        catch { transaction.Dispose(); throw; }
    }

    private void RefreshFromDisk()
    {
        // Load under the process lock; immutable revisions that haven't changed reuse
        // their objects, preserving references held by the running agent.
        if (!File.Exists(_filePath) && !File.Exists(_bakPath)) return;
        var previous = new Dictionary<Guid, ConversationFile>(_conversationFiles);
        Load();
        foreach (var reference in _data.ConversationFiles)
            if (previous.TryGetValue(reference.Id, out var before) && before != reference) _externallyChanged.Add(reference.Id);
        _conversationFiles.Clear();
        foreach (var reference in _data.ConversationFiles) _conversationFiles[reference.Id] = reference;
    }

    /// <summary>Refresh at navigation/management boundaries, never during rendering.</summary>
    public void Refresh()
    {
        _lock.EnterWriteLock();
        try { using var transaction = BeginTransaction(); }
        finally { _lock.ExitWriteLock(); }
    }
}
