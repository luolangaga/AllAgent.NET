using System.Globalization;

namespace LanAgent.Terminal;

/// <summary>Grapheme-aware editing state. Clipboard access happens only for explicit user gestures.</summary>
internal sealed class InputState(Func<string, bool>? copy = null)
{
    public string Text { get; private set; } = "";
    public int Cursor { get; private set; }
    public int Selection { get; private set; } // Command suggestion, not text selection.
    public int? Anchor { get; private set; }
    public int SelectionStart => Math.Min(Cursor, Anchor ?? Cursor);
    public int SelectionLength => Math.Abs(Cursor - (Anchor ?? Cursor));
    public bool HasSelection => SelectionLength > 0;
    public string SelectedText => Text.Substring(SelectionStart, SelectionLength);
    public string? Notice { get; private set; }
    public bool AllowCopy { get; set; } = true;
    public bool SearchingHistory { get; private set; }
    private int _historyIndex = -1;
    private string _draft = "";
    private InputLayout? _layout;
    private int? _preferredColumn;
    internal void Layout(InputLayout layout) => _layout = layout;
    private sealed record Edit(string Text, int Cursor, int? Anchor);
    private readonly List<Edit> _undo = [], _redo = [];
    private Edit Snapshot() => new(Text, Cursor, Anchor);
    private void SaveEdit() { _undo.Add(Snapshot()); if (_undo.Count > 100) _undo.RemoveAt(0); _redo.Clear(); }
    private int Previous(int position) => position == 0 ? 0 : StringInfo.ParseCombiningCharacters(Text).LastOrDefault(i => i < position);
    private int Next(int position) => StringInfo.ParseCombiningCharacters(Text).FirstOrDefault(i => i > position, Text.Length);
    public void Set(string value) { Text = TerminalSafeText.Clean(value.Replace("\r\n", "\n").Replace('\r', '\n')); Cursor = Text.Length; Anchor = null; Selection = 0; _historyIndex = -1; SearchingHistory = false; Notice = null; _preferredColumn = null; _undo.Clear(); _redo.Clear(); }
    public void MoveTo(int position, bool extend = false)
    {
        _preferredColumn = null;
        position = Math.Clamp(position, 0, Text.Length);
        if (position < Text.Length) position = StringInfo.ParseCombiningCharacters(Text).LastOrDefault(i => i <= position);
        if (extend) Anchor ??= Cursor; else Anchor = null;
        Cursor = position; Selection = 0; Notice = null;
    }
    public void SelectAll() { Anchor = 0; Cursor = Text.Length; Selection = 0; }
    public void MoveSuggestion(int delta, int count) { Selection = Math.Clamp(Selection + delta, 0, Math.Max(0, count - 1)); }
    public bool CopySelection(bool cut = false)
    {
        if (!HasSelection) return false;
        if (!AllowCopy) { Notice = "密码字段不允许复制"; return false; }
        if (!(copy ?? ClipboardText.Write)(SelectedText)) { Notice = "剪贴板暂不可用，选中文字已保留"; return false; }
        Notice = cut ? "已剪切" : "已复制";
        if (cut) Replace(SelectionStart, SelectionLength, "");
        return true;
    }
    private void Replace(int start, int length, string value)
    {
        value = TerminalSafeText.Clean(value.Replace("\r\n", "\n").Replace('\r', '\n'));
        if (Text.Length - length + value.Length > 32000) { Notice = "输入最多 32000 个字符"; return; }
        if (length == 0 && value.Length == 0) return;
        SaveEdit(); Text = Text.Remove(start, length).Insert(start, value); Cursor = start + value.Length;
        Anchor = null; Selection = 0; SearchingHistory = false; _historyIndex = -1; _preferredColumn = null;
    }
    public void Insert(string value) { Notice = null; Replace(SelectionStart, SelectionLength, value); }
    public void InsertPaste(string value, string? notice) { Insert(value); Notice ??= notice; }
    public void RemoveLastImage()
    {
        var image = PromptReferences.ImageReferences(Text).LastOrDefault();
        if (image == null) { Notice = "草稿没有图片附件。"; return; }
        Replace(image.Index, image.Length, ""); Notice = "已移除最后一张图片；Ctrl+Z 可恢复。";
    }
    private void Restore(List<Edit> source, List<Edit> destination)
    {
        if (source.Count == 0) return;
        destination.Add(Snapshot()); var previous = source[^1]; source.RemoveAt(source.Count - 1);
        Text = previous.Text; Cursor = previous.Cursor; Anchor = previous.Anchor; Selection = 0; SearchingHistory = false; _historyIndex = -1; Notice = null;
    }
    public void Complete(CommandHint hint)
    {
        if (Text.StartsWith('/')) Replace(0, Text.Length, hint.Text + " ");
        else { var start = Text.LastIndexOf('@', Math.Max(0, Cursor - 1)); if (start >= 0) Replace(start, Cursor - start, hint.Text); }
        Selection = 0;
    }
    public IReadOnlyList<CommandHint> Suggestions(IEnumerable<CommandHint> commands) => Text.StartsWith('/') && !HasSelection
        ? commands.Where(c => (Text.Trim() != "/" || !c.Advanced) && c.Text.StartsWith(Text.TrimEnd(), StringComparison.OrdinalIgnoreCase)).Take(50).ToArray() : [];
    public string? Key(ConsoleKeyInfo key, IReadOnlyList<CommandHint> hints, IReadOnlyList<string> history)
    {
        var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control); var shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        Notice = null;
        if (key.Modifiers.HasFlag(ConsoleModifiers.Alt) && key.Key == ConsoleKey.Backspace) { RemoveLastImage(); return null; }
        if (ctrl && key.Key == ConsoleKey.D && Text.Length == 0) return "/exit";
        if (ctrl && key.Key == ConsoleKey.C) { if (HasSelection) { CopySelection(); return null; } Set(""); return ""; }
        if (ctrl && key.Key == ConsoleKey.X || shift && key.Key == ConsoleKey.Delete) { CopySelection(cut: true); return null; }
        if (ctrl && key.Key == ConsoleKey.Insert) { CopySelection(); return null; }
        if (ctrl && key.Key == ConsoleKey.V || shift && key.Key == ConsoleKey.Insert) { Notice = "剪贴板没有可粘贴的文本或暂不可用"; return null; } // Reader supplies a Paste event on success.
        if (ctrl && key.Key == ConsoleKey.A) { SelectAll(); return null; }
        if (ctrl && key.Key == ConsoleKey.Z) { if (shift) Restore(_redo, _undo); else Restore(_undo, _redo); return null; }
        if (ctrl && key.Key == ConsoleKey.Y) { Restore(_redo, _undo); return null; }
        if (ctrl && key.Key == ConsoleKey.R)
        {
            var matches = history.Reverse().Where(h => h.Contains(SearchingHistory ? _draft : Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!SearchingHistory) { _draft = Text; _historyIndex = -1; }
            SearchingHistory = true; Anchor = null;
            if (matches.Length > 0) { _historyIndex = (_historyIndex + 1) % matches.Length; Text = matches[_historyIndex]; Cursor = Text.Length; }
            return null;
        }
        if (key.Key == ConsoleKey.Escape) { if (HasSelection) Anchor = null; else if (SearchingHistory) Set(_draft); else Set(""); SearchingHistory = false; return null; }
        if (key.Key == ConsoleKey.Tab) { if (hints.Count > 0) Complete(hints[Math.Clamp(Selection, 0, hints.Count - 1)]); return null; }
        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
        {
            var step = key.Key == ConsoleKey.UpArrow ? -1 : 1;
            if (hints.Count > 0 && !shift && !HasSelection) Selection = (Selection + step + hints.Count) % hints.Count;
            else if (!ctrl && _layout is { } layout && layout.Text == Text && (layout.Rows.Count > 1 || shift))
            {
                var row = layout.CaretRow(Cursor);
                var column = _preferredColumn ?? layout.Rows[row].Column(Cursor);
                var target = Math.Clamp(row + step, 0, layout.Rows.Count - 1);
                MoveTo(layout.Rows[target].HitTest(column), shift);
                _preferredColumn = column;
            }
            else if ((Text.Contains('\n') || shift) && !ctrl)
            {
                var start = Cursor == 0 ? 0 : Text.LastIndexOf('\n', Cursor - 1, Cursor) + 1;
                var column = new StringInfo(Text[start..Cursor]).LengthInTextElements;
                var nextStart = step < 0 ? start == 0 ? 0 : start == 1 ? 0 : Text.LastIndexOf('\n', start - 2, start - 1) + 1 : Text.IndexOf('\n', Cursor) is var end && end >= 0 ? end + 1 : start;
                var nextEnd = Text.IndexOf('\n', nextStart); if (nextEnd < 0) nextEnd = Text.Length;
                var positions = StringInfo.ParseCombiningCharacters(Text[nextStart..nextEnd]);
                MoveTo(nextStart + (column < positions.Length ? positions[column] : nextEnd - nextStart), shift);
            }
            else if (!shift && history.Count > 0)
            {
                if (_historyIndex < 0) { _draft = Text; _historyIndex = history.Count; }
                _historyIndex = Math.Clamp(_historyIndex + step, 0, history.Count);
                Text = _historyIndex == history.Count ? _draft : history[_historyIndex]; Cursor = Text.Length; Anchor = null;
            }
            return null;
        }
        if (key.Key == ConsoleKey.Enter)
        {
            if (key.Modifiers != 0) { Insert("\n"); return null; }
            if (Text.EndsWith('\\') && !HasSelection) { Replace(Text.Length - 1, 1, "\n"); return null; }
            if (Text.StartsWith('/') && hints.Count > 0 && !Text.Contains(' ') && !hints.Any(h => h.Text == Text)) return hints[Math.Clamp(Selection, 0, hints.Count - 1)].Text;
            return Text;
        }
        if (ctrl && key.Key == ConsoleKey.J) { Insert("\n"); return null; }
        if (ctrl && key.Key == ConsoleKey.E) MoveTo(Text.Length, shift);
        else if (key.Key == ConsoleKey.Home) MoveTo(ctrl ? 0 : Text.LastIndexOf('\n', Math.Max(0, Cursor - 1), Cursor) + 1, shift);
        else if (key.Key == ConsoleKey.End) { var end = Text.IndexOf('\n', Cursor); MoveTo(ctrl || end < 0 ? Text.Length : end, shift); }
        else if (key.Key == ConsoleKey.LeftArrow)
        {
            var position = HasSelection && !shift && !ctrl ? SelectionStart : Previous(Cursor);
            if (ctrl) { while (position > 0 && char.IsWhiteSpace(Text[position - 1])) position = Previous(position); while (position > 0 && !char.IsWhiteSpace(Text[position - 1])) position = Previous(position); }
            MoveTo(position, shift);
        }
        else if (key.Key == ConsoleKey.RightArrow)
        {
            var position = HasSelection && !shift && !ctrl ? SelectionStart + SelectionLength : Next(Cursor);
            if (ctrl) { while (position < Text.Length && !char.IsWhiteSpace(Text[position])) position = Next(position); while (position < Text.Length && char.IsWhiteSpace(Text[position])) position = Next(position); }
            MoveTo(position, shift);
        }
        else if (key.Key is ConsoleKey.Backspace or ConsoleKey.Delete && HasSelection) Replace(SelectionStart, SelectionLength, "");
        else if (ctrl && key.Key == ConsoleKey.U) Replace(0, Cursor, "");
        else if (ctrl && key.Key == ConsoleKey.K) Replace(Cursor, Text.Length - Cursor, "");
        else if (ctrl && key.Key is ConsoleKey.W or ConsoleKey.Backspace)
        {
            var start = Cursor; while (start > 0 && char.IsWhiteSpace(Text[start - 1])) start = Previous(start); while (start > 0 && !char.IsWhiteSpace(Text[start - 1])) start = Previous(start);
            Replace(start, Cursor - start, "");
        }
        else if (key.Key == ConsoleKey.Backspace && Cursor > 0) { var previous = Previous(Cursor); Replace(previous, Cursor - previous, ""); }
        else if (key.Key == ConsoleKey.Delete && Cursor < Text.Length) Replace(Cursor, Next(Cursor) - Cursor, "");
        else if (!ctrl && !char.IsControl(key.KeyChar)) Insert(key.KeyChar.ToString());
        Selection = 0; _historyIndex = -1; return null;
    }
}
