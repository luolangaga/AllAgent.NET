using System.Text;
using System.Text.Json;

namespace LanAgent.Core.Tools;

// Pi-style line/byte limits. Budgets are adapted to the host's JSON result cap.
internal static class ToolOutputWindow
{
    public const int MaxBytes = 20_000;
    public const int MaxLines = 2_000;
    public static int JsonBytes(string text) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text, ToolResult.JsonOptions)) - 2;
    public static string JsonHead(string text, int maxBytes = MaxBytes)
    {
        var head = Head(text, maxBytes);
        if (JsonBytes(head) <= maxBytes) return head;
        var low = 0; var high = maxBytes;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (JsonBytes(Head(text, middle)) <= maxBytes) low = middle; else high = middle - 1;
        }
        return Head(text, low);
    }
    public static string Head(string text, int bytes = MaxBytes)
    {
        var encoded = Encoding.UTF8.GetBytes(text);
        if (encoded.Length <= bytes) return text;
        var end = bytes;
        while (end > 0 && (encoded[end] & 0xc0) == 0x80) end--;
        return Encoding.UTF8.GetString(encoded, 0, end);
    }
    public static string Tail(string text, int bytes = MaxBytes, int lines = MaxLines)
    {
        var encoded = Encoding.UTF8.GetBytes(text);
        var start = Math.Max(0, encoded.Length - bytes);
        while (start < encoded.Length && (encoded[start] & 0xc0) == 0x80) start++;
        var tail = Encoding.UTF8.GetString(encoded, start, encoded.Length - start);
        // Keep complete lines unless a single line itself exceeds the byte budget.
        if (start > 0 && encoded[start - 1] != '\n' && tail.IndexOf('\n') is var newline && newline >= 0 && newline < tail.Length - 1)
            tail = tail[(newline + 1)..];
        var end = tail.EndsWith('\n') ? tail.Length - 1 : tail.Length;
        var count = 1;
        for (var i = end - 1; i >= 0; i--)
            if (tail[i] == '\n' && ++count > lines) return tail[(i + 1)..];
        return tail;
    }
    public static string JsonTail(string text)
    {
        var tail = Tail(text);
        if (JsonBytes(tail) <= MaxBytes) return tail;
        var low = 0; var high = MaxBytes;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (JsonBytes(Tail(text, middle)) <= MaxBytes) low = middle;
            else high = middle - 1;
        }
        return Tail(text, low);
    }
}
