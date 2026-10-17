using AllAgent.NET.Core.Services;

namespace AllAgent.NET.Core.Bridge;

/// <summary>A reconnectable transcript of an active browser run. Adjacent token
/// deltas are coalesced so storage grows with output, not token count.</summary>
internal sealed class BridgeStreamJournal(List<MessageDto> history, string input)
{
    private readonly object _gate = new();
    private readonly LinkedList<(long Sequence, AgentEvent Event, DateTime Timestamp)> _events = new();
    private readonly Dictionary<string, Queue<LinkedListNode<(long Sequence, AgentEvent Event, DateTime Timestamp)>>> _outputs = new();
    private long _sequence;
    public string StreamId { get; set; } = "";
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public long Append(AgentEvent evt, DateTime? timestamp = null)
    {
        lock (_gate)
        {
            var sequence = ++_sequence;
            if (_events.Last is { } tail)
            {
                var previous = tail.Value.Event;
                if (evt is TokenDelta token && previous is TokenDelta prior && prior.Text.Length < 8192)
                    evt = new TokenDelta(prior.Text + token.Text);
                else if (evt is ThinkingDelta thinking && previous is ThinkingDelta priorThinking && priorThinking.Text.Length < 8192)
                    evt = new ThinkingDelta(priorThinking.Text + thinking.Text);
                else previous = null;
                if (previous != null) { tail.Value = (sequence, evt, tail.Value.Timestamp); return sequence; }
            }
            var node = _events.AddLast((sequence, evt, timestamp ?? DateTime.UtcNow));
            // Live process output is a tail; complete results are recorded by
            // ToolCallCompleted and persisted host traces.
            if (evt is ToolCallOutput output)
            {
                if (!_outputs.TryGetValue(output.Id, out var lines)) _outputs[output.Id] = lines = new();
                lines.Enqueue(node);
                if (lines.Count > 400) _events.Remove(lines.Dequeue());
            }
            return sequence;
        }
    }

    public object Snapshot(Func<AgentEvent, string> name, Func<AgentEvent, object> data)
    {
        lock (_gate)
            return new
            {
                streamId = StreamId, history, input, createdAt = CreatedAt,
                events = _events.Select(item => new { eventName = name(item.Event), data = data(item.Event), sequence = item.Sequence, timestamp = item.Timestamp }).ToArray()
            };
    }
}
