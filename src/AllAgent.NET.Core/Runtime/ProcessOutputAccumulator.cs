using System.Text;
using AllAgent.NET.Core.Tools;

namespace AllAgent.NET.Core.Runtime;

// Bounded-memory tail plus lazy spill-to-disk, as in Pi's OutputAccumulator.
internal sealed class ProcessOutputAccumulator(string path) : IDisposable
{
    private readonly StringBuilder _tail = new();
    private readonly StringBuilder _initial = new();
    private StreamWriter? _file;
    private long _bytes;
    private long _newlines;
    private char _last;
    public string? FullOutputPath => _file == null ? null : path;
    public bool Truncated => _file != null || _bytes > ToolOutputWindow.MaxBytes || _newlines + (_bytes > 0 && _last != '\n' ? 1 : 0) > ToolOutputWindow.MaxLines ||
        ToolOutputWindow.JsonBytes(_initial.ToString()) > ToolOutputWindow.MaxBytes;
    public void Append(string text)
    {
        if (text.Length == 0) return;
        _bytes += Encoding.UTF8.GetByteCount(text); _newlines += text.Count(c => c == '\n'); _last = text[^1];
        _tail.Append(text);
        if (_tail.Length > ToolOutputWindow.MaxBytes * 2)
        { var bounded = ToolOutputWindow.Tail(_tail.ToString()); _tail.Clear().Append(bounded); }
        if (_file == null)
        {
            _initial.Append(text);
            if (Truncated)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _file = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                _file.Write(_initial.ToString()); _initial.Clear();
            }
        }
        else _file.Write(text);
    }
    public string Snapshot() => ToolOutputWindow.JsonTail(_tail.ToString());
    public void Flush() => _file?.Flush();
    public void Dispose() => _file?.Dispose();
}
