using System.Threading.Channels;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

/// <summary>Every refresh executes inside Spectre's Live scope; prompts wait for that owner to stop.</summary>
internal sealed class SpectreScreenDisplay(IAnsiConsole console) : IDisposable
{
    private sealed record UpdateRequest(IRenderable Frame, TaskCompletionSource Completed);
    private Channel<UpdateRequest>? _updates;
    private Task? _running;

    public void Start(IRenderable frame)
    {
        if (_running != null) return;
        var updates = Channel.CreateUnbounded<UpdateRequest>(new() { SingleReader = true, SingleWriter = true });
        _updates = updates;
        _running = console.Live(frame).AutoClear(true).Overflow(VerticalOverflow.Crop)
            .StartAsync(async context =>
            {
                context.Refresh();
                await foreach (var request in updates.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    try { context.UpdateTarget(request.Frame); request.Completed.TrySetResult(); }
                    catch (Exception error) { request.Completed.TrySetException(error); throw; }
                }
            });
        if (_running.IsCompleted) _running.GetAwaiter().GetResult();
    }

    public void Update(IRenderable frame)
    {
        if (_running == null) return;
        if (_running.IsFaulted) _running.GetAwaiter().GetResult();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_updates!.Writer.TryWrite(new(frame, completed))) throw new InvalidOperationException("Spectre display has stopped.");
        completed.Task.GetAwaiter().GetResult();
    }

    public void Stop()
    {
        if (_running == null) return;
        _updates!.Writer.TryComplete();
        try { _running.GetAwaiter().GetResult(); }
        finally { _running = null; _updates = null; }
    }

    public void Dispose() => Stop();
}
