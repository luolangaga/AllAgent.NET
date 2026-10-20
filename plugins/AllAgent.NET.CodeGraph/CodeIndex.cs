using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllAgent.NET.CodeGraph;

public sealed class CodeSymbol
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Kind { get; set; } = "";
    [JsonIgnore] public string File { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? Container { get; set; }
    public string? Signature { get; set; }
}

public sealed class CodeReference
{
    public string Name { get; set; } = "";
    [JsonIgnore] public string File { get; set; } = "";
    public int Line { get; set; }
}

public sealed class CodeIndexFile
{
    [JsonIgnore] public string Path { get; set; } = "";
    public string Language { get; set; } = "other";
    public long ModifiedTicks { get; set; }
    public long Size { get; set; }
    public string? Error { get; set; }
    public List<CodeSymbol> Symbols { get; set; } = [];
    public List<CodeReference> References { get; set; } = [];
}

public sealed class CodeIndex
{
    public const int CurrentVersion = 1;
    public const int MaxFiles = 20_000;
    public const long MaxParseBytes = 1_048_576;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".idea", ".allagent", ".codegraph",
        "bin", "obj", "node_modules", "dist", "build", "out", "coverage",
        ".venv", "venv", "__pycache__", "artifacts", "target",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "do", "does", "for", "from",
        "how", "in", "is", "it", "of", "on", "or", "the", "to", "what", "when", "where",
        "which", "who", "why", "with", "work", "works",
    };

    private static readonly JsonSerializerOptions Json = new(CodeGraphJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public int Version { get; set; } = CurrentVersion;
    public string Workspace { get; set; } = "";
    public DateTime BuiltAt { get; set; }
    public Dictionary<string, CodeIndexFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public List<CodeSymbol> Symbols { get; private set; } = [];

    [JsonIgnore]
    public List<CodeReference> References { get; private set; } = [];

    [JsonIgnore]
    public string? Notice { get; set; }

    private Dictionary<string, List<CodeSymbol>>? _byName;

    public static string IndexDirectory(string workspace) => Path.Combine(workspace, ".codegraph");
    public static string IndexPath(string workspace) => Path.Combine(IndexDirectory(workspace), "allagent-index.json");

    public static CodeIndex? Load(string workspace)
    {
        var path = IndexPath(workspace);
        if (!File.Exists(path)) return null;
        try
        {
            var index = JsonSerializer.Deserialize<CodeIndex>(File.ReadAllText(path), Json);
            if (index == null || index.Version != CurrentVersion) return null;
            index.Workspace = workspace;
            index.Files = new Dictionary<string, CodeIndexFile>(index.Files, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, file) in index.Files) file.Path = key;
            index.Rebuild();
            return index;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save()
    {
        var path = IndexPath(Workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, true);
    }

    public void Rebuild()
    {
        Symbols = [];
        References = [];
        _byName = null;
        foreach (var (path, file) in Files)
        {
            file.Path = path;
            foreach (var symbol in file.Symbols)
            {
                symbol.File = path;
                Symbols.Add(symbol);
            }
            foreach (var reference in file.References)
            {
                reference.File = path;
                References.Add(reference);
            }
        }
        Symbols.Sort((left, right) =>
        {
            var byFile = string.CompareOrdinal(left.File, right.File);
            return byFile != 0 ? byFile : left.StartLine.CompareTo(right.StartLine);
        });
    }

    public IReadOnlyList<CodeSymbol> Search(string query, int limit = 12)
    {
        var terms = Tokenize(query).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (terms.Length == 0 || Symbols.Count == 0) return [];
        var total = Symbols.Count;
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            var matches = 0;
            foreach (var symbol in Symbols)
                if (symbol.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || symbol.FullName.Contains(term, StringComparison.OrdinalIgnoreCase))
                    matches++;
            weights[term] = Math.Log(1.0 + (double)total / (1 + matches));
        }
        var scored = new List<(CodeSymbol Symbol, double Score)>();
        foreach (var symbol in Symbols)
        {
            var score = 0.0;
            for (var index = 0; index < terms.Length; index++)
            {
                var term = terms[index];
                var weight = weights[term] * (index == 0 ? 1.5 : 1.0);
                if (symbol.Name.Equals(term, StringComparison.Ordinal)) score += 200 * weight;
                else if (symbol.Name.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 120 * weight;
                else if (symbol.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score += 100 * weight;
                else if (symbol.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 70 * weight;
                else if (symbol.FullName.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 50 * weight;
                else if (symbol.File.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 30 * weight;
            }
            if (score <= 0) continue;
            score *= symbol.Kind switch
            {
                "class" or "interface" or "struct" or "record" or "enum" or "delegate" => 1.25,
                "method" or "function" or "constructor" => 1.1,
                _ => 0.9,
            };
            scored.Add((symbol, score));
        }
        return scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Symbol.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Symbol.StartLine)
            .Select(item => item.Symbol)
            .Take(limit)
            .ToArray();
    }

    public IReadOnlyList<CodeSymbol> SearchFile(string query, int limit = 3)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0) return [];
        return Files.Values
            .Where(file => file.Symbols.Count > 0 && terms.Any(term => file.Path.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .SelectMany(file => file.Symbols.Take(12))
            .Take(limit * 12)
            .ToArray();
    }

    public IReadOnlyList<CodeSymbol> Resolve(string name, int limit = 4)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        ByName.TryGetValue(name, out var exact);
        if (exact is { Count: > 0 }) return exact.Take(limit).ToArray();
        return Symbols
            .Where(symbol => symbol.Name.EndsWith(name, StringComparison.OrdinalIgnoreCase) || symbol.FullName.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToArray();
    }

    public IReadOnlyList<CodeReference> ReferencesTo(string name) =>
        References.Where(reference => reference.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();

    public IReadOnlyList<CodeReference> ReferencesIn(CodeSymbol symbol) =>
        References
            .Where(reference => reference.File.Equals(symbol.File, StringComparison.OrdinalIgnoreCase)
                && reference.Line >= symbol.StartLine && reference.Line <= symbol.EndLine)
            .ToArray();

    public IReadOnlyList<string> Languages()
    {
        var counts = Files.Values
            .Where(file => file.Language != "other")
            .GroupBy(file => file.Language, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{DisplayLanguage(group.Key)} {group.Count()}");
        return counts.ToArray();
    }

    public static string DisplayLanguage(string language) => language switch
    {
        "csharp" => "C#",
        "typescript" => "TypeScript",
        "tsx" => "TSX",
        "javascript" => "JavaScript",
        "python" => "Python",
        _ => language,
    };

    public static string LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".ts" => "typescript",
        ".tsx" => "tsx",
        ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
        ".py" => "python",
        _ => "other",
    };

    public static List<string> EnumerateFiles(string workspace, out bool truncated)
    {
        var results = new List<string>();
        truncated = false;
        var pending = new Stack<(string Directory, GitIgnore Rules)>();
        pending.Push((workspace, GitIgnore.Empty));
        while (pending.Count > 0)
        {
            var (directory, inherited) = pending.Pop();
            var rules = GitIgnore.Load(directory, inherited);
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (rules.IsIgnored(file, false)) continue;
                    if (results.Count >= MaxFiles) { truncated = true; return results; }
                    results.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (name.Length == 0 || name[0] == '.' || IgnoredDirectories.Contains(name)) continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    if (rules.IsIgnored(child, true)) continue;
                    pending.Push((child, rules));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    public static string RelativePath(string workspace, string file) =>
        Path.GetRelativePath(workspace, file).Replace('\\', '/');

    public static IReadOnlyList<string> Tokenize(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var tokens = query
            .Split([' ', '\t', '\r', '\n', ',', ';', '(', ')', '[', ']', '{', '}', ':', '"', '\'', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToArray();
        var meaningful = tokens.Where(token => !StopWords.Contains(token)).ToArray();
        return meaningful.Length > 0 ? meaningful : tokens;
    }

    private Dictionary<string, List<CodeSymbol>> ByName
    {
        get
        {
            if (_byName != null) return _byName;
            _byName = new Dictionary<string, List<CodeSymbol>>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in Symbols)
            {
                if (!_byName.TryGetValue(symbol.Name, out var list)) _byName[symbol.Name] = list = [];
                list.Add(symbol);
            }
            return _byName;
        }
    }
}
