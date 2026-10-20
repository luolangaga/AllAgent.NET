using System.Runtime.InteropServices;

namespace AllAgent.NET.CodeGraph;

/// <summary>Preloads the native tree-sitter grammars from the plugin directory. The bindings
/// only probe AppContext.BaseDirectory, and Windows reuses an already-loaded module by base
/// name, so loading each DLL by full path makes the later by-name lookup succeed.</summary>
internal static class TreeSitterNative
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> GrammarLibraries = new(StringComparer.Ordinal)
    {
        ["C#"] = "tree-sitter-c-sharp",
        ["TypeScript"] = "tree-sitter-typescript",
        ["TSX"] = "tree-sitter-tsx",
        ["JavaScript"] = "tree-sitter-javascript",
        ["Python"] = "tree-sitter-python",
    };
    private static readonly HashSet<string> Loaded = new(StringComparer.Ordinal);
    private static bool _initialized;
    private static string? _error;

    public static string? Error
    {
        get { EnsureLoaded(); lock (Gate) return _error; }
    }

    public static bool EnsureLoaded()
    {
        lock (Gate)
        {
            if (_initialized) return _error == null;
            _initialized = true;
            var directories = CandidateDirectories().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!TryLoad("tree-sitter", directories, out var coreError))
            {
                _error = coreError;
                return false;
            }
            foreach (var (grammar, library) in GrammarLibraries)
            {
                if (TryLoad(library, directories, out var error)) Loaded.Add(grammar);
                else _error ??= error;
            }
            return _error == null;
        }
    }

    public static bool IsGrammarAvailable(string grammar)
    {
        EnsureLoaded();
        lock (Gate) return Loaded.Contains(grammar);
    }

    private static bool TryLoad(string library, string[] directories, out string? error)
    {
        var fileName = library + ".dll";
        foreach (var directory in directories)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) continue;
            try
            {
                NativeLibrary.Load(path);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                error = $"无法加载 {fileName}：{ex.Message}";
                return false;
            }
        }
        error = $"缺少 {fileName}";
        return false;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var assembly = Path.GetDirectoryName(typeof(TreeSitterNative).Assembly.Location);
        if (!string.IsNullOrEmpty(assembly)) yield return Path.Combine(assembly, "native", "win-x64");
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        yield return Path.Combine(AppContext.BaseDirectory, "native", "win-x64");
        yield return AppContext.BaseDirectory;
    }
}
