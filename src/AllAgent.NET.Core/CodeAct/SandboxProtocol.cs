using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.CodeAct;

/// <summary>Bounded, versioned JSON-lines shared by the trusted worker and its parent.</summary>
public static class SandboxProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 16 * 1024 * 1024;
    public const int MaxDepth = 64;
    private static readonly ConditionalWeakTable<TextReader, Reader> Readers = new();
    private static readonly ConditionalWeakTable<Stream, FrameReader> StreamReaders = new();

    public static async Task<JsonDocument> ReadAsync(Stream input, CancellationToken ct = default)
    {
        var frame = await StreamReaders.GetValue(input, _ => new()).ReadAsync(input, ct);
        return JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = MaxDepth });
    }

    // Console.In is a synchronized TextReader on Windows. Bulk character reads may wait
    // for the requested count even after a newline; raw pipe reads return available bytes.
    private sealed class FrameReader
    {
        private readonly byte[] _buffer = new byte[4096];
        private int _offset, _count;
        internal async Task<byte[]> ReadAsync(Stream input, CancellationToken ct)
        {
            using var frame = new MemoryStream();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_offset == _count) { _count = await input.ReadAsync(_buffer, ct); _offset = 0; }
                if (_count == 0) throw new EndOfStreamException("Sandbox channel closed before a complete frame.");
                var end = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
                var length = (end < 0 ? _count : end) - _offset;
                if (frame.Length + length > MaxFrameBytes) throw new InvalidDataException("Sandbox IPC frame exceeds 16 MiB.");
                frame.Write(_buffer, _offset, length); _offset += length;
                if (end >= 0) { _offset++; return frame.ToArray(); }
            }
        }
    }

    public static string Encode(object value, string? executionId, string? callId = null)
    {
        var node = JsonSerializer.SerializeToNode(value, ToolResult.JsonOptions)!.AsObject();
        node["version"] = Version; node["executionId"] = executionId;
        if (callId != null) node["callId"] = callId;
        var text = node.ToJsonString(ToolResult.JsonOptions);
        if (Encoding.UTF8.GetByteCount(text) > MaxFrameBytes) throw new InvalidDataException("Sandbox IPC frame exceeds 16 MiB.");
        return text;
    }

    public static void Validate(JsonElement frame, string? executionId)
    {
        if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != Version ||
            !frame.TryGetProperty("executionId", out var id) || id.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || id.GetString() != executionId)
            throw new InvalidDataException("Sandbox protocol version or execution ID mismatch.");
    }

    public static async Task<JsonDocument> ReadAsync(TextReader input, CancellationToken ct = default)
    {
        var line = await Readers.GetValue(input, _ => new()).ReadAsync(input, ct);
        if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes) throw new InvalidDataException("Sandbox IPC frame exceeds 16 MiB.");
        return JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = MaxDepth });
    }

    private sealed class Reader
    {
        private readonly char[] _buffer = new char[4096];
        private int _offset, _count;
        internal async Task<string> ReadAsync(TextReader input, CancellationToken ct)
        {
            var line = new StringBuilder();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_offset == _count) { _count = await input.ReadAsync(_buffer.AsMemory(), ct); _offset = 0; }
                if (_count == 0) throw new EndOfStreamException("Sandbox channel closed before a complete frame.");
                var end = Array.IndexOf(_buffer, '\n', _offset, _count - _offset);
                var length = (end < 0 ? _count : end) - _offset;
                if (line.Length + length > MaxFrameBytes) throw new InvalidDataException("Sandbox IPC frame exceeds 16 MiB.");
                line.Append(_buffer, _offset, length); _offset += length;
                if (end >= 0) { _offset++; return line.ToString().TrimEnd('\r'); }
            }
        }
    }
}
