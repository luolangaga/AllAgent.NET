namespace AllAgent.NET.Core.Runtime;

public static class AgentExecutionScope
{
    private static readonly AsyncLocal<bool> Child = new();
    private static readonly AsyncLocal<Guid?> Session = new();
    private static readonly AsyncLocal<string?> ToolCall = new();
    private static readonly AsyncLocal<string?> ParentToolCall = new();
    public static string? ParentToolCallId => ParentToolCall.Value;
    private static readonly AsyncLocal<string?> Run = new();
    private static readonly AsyncLocal<string?> Workspace = new();
    public static string? WorkspaceRoot => Workspace.Value;
    public static string? RunId => Run.Value;
    public static string? ToolCallId => ToolCall.Value;
    internal static IDisposable EnterTool(string id, string? parentId = null)
    { var previous = ToolCall.Value; var previousParent = ParentToolCall.Value; ToolCall.Value = id; ParentToolCall.Value = parentId;
        return new Restore(() => { ToolCall.Value = previous; ParentToolCall.Value = previousParent; }); }
    public static Guid? ConversationId => Session.Value;
    public static bool IsSubAgent => Child.Value;
    internal static IDisposable Enter(bool child, Guid? conversationId = null, string? workspaceRoot = null)
    { var previous = Child.Value; var previousSession = Session.Value; var previousTool = ToolCall.Value; var previousRun = Run.Value;
        var previousWorkspace = Workspace.Value;
        var previousParent = ParentToolCall.Value; ParentToolCall.Value = null;
        Child.Value = child; Session.Value = conversationId; ToolCall.Value = null; Run.Value = Guid.NewGuid().ToString("N"); Workspace.Value = workspaceRoot;
        return new Restore(() => { Child.Value = previous; Session.Value = previousSession; ToolCall.Value = previousTool; ParentToolCall.Value = previousParent; Run.Value = previousRun; Workspace.Value = previousWorkspace; }); }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}
