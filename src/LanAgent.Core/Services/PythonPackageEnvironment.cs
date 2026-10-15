using System.Text.Json;
using System.Text.RegularExpressions;
using LanAgent.Core.Runtime;

namespace LanAgent.Core.Services;

public sealed class PythonPackageEnvironment(string directory)
{
    public static IReadOnlyList<string> Defaults { get; } = ["requests", "beautifulsoup4", "Pillow", "PyYAML", "numpy", "pandas", "openpyxl", "python-docx", "pypdf"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Root { get; } = Path.GetFullPath(directory);
    public string PythonPath => Path.Combine(Root, "python", "venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
    public string RegistryPath => Path.Combine(Root, "python", "libraries.json");
    public string CatalogPath => Path.Combine(Root, "pip.md");
    public bool Ready => File.Exists(PythonPath) && File.Exists(RegistryPath) && File.Exists(CatalogPath);
    public static bool ValidRequirement(string value) => Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._-]*(?:\[[A-Za-z0-9_,.-]+\])?(?:(?:==|>=|<=|~=|>|<)[A-Za-z0-9.*+_-]+)?$");

    public async Task EnsureAsync(string python, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Ready) return;
            Directory.CreateDirectory(Path.Combine(Root, "python"));
            if (!File.Exists(PythonPath)) await RunAsync(python, ["-m", "venv", Path.Combine(Root, "python", "venv")], ct);
            await RunAsync(PythonPath, ["-m", "pip", "install", "--disable-pip-version-check", "--no-input", .. Defaults], ct);
            await RefreshCoreAsync(ct);
        }
        finally { _gate.Release(); }
    }
    public async Task InstallAsync(IEnumerable<string> requirements, CancellationToken ct = default)
    {
        var packages = requirements.ToArray();
        if (packages.Length == 0 || packages.Any(p => !ValidRequirement(p))) throw new ArgumentException("请填写 PyPI 包名，可带版本，例如 requests==2.32.5；不接受 pip 开关或脚本路径。");
        await _gate.WaitAsync(ct);
        try
        {
            if (!Ready) throw new InvalidOperationException("Python 库环境尚未初始化；运行 /doctor install。");
            try { await RunAsync(PythonPath, ["-m", "pip", "install", "--disable-pip-version-check", "--no-input", .. packages], ct); }
            finally { await RefreshCoreAsync(CancellationToken.None); }
        }
        finally { _gate.Release(); }
    }
    public Task RefreshAsync(CancellationToken ct = default) => RefreshLockedAsync(ct);
    private async Task RefreshLockedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct); try { await RefreshCoreAsync(ct); } finally { _gate.Release(); }
    }
    private Task RefreshCoreAsync(CancellationToken ct) => RunAsync(PythonPath, ["-c", RuntimeScripts.Read("python_catalog.py"), Root], ct);
    private static async Task RunAsync(string python, IEnumerable<string> arguments, CancellationToken ct)
    {
        var info = BoundedProcess.StartInfo(python, arguments); info.Environment["PYTHONIOENCODING"] = "utf-8";
        var result = await BoundedProcess.RunAsync(info, TimeSpan.FromMinutes(15), ct);
        if (result.ExitCode != 0) throw new InvalidOperationException("Python 库环境操作失败：" + result.Stderr + result.Stdout);
    }
    public JsonElement Catalog()
    {
        if (!File.Exists(RegistryPath)) throw new InvalidOperationException("Python 库目录尚未生成。");
        return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(RegistryPath));
    }
    public IReadOnlyDictionary<string, string> Aliases() => Catalog().GetProperty("aliases").Deserialize<Dictionary<string, string>>()!;
    public string ReadDocument(string package)
    {
        var entry = Catalog().GetProperty("packages").EnumerateArray().FirstOrDefault(item => Normalize(item.GetProperty("name").GetString()!) == Normalize(package));
        if (entry.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("该库未安装。");
        if (entry.GetProperty("document").ValueKind == JsonValueKind.Null) return "这是自带库，没有额外文档；可使用已知 API，遇到不确定用法请查官方文档。";
        var path = Path.GetFullPath(Path.Combine(Root, entry.GetProperty("document").GetString()!));
        var docs = Path.Combine(Root, "python", "docs") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(docs, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("文档路径不在库文档目录内。");
        return File.ReadAllText(path);
    }
    private static string Normalize(string package) => Regex.Replace(package, "[-_.]+", "-").ToLowerInvariant();
}
