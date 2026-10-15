using System.Text.Json;
using LanAgent.Core.Data;
using LanAgent.Core.Models;

namespace LanAgent.Core.Services;

public sealed partial class WorkspaceCheckpoints
{
    internal string RevertJournalPath => _storage.Resolve("revert.pending.json");
    internal sealed record RevertFlag(Guid Id, bool Before, bool After);
    internal sealed record RevertJournal(Guid Session, string HistoryHash, SessionRevertState? Before, SessionRevertState? After,
        IReadOnlyList<RevertFile> Writes, IReadOnlyList<RevertFlag> Flags);
    private void EnsureNoRevertJournal()
    {
        if (File.Exists(RevertJournalPath)) throw new IOException("有未完成的撤回恢复日志，请先恢复事务：" + RevertJournalPath);
    }
    public IReadOnlyList<WorkspaceCheckpoint> RevertRange(DataStore store, Guid session, Guid message, bool preview = false)
    {
        lock (_gate)
        {
            if (_active != null) throw new InvalidOperationException("任务运行中不能撤回历史。");
            EnsureNoRevertJournal();
            var conversation = store.GetConversation(session) ?? throw new ArgumentException("会话不存在。");
            var original = conversation.Revert; ValidateWorkspace(original);
            var boundary = conversation.Messages.FindIndex(m => m.Id == message && m.Role == "user" && !m.IsInternal);
            if (boundary < 0) throw new ArgumentException("用户消息不存在。");
            var target = conversation.Messages.Take(boundary).ToList(); var expected = conversation.Messages;
            var range = new List<WorkspaceCheckpoint>();
            foreach (var checkpoint in List(session).Where(c => !conversation.InvalidatedCheckpoints.Contains(c.Id)))
            {
                if (!Same(checkpoint.AfterMessages, expected)) continue;
                range.Add(checkpoint); expected = checkpoint.BeforeMessages;
                if (Same(expected, target)) break;
                if (expected.Count < target.Count) throw new InvalidOperationException("所选消息位于任务内部，尚无精确文件检查点，未撤回。");
            }
            if (range.Count == 0 || !Same(expected, target)) throw new InvalidOperationException("找不到连续且精确匹配消息的检查点，未撤回。");
            var files = new Dictionary<string, RevertFile>(StringComparer.OrdinalIgnoreCase);
            var previous = original?.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase) ?? new();
            foreach (var checkpoint in range)
            {
                foreach (var file in checkpoint.Files)
                {
                    ValidateFile(new(file.Path, file.Before, file.After));
                    if (!files.TryGetValue(file.Path, out var planned))
                    {
                        var before = previous.TryGetValue(file.Path, out var staged) ? staged.Before : Read(file.Path);
                        planned = new(file.Path, before, before);
                    }
                    if (!Equal(planned.After, file.Completed ? file.After : file.Before)) throw new IOException("文件状态冲突，未撤回：" + file.Path);
                    files[file.Path] = planned with { After = file.Before };
                }
            }
            var next = new SessionRevertState(message, _workspace.Root, range.Select(c => c.Id).ToArray(), files.Values.ToArray());
            if (!preview) ChangeRevert(store, conversation, original, next); return range;
        }
    }
    public void ClearRevert(DataStore store, Guid session)
    {
        lock (_gate)
        {
            if (_active != null) throw new InvalidOperationException("任务运行中不能恢复撤回。");
            EnsureNoRevertJournal(); var conversation = store.GetConversation(session) ?? throw new ArgumentException("会话不存在。");
            if (conversation.Revert is not { } before) return;
            ValidateWorkspace(before); ChangeRevert(store, conversation, before, null);
        }
    }
    private void ChangeRevert(DataStore store, Conversation conversation, SessionRevertState? before, SessionRevertState? after)
    {
        var oldFiles = before?.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase) ?? new();
        var newFiles = after?.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase) ?? new();
        var writes = new List<RevertFile>();
        foreach (var path in oldFiles.Keys.Concat(newFiles.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var old = oldFiles.GetValueOrDefault(path); var next = newFiles.GetValueOrDefault(path);
            var current = old != null ? old.After : next!.Before;
            var desired = next != null ? next.After : old!.Before;
            var write = new RevertFile(path, current, desired); ValidateFile(write);
            if (!Equal(Read(path), current)) throw new IOException("文件已被其他操作修改，未改动任何文件：" + path);
            writes.Add(write);
        }
        var ids = (before?.Checkpoints ?? []).Concat(after?.Checkpoints ?? []).Distinct().ToArray();
        var checkpoints = List(conversation.Id).ToDictionary(c => c.Id);
        var flags = ids.Select(id => new RevertFlag(id, checkpoints[id].Undone, after?.Checkpoints.Contains(id) == true)).ToArray();
        var journal = new RevertJournal(conversation.Id, DataStore.RevertHistoryHash(conversation.Messages), before, after, writes, flags);
        WriteJournal(journal);
        try
        {
            ApplyWrites(writes, forward: true);
            foreach (var flag in flags) { checkpoints[flag.Id].Undone = flag.After; Save(checkpoints[flag.Id]); }
            conversation.Revert = after; store.UpdateConversation(conversation);
            File.Delete(RevertJournalPath);
        }
        catch
        {
            // Disk state decides rollback versus completion, including a failed post-write verification.
            if (!RecoverRevert(store)) throw;
        }
    }
    public bool RecoverRevert(DataStore store)
    {
        lock (_gate)
        {
            if (!File.Exists(RevertJournalPath)) return false;
            if (_active != null) throw new InvalidOperationException("任务运行中不能恢复撤回事务。");
            var journal = JsonSerializer.Deserialize<RevertJournal>(File.ReadAllText(RevertJournalPath)) ?? throw new IOException("撤回日志无效。");
            ValidateWorkspace(journal.Before); ValidateWorkspace(journal.After);
            foreach (var file in journal.Writes)
            {
                ValidateFile(file); var actual = Read(file.Path);
                if (!Equal(actual, file.Before) && !Equal(actual, file.After)) throw new IOException("恢复日志中的文件被外部改动，未覆盖：" + file.Path);
            }
            var persisted = store.PersistedRevert(journal.Session);
            if (persisted.HistoryHash != journal.HistoryHash) throw new IOException("会话历史在撤回恢复期间已发生变化。");
            var committed = Same(persisted.State, journal.After);
            if (!committed && !Same(persisted.State, journal.Before)) throw new IOException("会话撤回状态与恢复日志不一致。");
            var checkpoints = List(journal.Session).ToDictionary(c => c.Id);
            if (journal.Flags.Any(flag => !checkpoints.ContainsKey(flag.Id))) throw new IOException("恢复日志的检查点不存在。");
            ApplyWrites(journal.Writes, committed);
            foreach (var flag in journal.Flags) { checkpoints[flag.Id].Undone = committed ? flag.After : flag.Before; Save(checkpoints[flag.Id]); }
            var conversation = store.GetConversation(journal.Session) ?? throw new IOException("恢复日志的会话不存在。");
            conversation.Revert = committed ? journal.After : journal.Before; store.UpdateConversation(conversation);
            File.Delete(RevertJournalPath); return committed;
        }
    }
    private void ApplyWrites(IReadOnlyList<RevertFile> files, bool forward)
    {
        foreach (var file in files)
        {
            var bytes = forward ? file.After : file.Before;
            var actual = Read(file.Path);
            if (Equal(actual, bytes)) continue;
            if (!Equal(actual, file.Before) && !Equal(actual, file.After)) throw new IOException("文件在事务执行期间被其他操作改动：" + file.Path);
            var path = _workspace.Resolve(file.Path);
            if (bytes == null) File.Delete(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temp, bytes); _workspace.Resolve(file.Path); File.Move(temp, path, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
    }
    private void ValidateWorkspace(SessionRevertState? state)
    {
        if (state != null && !string.Equals(state.Workspace, _workspace.Root, StringComparison.OrdinalIgnoreCase)) throw new IOException("撤回状态属于其他工作区。");
    }
    private void ValidateFile(RevertFile file)
    {
        _workspace.Resolve(file.Path);
    }
    private void WriteJournal(RevertJournal journal)
    {
        var temp = RevertJournalPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, journal); stream.Flush(true); }
        File.Move(temp, RevertJournalPath, true);
    }
    private static bool Same<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
