using Spectre.Console;
using Spectre.Console.Rendering;
using System.Threading.Channels;

namespace LanAgent.Terminal;

/// <summary>Single screen owner. Only changed rows are written; prompts take ownership after Stop.</summary>
internal sealed class SpectreScreenDisplay(IAnsiConsole console, bool fullScreen = false) : IDisposable
{
    private sealed record UpdateRequest(IRenderable Frame, TaskCompletionSource Completed);
    private Channel<UpdateRequest>? _updates;
    private Task? _running;
    private bool _started;
    private IReadOnlyList<List<Segment>>? _rows;
    private int _width, _height;

    public void Start(IRenderable frame)
    {
        if (!fullScreen)
        {
            if (_running != null) return;
            var updates = Channel.CreateUnbounded<UpdateRequest>(new() { SingleReader = true, SingleWriter = true });
            _updates = updates;
            _running = console.Live(frame).AutoClear(true).Overflow(VerticalOverflow.Crop).StartAsync(async context =>
            {
                context.Refresh();
                await foreach (var request in updates.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    try { context.UpdateTarget(request.Frame); request.Completed.TrySetResult(); }
                    catch (Exception error) { request.Completed.TrySetException(error); throw; }
                }
            });
            if (_running.IsCompleted) _running.GetAwaiter().GetResult();
            return;
        }
        if (_started) return;
        _started = true;
        _rows = null;
        console.Cursor.Hide();
    }

    public void Update(IRenderable frame)
    {
        if (_running == null) return;
        if (_running.IsFaulted) _running.GetAwaiter().GetResult();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_updates!.Writer.TryWrite(new(frame, completed))) throw new InvalidOperationException("Spectre display has stopped.");
        completed.Task.GetAwaiter().GetResult();
    }

    public void Update(Segment[] segments, int width, int height)
    {
        if (!_started) return;
        var rows = Segment.SplitLines(segments, width, height);
        var delta = ChangedRows(rows, _width == width && _height == height ? _rows : null);
        if (delta.Count > 0) console.Write(new SegmentFrame(delta, width));
        _rows = rows; _width = width; _height = height;
    }

    internal static List<Segment> ChangedRows(IReadOnlyList<List<Segment>> rows, IReadOnlyList<List<Segment>>? previous)
    {
        var delta = new List<Segment>();
        for (var y = 0; y < rows.Count; y++)
        {
            if (previous != null && y < previous.Count && Equal(rows[y], previous[y])) continue;
            var row = rows[y];
            var first = 0; var end = row.Count; var column = 1;
            if (previous != null && y < previous.Count)
            {
                var old = previous[y];
                while (first < Math.Min(row.Count, old.Count) && EqualSegment(row[first], old[first]))
                { column += row[first].CellCount(); first++; }
                // Only skip a suffix at the same physical columns. Full frames are padded;
                // the width check also keeps this safe for unpadded callers and wide Unicode.
                if (row.Sum(segment => segment.CellCount()) == old.Sum(segment => segment.CellCount()))
                {
                    var oldEnd = old.Count;
                    while (end > first && oldEnd > first && EqualSegment(row[end - 1], old[oldEnd - 1]))
                    { end--; oldEnd--; }
                }
            }
            // Keep unchanged borders, sidebar and trailing padding out of the output stream.
            delta.Add(Segment.Control($"\u001b[{y + 1};{column}H"));
            for (var index = first; index < end; index++) delta.Add(row[index]);
        }
        return delta;
    }

    private static bool EqualSegment(Segment left, Segment right) =>
        left.Text == right.Text && left.Style.Equals(right.Style) && left.Link == right.Link && left.IsControlCode == right.IsControlCode;

    private static bool Equal(List<Segment> left, List<Segment> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (!EqualSegment(left[i], right[i])) return false;
        return true;
    }

    public void Stop()
    {
        if (_running != null)
        {
            _updates!.Writer.TryComplete();
            try { _running.GetAwaiter().GetResult(); }
            finally { _running = null; _updates = null; }
        }
        if (!_started) return;
        _started = false; _rows = null;
        console.Cursor.Show();
    }

    public void Dispose() => Stop();
}
