namespace LanAgent.Core.Runtime;

public static class AgentExecutionScope
{
    private static readonly AsyncLocal<bool> Child = new();
    private static readonly AsyncLocal<Guid?> Session = new();
    private static readonly AsyncLocal<string?> ToolCall = new();
    public static string? ToolCallId => ToolCall.Value;
    internal static IDisposable EnterTool(string id)
    { var previous = ToolCall.Value; ToolCall.Value = id; return new Restore(() => ToolCall.Value = previous); }
    public static Guid? ConversationId => Session.Value;
    public static bool IsSubAgent => Child.Value;
    internal static IDisposable Enter(bool child, Guid? conversationId = null)
    { var previous = Child.Value; var previousSession = Session.Value; var previousTool = ToolCall.Value; Child.Value = child; Session.Value = conversationId; ToolCall.Value = null; return new Restore(() => { Child.Value = previous; Session.Value = previousSession; ToolCall.Value = previousTool; }); }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}
