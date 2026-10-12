namespace LanAgent.Core.Extensions;

/// <summary>Before hooks fail closed. All hook calls have their own deadline and respect caller cancellation.</summary>
public sealed class HookPipeline(Func<IReadOnlyList<AgentHook>> hooks)
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _errors = new();
    public IReadOnlyList<string> Errors => _errors.ToArray();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
    public async ValueTask NotifyAsync(HookEvent evt, CancellationToken ct)
    {
        foreach (var hook in hooks())
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(Timeout);
            try
            {
                // Plugins may perform synchronous work before returning their ValueTask.
                // Keep that work off the agent/UI thread so the deadline covers it too.
                var decision = await Task.Run(() => hook.Invoke(evt, timeout.Token).AsTask(), CancellationToken.None).WaitAsync(timeout.Token);
                if (!decision.Allow && evt.Stage is HookStage.BeforeTool or HookStage.BeforeTurn)
                    throw new InvalidOperationException("Hook " + hook.Name + " 拒绝操作：" + (decision.Reason ?? "未提供原因"));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                var error = "Hook " + hook.Name + " 超时。";
                if (evt.Stage is HookStage.BeforeTool or HookStage.BeforeTurn) throw new TimeoutException(error);
                Record(error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && evt.Stage is not (HookStage.BeforeTool or HookStage.BeforeTurn))
            { Record("Hook " + hook.Name + "：" + ex.Message); }
        }
    }
    private void Record(string error) { _errors.Enqueue(LanAgent.Core.Tools.ToolResult.Clip(error)); while (_errors.Count > 32) _errors.TryDequeue(out _); }
}
