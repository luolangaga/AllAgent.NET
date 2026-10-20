namespace AllAgent.NET.Core.Extensions;

/// <summary>Host-owned workspace/session launch API. Plugins never construct a second data store.</summary>
public sealed record PluginWorkspace(string Path, string Name);
/// <param name="Prompt">Full model input; never used as the user-visible message.</param>
/// <param name="Message">Public task summary. Defaults to Title when omitted.</param>
public sealed record PluginTaskRequest(string Workspace, string Title, string Prompt, string? Message = null);
public sealed record PluginTaskResult(string Workspace, Guid ConversationId, string StreamId);
public sealed record PluginWorkspaceTasks(
    Func<IReadOnlyList<PluginWorkspace>> List,
    Func<PluginTaskRequest, CancellationToken, Task<PluginTaskResult>> StartAsync)
{
    /// <summary>Workspace the terminal currently has open; null in hosts without a terminal view.</summary>
    public Func<string?>? Current { get; init; }
}
