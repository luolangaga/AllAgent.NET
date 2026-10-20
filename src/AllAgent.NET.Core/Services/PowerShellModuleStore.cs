using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Core.Services;

public sealed class PowerShellModuleStore(string sharedDirectory, string? workerPath = null)
{
    public string Root { get; } = Path.Combine(Path.GetFullPath(sharedDirectory), "powershell", "Modules");
    private string CatalogPath => Path.Combine(Path.GetDirectoryName(Root)!, "capabilities.json");
    private readonly string _worker = workerPath ?? Path.Combine(AppContext.BaseDirectory, "AllAgent.NET.PowerShell.exe");
    private string _catalog = "[]";
    /// <summary>The bundled SDK worker this store drives; hosts surface it so a missing component is diagnosable.</summary>
    public string WorkerPath => _worker;
    public bool HasCommand(string module, string command)
    {
        using var doc = JsonDocument.Parse(_catalog);
        return doc.RootElement.EnumerateArray().Any(m => string.Equals(m.GetProperty("name").GetString(), module, StringComparison.OrdinalIgnoreCase)
            && m.GetProperty("error").ValueKind == JsonValueKind.Null && m.GetProperty("commands").EnumerateArray().Any(c => string.Equals(c.GetString(), command, StringComparison.OrdinalIgnoreCase)));
    }
    public string Summary
    {
        get
        {
            using var doc = JsonDocument.Parse(_catalog);
            return JsonSerializer.Serialize(doc.RootElement.EnumerateArray().Select(m => new { name = m.GetProperty("name").GetString(), version = m.GetProperty("version").GetString(), commandCount = m.GetProperty("commands").GetArrayLength(), error = m.GetProperty("error") })
                .OrderBy(m => m.name, StringComparer.Ordinal).ThenBy(m => m.version, StringComparer.Ordinal));
        }
    }
    public object Commands(string module, int skip = 0, int limit = 50)
    {
        if (skip < 0 || limit is < 1 or > 100) throw new ArgumentException("skip >= 0; limit must be 1–100.");
        using var doc = JsonDocument.Parse(_catalog);
        return doc.RootElement.EnumerateArray().Where(m => string.IsNullOrEmpty(module) || string.Equals(m.GetProperty("name").GetString(), module, StringComparison.OrdinalIgnoreCase))
            .Select(m => new { name = m.GetProperty("name").GetString(), version = m.GetProperty("version").GetString(), total = m.GetProperty("commands").GetArrayLength(), commands = m.GetProperty("commands").EnumerateArray().Skip(skip).Take(limit).Select(c => c.GetString()).ToArray(), error = m.GetProperty("error").Clone() }).ToArray();
    }
    public async Task<string> ExecuteAsync(string operation, string? name = null, string? version = null, CancellationToken ct = default)
    {
        if (operation is not ("list" or "search" or "install")) throw new ArgumentException("Use list, search or install.");
        if (operation != "list" && (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, operation == "search" ? @"^[A-Za-z0-9.*_-]{1,128}$" : @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))) throw new ArgumentException("Invalid module name.");
        if (version != null && !Regex.IsMatch(version, @"^\d+\.\d+(?:\.\d+){0,2}$")) throw new ArgumentException("Use an exact stable version, e.g. 5.7.1.");
        if (operation != "list") await BootstrapAsync(ct);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { operation, name, version })));
        var info = BoundedProcess.StartInfo(_worker, ["--modules", payload]);
        info.Environment["ALLAGENT_PS_MODULE_ROOT"] = Root;
        ProcessOutput result;
        try { result = await BoundedProcess.RunAsync(info, TimeSpan.FromMinutes(5), ct, limit: 2_000_000); }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(operation == "search"
                ? "连接 PowerShell Gallery 超时（5 分钟）。请检查网络、系统代理或 HTTPS_PROXY 后重试。"
                : "安装 PowerShell 模块超时（5 分钟）。请检查网络后重试，已下载的部分不会注册为能力。");
        }
        if (result.ExitCode != 0 || result.Truncated) throw new InvalidOperationException("PowerShell module operation failed: " + result.Stderr + (result.Truncated ? " Catalog exceeds size limit." : ""));
        using var parsed = JsonDocument.Parse(result.Stdout);
        if (parsed.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Module worker returned an invalid catalog.");
        if (operation != "search")
        {
            _catalog = result.Stdout;
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
            var temp = CatalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, _catalog, ct);
            File.Move(temp, CatalogPath, true);
            if (operation == "install")
            {
                var matches = parsed.RootElement.EnumerateArray().Where(m => string.Equals(m.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 0 || matches.All(m => m.GetProperty("error").ValueKind != JsonValueKind.Null))
                    throw new InvalidOperationException("Module downloaded, but import validation failed. Run powershell list for details; check PowerShell 7 compatibility and module dependencies.");
            }
        }
        return result.Stdout;
    }
    public void LoadCatalog()
    {
        if (!File.Exists(CatalogPath)) return;
        var json = File.ReadAllText(CatalogPath);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array) _catalog = json;
    }
    private async Task BootstrapAsync(CancellationToken ct)
    {
        var target = Path.Combine(Root, "Microsoft.PowerShell.PSResourceGet", "1.2.0");
        if (File.Exists(Path.Combine(target, "Microsoft.PowerShell.PSResourceGet.psd1"))) return;
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var response = await client.GetAsync("https://www.powershellgallery.com/api/v2/package/Microsoft.PowerShell.PSResourceGet/1.2.0", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) { if (bytes.Length + read > 50_000_000) throw new IOException("Module manager package exceeds 50 MB."); await bytes.WriteAsync(buffer.AsMemory(0, read), ct); }
        bytes.Position = 0;
        using var zip = new ZipArchive(bytes);
        if (zip.Entries.Count > 3000 || zip.Entries.Sum(e => e.Length) > 150_000_000) throw new IOException("Module manager archive exceeds extraction limits.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var stage = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var entry in zip.Entries)
            {
                var relative = entry.FullName.Replace('\\', '/');
                if (relative.Split('/').Any(part => part is "." or ".." || part.Contains(':')) || relative.StartsWith('/')) throw new IOException("Unsafe module archive path.");
                if (relative.EndsWith('/')) continue;
                var file = Path.GetFullPath(Path.Combine(stage, relative));
                if (!file.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Module archive escaped staging directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                entry.ExtractToFile(file);
            }
            if (!File.Exists(Path.Combine(stage, "Microsoft.PowerShell.PSResourceGet.psd1"))) throw new IOException("Module manager manifest missing.");
            if (!Directory.Exists(target)) Directory.Move(stage, target);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
