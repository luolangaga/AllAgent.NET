using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Versioning;

namespace AllAgent.NET.Core.Extensions;

public sealed record PluginPackage(string Id, string Version, string Description, string Authors, long Downloads);

public sealed record PluginSearchResult(long TotalHits, IReadOnlyList<PluginPackage> Packages);

/// <summary>NuGet V3 search + dependency installation. Never invokes MSBuild, install scripts or package executables.</summary>
public sealed class NuGetPluginInstaller(string dataDirectory, HttpClient? http = null, string source = "https://api.nuget.org/v3/index.json")
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    private readonly string _root = Path.Combine(Path.GetFullPath(dataDirectory), "plugins");
    private static readonly NuGetFramework Target = NuGetFramework.ParseFolder("net10.0");
    private static readonly NuGetVersion SdkVersion = NuGetVersion.Parse(
        typeof(NuGetPluginInstaller).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(NuGetPluginInstaller).Assembly.GetName().Version!.ToString(3));
    // Keep the short tag and the legacy LanAgent tag accepted while AllAgent.NET is canonical.
    private static readonly string[] AcceptedPluginTags = ["AllAgent.NET", "AllAgent", "LanAgent"];
    private async Task<string> ResourceAsync(string type, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(source, ct));
        var candidates = doc.RootElement.GetProperty("resources").EnumerateArray()
            .Select(r => (Type: r.GetProperty("@type").GetString() ?? "", Url: r.GetProperty("@id").GetString() ?? ""))
            .Where(r => r.Type.StartsWith(type, StringComparison.Ordinal) && r.Url.Length > 0)
            .ToArray();
        // Prefer the newest stable service version; a prerelease resource may be listed first.
        var stable = candidates.Where(r => !r.Type.Contains('-')).OrderByDescending(r => r.Type, StringComparer.Ordinal).ToArray();
        return (stable.Length > 0 ? stable : candidates.OrderByDescending(r => r.Type, StringComparer.Ordinal).ToArray()).First().Url;
    }
    // The NuGet search service does not understand parentheses: wrapping the tag filter in
    // them collapses the field syntax and turns the query into a full-text search that
    // matches nothing useful. Keep the tag OR chain bare and append keywords with a space.
    private string TagQuery => string.Join(" OR ", AcceptedPluginTags.Select(tag => $"tags:{tag}"));
    public async Task<PluginSearchResult> SearchAsync(string query = "", int skip = 0, int take = 30, CancellationToken ct = default)
    {
        var url = await ResourceAsync("SearchQueryService", ct);
        var keyword = (query ?? string.Empty).Trim();
        var count = Math.Clamp(take, 1, 100);
        var offset = Math.Max(0, skip);
        async Task<(long Total, IReadOnlyList<PluginPackage> Items)> FetchAsync(string queryText, int pageSkip, int pageTake)
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(
                url + "?q=" + Uri.EscapeDataString(queryText) + "&take=" + pageTake + "&skip=" + pageSkip + "&prerelease=false", ct));
            var total = doc.RootElement.TryGetProperty("totalHits", out var hits) ? hits.GetInt64() : 0;
            var items = doc.RootElement.GetProperty("data").EnumerateArray().Where(p => HasPluginTag(Tags(p))).Select(p =>
                new PluginPackage(p.GetProperty("id").GetString()!, p.GetProperty("version").GetString()!, p.GetProperty("description").GetString() ?? "", p.TryGetProperty("authors", out var a) ? a.ToString() : "", p.TryGetProperty("totalDownloads", out var d) ? d.GetInt64() : 0)).ToArray();
            return (total, items);
        }
        var (total, packages) = await FetchAsync(keyword.Length == 0 ? TagQuery : TagQuery + " " + keyword, offset, count);
        if (keyword.Length == 0 || total > 0) return new PluginSearchResult(total, packages);
        // Chinese keywords and partial ids often tokenize to zero hits server-side; fall back
        // to filtering the complete tag listing locally so any substring still matches.
        var (_, all) = await FetchAsync(TagQuery, 0, 100);
        var matches = all.Where(p => p.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || p.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || p.Authors.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new PluginSearchResult(matches.Length, matches.Skip(offset).Take(count).ToArray());
    }
    private static IEnumerable<string> Tags(JsonElement package) => package.TryGetProperty("tags", out var tags) ? tags.ValueKind == JsonValueKind.Array ? tags.EnumerateArray().Select(t => t.GetString() ?? "") : SplitTags(tags.GetString()) : [];
    private static IEnumerable<string> SplitTags(string? tags) => (tags ?? string.Empty).Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
    private static bool HasPluginTag(IEnumerable<string> tags) => tags.Any(tag => AcceptedPluginTags.Contains(tag, StringComparer.OrdinalIgnoreCase));
    public async Task<InstalledPlugin> ImportAsync(string packagePath, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(packagePath); if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("包超过 64MB。");
        using var reader = new PackageArchiveReader(path); return await InstallAsync(reader.NuspecReader.GetId(), reader.NuspecReader.GetVersion().ToNormalizedString(), ct, path);
    }
    public async Task<InstalledPlugin> InstallAsync(string id, string version, CancellationToken ct = default, string? localPackage = null)
    {
        Validate(id); var parsed = NuGetVersion.Parse(version);
        string? baseUrl = null;
        Directory.CreateDirectory(_root);
        var staging = Path.Combine(_root, ".staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        var resolved = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        var hashes = new Dictionary<string, string>();
        async Task Download(string packageId, NuGetVersion packageVersion, bool primary)
        {
            ct.ThrowIfCancellationRequested(); Validate(packageId);
            if (resolved.TryGetValue(packageId, out var existing)) { if (existing != packageVersion) throw new InvalidOperationException("插件依赖版本冲突：" + packageId); return; }
            if (resolved.Count >= 64) throw new InvalidDataException("依赖包超过 64 个。"); resolved[packageId] = packageVersion;
            var lower = packageId.ToLowerInvariant(); var v = packageVersion.ToNormalizedString().ToLowerInvariant();
            if (localPackage == null || !primary) baseUrl ??= await ResourceAsync("PackageBaseAddress", ct);
            using var response = localPackage != null && primary ? null : await _http.GetAsync(baseUrl!.TrimEnd('/') + $"/{lower}/{v}/{lower}.{v}.nupkg", HttpCompletionOption.ResponseHeadersRead, ct); response?.EnsureSuccessStatusCode();
            await using var stream = response == null ? File.OpenRead(localPackage!) : await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var bytes = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(bytes, ct)) > 0) { if (buffer.Length + read > 64 * 1024 * 1024) throw new InvalidDataException("包超过 64MB。"); await buffer.WriteAsync(bytes.AsMemory(0, read), ct); }
            hashes[packageId + "@" + v] = Convert.ToHexString(SHA256.HashData(buffer.ToArray())); buffer.Position = 0;
            using var reader = new PackageArchiveReader(buffer, leaveStreamOpen: true); var nuspec = reader.NuspecReader;
            if (!nuspec.GetId().Equals(packageId, StringComparison.OrdinalIgnoreCase) || nuspec.GetVersion() != packageVersion) throw new InvalidDataException("包标识与请求不一致。");
            if (primary && !HasPluginTag(SplitTags(nuspec.GetTags()))) throw new InvalidDataException("包未标注 AllAgent.NET 或兼容的 LanAgent 插件标签。");
            var groups = reader.GetLibItems().ToArray(); var framework = new FrameworkReducer().GetNearest(Target, groups.Select(g => g.TargetFramework));
            var assets = framework == null ? Array.Empty<string>() : groups.Single(g => g.TargetFramework.Equals(framework)).Items.ToArray();
            var files = assets.Where(f => !Path.GetFileName(f).Equals("AllAgent.NET.Core.dll", StringComparison.OrdinalIgnoreCase)).ToList();
            if (primary)
            {
                files.AddRange(reader.GetFiles().Where(f => f.StartsWith("allagent/", StringComparison.OrdinalIgnoreCase)));
                if (!files.Any(f => f.EndsWith("allagent-plugin.json", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("包缺少 allagent/allagent-plugin.json。");
            }
            foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var relative = file.StartsWith("allagent/", StringComparison.OrdinalIgnoreCase) ? file[9..] : file[(file.IndexOf('/', 4) + 1)..];
                var target = Path.GetFullPath(Path.Combine(staging, relative));
                if (relative.Contains(':') || !target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("包路径越界。");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = reader.GetStream(file); using var content = new MemoryStream();
                while ((read = await input.ReadAsync(bytes, ct)) > 0) { expanded += read; if (expanded > 256 * 1024 * 1024) throw new InvalidDataException("展开文件超过 256MB。"); await content.WriteAsync(bytes.AsMemory(0, read), ct); }
                if (File.Exists(target)) { if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(content.ToArray())) throw new InvalidDataException("依赖文件冲突：" + relative); }
                else await File.WriteAllBytesAsync(target, content.ToArray(), ct);
            }
            var dependencyGroups = nuspec.GetDependencyGroups().ToArray(); var depFramework = new FrameworkReducer().GetNearest(Target, dependencyGroups.Select(g => g.TargetFramework));
            var dependencies = depFramework == null ? [] : dependencyGroups.Single(g => g.TargetFramework.Equals(depFramework)).Packages;
            foreach (var dependency in dependencies)
            {
                // SDK contract is shared with the host; never install a second Core assembly.
                if (dependency.Id.Equals("AllAgent.NET.Core", StringComparison.OrdinalIgnoreCase)) { if (!dependency.VersionRange.Satisfies(SdkVersion)) throw new InvalidDataException($"不兼容的 AllAgent.NET.Core SDK 版本：宿主 {SdkVersion}，插件要求 {dependency.VersionRange}。"); continue; }
                if (resolved.TryGetValue(dependency.Id, out var already)) { if (!dependency.VersionRange.Satisfies(already)) throw new InvalidDataException("依赖约束冲突：" + dependency.Id); continue; }
                baseUrl ??= await ResourceAsync("PackageBaseAddress", ct);
                using var index = JsonDocument.Parse(await _http.GetStringAsync(baseUrl.TrimEnd('/') + "/" + dependency.Id.ToLowerInvariant() + "/index.json", ct));
                var candidate = index.RootElement.GetProperty("versions").EnumerateArray().Select(e => NuGetVersion.Parse(e.GetString()!)).Where(vv => !vv.IsPrerelease && dependency.VersionRange.Satisfies(vv)).OrderBy(vv => vv).FirstOrDefault() ?? throw new InvalidDataException("没有兼容依赖：" + dependency.Id);
                await Download(dependency.Id, candidate, false);
            }
        }
        try
        {
            await Download(id, parsed, true);
            await File.WriteAllTextAsync(Path.Combine(staging, "packages.lock.json"), JsonSerializer.Serialize(hashes, new JsonSerializerOptions { WriteIndented = true }), ct);
            var folder = id.ToLowerInvariant() + "-" + parsed.ToNormalizedString() + "-" + Guid.NewGuid().ToString("N")[..8]; var destination = Path.Combine(_root, folder);
            Directory.Move(staging, destination); return new(id, parsed.ToNormalizedString(), folder);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private static void Validate(string id) { if (!Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$")) throw new ArgumentException("包 ID 无效。"); }
}
