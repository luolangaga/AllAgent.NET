using System.Text.Json;
using LanAgent.Core.Data;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LanAgent.Terminal;

internal sealed class InputEditor(string historyPath, string workspace, Func<IEnumerable<CommandHint>> commands)
{
    private readonly List<string> _history = Load(historyPath);
    private readonly Dictionary<string, (long Started, Task<string[]> Files, string[] Previous)> _directories = new(StringComparer.OrdinalIgnoreCase);
    private CommandHint[]? _commandSnapshot;
    private string? _hintText;
    private bool _hintSelection;
    private IReadOnlyList<CommandHint> _commandHints = [];
    public IReadOnlyList<string> History => _history;
    public Func<bool>? WakeRequested { get; set; }
    public string? InterruptedDraft { get; set; }
    public IEnumerable<CommandHint> Commands() => _commandSnapshot ??= commands().ToArray();
    public async Task PasteAsync(InputState state, string text, CancellationToken ct = default)
    {
        var prepared = await PastedImages.PrepareAsync(text, workspace, ct);
        state.InsertPaste(prepared.Text, prepared.Notice);
    }
    private static List<string> Load(string path)
    {
        try { return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path))?.TakeLast(500).ToList() ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException) { return []; }
    }
    public IRenderable Render(InputState state, IReadOnlyList<CommandHint> suggestions, string status)
    {
        var input = new InputText(state);
        var rows = new List<IRenderable> { new Rule().RuleStyle("grey"), input, new Rule().RuleStyle("grey") };
        if (state.SearchingHistory) rows.Add(new Text("历史搜索 · Ctrl+R 下一个 · Esc 返回原输入", new Style(Color.Yellow)));
        if (suggestions.Count > 0)
        {
            var table = new Table().NoBorder().HideHeaders().AddColumn("命令").AddColumn("说明");
            var start = Math.Max(0, state.Selection - 5);
            foreach (var (hint, index) in suggestions.Select((hint, index) => (hint, index)).Skip(start).Take(7))
                table.AddRow(hint.MarkerMarkup + (index == state.Selection ? "[black on cyan]› " : "[dim]  ") + Markup.Escape(hint.Text) + "[/]", Markup.Escape(hint.Description + (hint.Usage.Length > 0 ? " · " + hint.Usage : "")));
            rows.Add(table);
            rows.Add(new Text($"{suggestions.Count} 项 · ↑↓ 选择 · Tab 补全", new Style(Color.Grey)));
        }
        else if (state.Text.StartsWith('/')) rows.Add(new Text("没有匹配命令；/help 查看全部命令", new Style(Color.Yellow)));
        var imageLabel = PromptReferences.ImageDraftLabel(state.Text);
        rows.Add(new Text((state.Notice ?? status) + "\n" + (imageLabel.Length > 0 ? imageLabel : "Shift+方向键 选择 · Ctrl+A 全选 · Ctrl+C/X/V 复制/剪切/粘贴 · Ctrl+Z/Y 撤销/重做"), new Style(Color.Grey)));
        return new Rows(rows);
    }
    internal IReadOnlyList<CommandHint> Hints(InputState state)
    {
        if (state.Text.StartsWith('/'))
        {
            if (_hintText != state.Text || _hintSelection != state.HasSelection)
            {
                _commandHints = state.Suggestions(Commands());
                _hintText = state.Text; _hintSelection = state.HasSelection;
            }
            return _commandHints;
        }
        var prefix = state.Text[..state.Cursor]; var start = prefix.LastIndexOf('@');
        if (start < 0 || start > 0 && !char.IsWhiteSpace(prefix[start - 1])) return [];
        var token = prefix[(start + 1)..];
        if (token.Any(char.IsWhiteSpace)) return [];
        var policy = new LanAgent.Core.Tools.WorkspacePolicy(workspace);
        var directory = Path.GetDirectoryName(token.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        try
        {
            var root = policy.Resolve(directory.Length == 0 ? "." : directory);
            if (!_directories.TryGetValue(root, out var cached) || cached.Files.IsCompleted && Environment.TickCount64 - cached.Started > 1500)
            {
                var files = Task.Run(() =>
                {
                    try { return new DirectoryInfo(root).EnumerateFileSystemInfos().Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
                        .Select(file => Path.GetRelativePath(workspace, file.FullName).Replace('\\', '/') + (file.Attributes.HasFlag(FileAttributes.Directory) ? "/" : "")).Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
                });
                var previous = cached.Files?.IsCompletedSuccessfully == true ? cached.Files.Result : cached.Previous ?? [];
                _directories[root] = cached = (Environment.TickCount64, files, previous);
                if (_directories.Count > 8) _directories.Remove(_directories.Keys.First(key => key != root));
            }
            var indexed = cached.Files.IsCompletedSuccessfully ? cached.Files.Result : cached.Previous;
            return indexed.Where(p => p.StartsWith(token, StringComparison.OrdinalIgnoreCase)).Take(50)
                .Select(p => new CommandHint(p.Any(char.IsWhiteSpace) ? "@\"" + p + "\"" : "@" + p, "工作区文件引用")).ToArray();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return []; }
    }
    public async Task<string> ReadAsync(string status, CancellationToken ct = default, string initial = "")
    {
        InterruptedDraft = null;
        // Refresh between prompts, never discover skills on a wheel event or idle tick.
        _commandSnapshot = null; _hintText = null;
        if (FullScreenTerminal.Current is { } screen)
        {
            var submitted = await screen.ReadInputAsync(this, status, initial, ct);
            if (submitted.Length > 0) await RememberAsync(submitted, ct);
            return submitted;
        }
        if (!AnsiConsole.Profile.Capabilities.Interactive || !AnsiConsole.Profile.Capabilities.Ansi)
            return await AnsiConsole.PromptAsync(new TextPrompt<string>("[cyan]你[/] ›").AllowEmpty(), ct);
        var state = new InputState(); state.Set(initial); string? result = null;
        var reader = new TerminalKeyReader();
        using var inputMode = new TextInputMode();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            Console.Write("\u001b[?2004h");
            await AnsiConsole.Live(Render(state, Hints(state), status)).AutoClear(true).Overflow(VerticalOverflow.Crop).StartAsync(async context =>
            {
                while (result == null && !cancelled.IsCancellationRequested)
                {
                    while (!reader.HasInput && !cancelled.IsCancellationRequested)
                    {
                        if (WakeRequested?.Invoke() == true) { InterruptedDraft = state.Text; result = ""; return; }
                        await Task.Delay(33, cancelled.Token);
                    }
                    TerminalInput key;
                    try { key = await reader.ReadAsync(cancelled.Token); } catch (OperationCanceledException) { break; }
                    if (key.Paste != null) await PasteAsync(state, key.Paste, cancelled.Token);
                    else result = state.Key(key.Key, Hints(state), _history);
                    context.UpdateTarget(Render(state, Hints(state), status));
                }
            });
        }
        finally { Console.CancelKeyPress -= handler; Console.Write("\u001b[?2004l"); }
        result ??= "";
        if (result.Length > 0)
        {
            AnsiConsole.Write(new Text("你 › " + result + "\n", new Style(Color.Cyan1)));
            await RememberAsync(result, ct);
        }
        return result;
    }
    private Task RememberAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            using var transaction = new StorageTransaction(historyPath);
            var latest = Load(historyPath);
            if (latest.LastOrDefault() != text) latest.Add(text);
            _history.Clear(); _history.AddRange(latest.TakeLast(500));
            File.WriteAllText(historyPath + ".tmp", JsonSerializer.Serialize(_history));
            File.Move(historyPath + ".tmp", historyPath, true);
        }
        catch (IOException) { if (_history.LastOrDefault() != text) _history.Add(text); }
        return Task.CompletedTask;
    }
}

/// <summary>Bracketed paste is data: newlines and slash commands never submit while the paste is open.</summary>
internal sealed class BracketedPaste
{
    private readonly System.Text.StringBuilder _buffer = new();
    private string _escape = "";
    private bool _active;
    public bool Feed(ConsoleKeyInfo key, out string? text)
    {
        text = null;
        if (!_active && _escape.Length == 0 && key.Key != ConsoleKey.Escape) return false;
        var c = key.Key == ConsoleKey.Escape ? '\u001b' : key.KeyChar;
        if (_escape.Length > 0 || c == '\u001b')
        {
            _escape += c;
            if (_escape == "\u001b[200~") { _active = true; _buffer.Clear(); _escape = ""; return true; }
            if (_escape == "\u001b[201~") { text = _buffer.ToString().Replace("\r\n", "\n").Replace('\r', '\n'); _active = false; _escape = ""; _buffer.Clear(); return true; }
            if ("\u001b[200~".StartsWith(_escape, StringComparison.Ordinal) || "\u001b[201~".StartsWith(_escape, StringComparison.Ordinal)) return true;
            if (_active && _buffer.Length < 32000) _buffer.Append(_escape);
            _escape = ""; return _active;
        }
        if (_active) { if (_buffer.Length < 32000) _buffer.Append(key.Key == ConsoleKey.Enter ? '\n' : c); return true; }
        return false;
    }
}
