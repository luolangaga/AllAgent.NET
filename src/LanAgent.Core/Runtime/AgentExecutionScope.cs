namespace LanAgent.Core.Runtime;

public static class AgentExecutionScope
{
    private static readonly AsyncLocal<bool> Child = new();
    private static readonly AsyncLocal<Guid?> Session = new();
    public static Guid? ConversationId => Session.Value;
    public static bool IsSubAgent => Child.Value;
    internal static IDisposable Enter(bool child, Guid? conversationId = null)
    { var previous = Child.Value; var previousSession = Session.Value; Child.Value = child; Session.Value = conversationId; return new Restore(() => { Child.Value = previous; Session.Value = previousSession; }); }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}
