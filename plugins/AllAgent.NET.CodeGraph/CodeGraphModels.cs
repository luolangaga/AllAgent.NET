namespace AllAgent.NET.CodeGraph;

public sealed record CodeGraphStatus(
    string Workspace,
    bool Installed,
    string? Path,
    string? Version,
    bool Indexed,
    string? IndexPath,
    long? IndexBytes,
    DateTime? IndexUpdatedAt,
    string? Detail,
    string? Error);

public sealed record ExploreResult(string Output, bool Truncated, string? Error, DateTime? SyncedAt);

public sealed record InstallResult(bool Installed, string? Path, string? Version, string Output, string? Error);

public enum IndexMode { Init, Sync, Rebuild }

public sealed record CodeGraphOperation(
    string Id,
    string Kind,
    string Workspace,
    string State,
    string? Output = null,
    string? Error = null,
    string? LogPath = null,
    DateTime StartedAt = default,
    DateTime? FinishedAt = null);

public sealed record CodeGraphStatusQuery(string? Workspace = null);

public sealed record CodeGraphIndexRequest(string? Workspace, string Mode = "init");

public sealed record CodeGraphOperationQuery(string Id);

public sealed record CodeGraphSettings(string? Path, bool AutoSync, bool Telemetry);

public sealed record CodeGraphSettingsSave(string? Path, bool AutoSync, bool Telemetry);
