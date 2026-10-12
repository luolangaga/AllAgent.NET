namespace LanAgent.Core.Models;

public sealed record RevertFile(string Path, byte[]? Before, byte[]? After);
public sealed record SessionRevertState(Guid MessageId, string Workspace, IReadOnlyList<Guid> Checkpoints, IReadOnlyList<RevertFile> Files);
