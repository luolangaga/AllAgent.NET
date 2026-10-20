namespace AllAgent.NET.CodeGraph;

public sealed record CodeGraphStatus(
    string Workspace,
    bool Indexed,
    string? IndexPath,
    long? IndexBytes,
    DateTime? IndexUpdatedAt,
    int Files,
    int Symbols,
    int References,
    string? Detail,
    string? Error);

public sealed record ExploreResult(string Output, bool Truncated, string? Error, DateTime? SyncedAt);

public enum IndexMode { Init, Sync, Rebuild }

public sealed record CodeGraphOperation(
    string Id,
    string Kind,
    string Workspace,
    string State,
    string? Output = null,
    string? Error = null,
    DateTime StartedAt = default,
    DateTime? FinishedAt = null);

public sealed record CodeGraphStatusQuery(string? Workspace = null);

public sealed record CodeGraphIndexRequest(string? Workspace, string Mode = "init");

public sealed record CodeGraphOperationQuery(string Id);

public sealed record CodeGraphSettings(bool AutoSync);

public sealed record CodeGraphSettingsSave(bool AutoSync);
