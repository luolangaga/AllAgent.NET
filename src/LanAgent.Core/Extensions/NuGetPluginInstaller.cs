using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Versioning;

namespace LanAgent.Core.Extensions;

public sealed record PluginPackage(string Id, string Version, string Description, string Authors, long Downloads);

/// <summary>NuGet V3 search + dependency installation. Never invokes MSBuild, install scripts or package executables.</summary>
public sealed class NuGetPluginInstaller(string dataDirectory, HttpClient? http = null, string source = "https://api.nuget.org/v3/index.json")
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    private readonly string _root = Path.Combine(Path.GetFullPath(dataDirectory), "plugins");
    private static readonly NuGetFramework Target = NuGetFramework.ParseFolder("net8.0");
    private async Task<string> ResourceAsync(string type, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(source, ct));
        var resource = doc.RootElement.GetProperty("resources").EnumerateArray().First(r => r.GetProperty("@type").GetString()!.StartsWith(type, StringComparison.Ordinal));
        return resource.GetProperty("@id").GetString()!;
    }
    public async Task<IReadOnlyList<PluginPackage>> SearchAsync(string query = "", int skip = 0, CancellationToken ct = default)
    {
        var url = await ResourceAsync("SearchQueryService", ct);
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url + "?q=" + Uri.EscapeDataString("tags:LanAgent " + query) + "&take=30&skip=" + Math.Max(0, skip) + "&prerelease=false", ct));
        return doc.RootElement.GetProperty("data").EnumerateArray().Where(p => Tags(p).Contains("LanAgent", StringComparer.OrdinalIgnoreCase)).Select(p => new PluginPackage(p.GetProperty("id").GetString()!, p.GetProperty("version").GetString()!, p.GetProperty("description").GetString() ?? "", p.TryGetProperty("authors", out var a) ? a.ToString() : "", p.TryGetProperty("totalDownloads", out var d) ? d.GetInt64() : 0)).ToArray();
    }
    private static IEnumerable<string> Tags(JsonElement package) => package.TryGetProperty("tags", out var tags) ? tags.ValueKind == JsonValueKind.Array ? tags.EnumerateArray().Select(t => t.GetString() ?? "") : (tags.GetString() ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries) : [];
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
            if (primary && !nuspec.GetTags().Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Contains("LanAgent", StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("包未标注 LanAgent 标签。");
            var groups = reader.GetLibItems().ToArray(); var framework = new FrameworkReducer().GetNearest(Target, groups.Select(g => g.TargetFramework));
            var assets = framework == null ? Array.Empty<string>() : groups.Single(g => g.TargetFramework.Equals(framework)).Items.ToArray();
            var files = assets.Where(f => !Path.GetFileName(f).Equals("LanAgent.Core.dll", StringComparison.OrdinalIgnoreCase)).ToList();
            if (primary)
            {
                files.AddRange(reader.GetFiles().Where(f => f.StartsWith("lanagent/", StringComparison.OrdinalIgnoreCase)));
                if (!files.Any(f => f.EndsWith("lanagent-plugin.json", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("包缺少 lanagent/lanagent-plugin.json。");
            }
            foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var relative = file.StartsWith("lanagent/", StringComparison.OrdinalIgnoreCase) ? file[9..] : file[(file.IndexOf('/', 4) + 1)..];
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
                if (dependency.Id.Equals("LanAgent.Core", StringComparison.OrdinalIgnoreCase)) { if (!dependency.VersionRange.Satisfies(new NuGetVersion(1, 0, 0))) throw new InvalidDataException("不兼容的 LanAgent.Core SDK 版本。"); continue; }
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
