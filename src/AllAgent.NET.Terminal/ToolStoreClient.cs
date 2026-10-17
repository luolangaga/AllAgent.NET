using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services;

namespace AllAgent.NET.Terminal;

internal sealed record StoreItem(string Id, string Name, string Source, string Description, string Url, JsonElement Data);
internal sealed record StoreResults(IReadOnlyList<StoreItem> Items, string? NextCursor, string Basis = "");

// No seeds, cached fallback results or synthetic entries: failures propagate to the page.
internal sealed class ToolStoreClient(HttpClient http)
{
    public static HttpClient CreateHttp() { var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("AllAgent-Store/1.0"); return client; }
    public async Task<StoreResults> RecommendSkills(CancellationToken ct = default)
    {
        var trending = await Search(false, "", ct: ct);
        return new(trending.Items.Take(12).ToArray(), null, "skills.sh 实时 24 小时趋势榜 · 保留目录排序");
    }
    public async Task<StoreResults> Search(bool mcp, string query, string? cursor = null, CancellationToken ct = default)
    {
        var url = mcp ? "https://registry.modelcontextprotocol.io/v0.1/servers?version=latest&limit=30&search=" + Uri.EscapeDataString(query) + (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))
            : string.IsNullOrWhiteSpace(query) ? "https://skills.sh/api/skills/trending/0" : "https://skills.sh/api/search?limit=30&q=" + Uri.EscapeDataString(query);
        using var doc = JsonDocument.Parse(await Download(url, 5_000_000, ct));
        var root = doc.RootElement;
        var array = root.GetProperty(mcp ? "servers" : "skills");
        var items = new List<StoreItem>();
        foreach (var entry in array.EnumerateArray())
        {
            var data = mcp ? entry.GetProperty("server") : entry;
            if (mcp && entry.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("io.modelcontextprotocol.registry/official", out var official) && official.TryGetProperty("status", out var status) && status.GetString() != "active") continue;
            var id = mcp ? Text(data, "name") : Text(data, "id", Text(data, "source") + "/" + Text(data, "skillId", Text(data, "slug", Text(data, "name"))));
            var source = mcp ? id : Text(data, "source");
            var name = mcp ? Text(data, "title", id) : Text(data, "name");
            var link = mcp ? (data.TryGetProperty("repository", out var repo) ? Text(repo, "url") : Text(data, "websiteUrl", "https://registry.modelcontextprotocol.io")) : "https://skills.sh/" + id;
            items.Add(new(id, name, source, Text(data, "description", mcp ? "" : "Agent Skill · " + source), link, data.Clone()));
        }
        return new(items, root.TryGetProperty("metadata", out var md) ? Text(md, "nextCursor", "") is { Length: > 0 } next ? next : null : null);
    }
    internal static string Text(JsonElement item, string key, string fallback = "") => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private async Task<byte[]> Download(string url, int limit, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
        var buffer = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) { if (output.Length + read > limit) throw new IOException("目录或下载文件超过大小限制。"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
        return output.ToArray();
    }
    public async Task<string> InstallSkill(StoreItem item, SkillCatalog catalog, string scope, CancellationToken ct)
    {
        if (!Regex.IsMatch(item.Source, @"^[\w.-]+/[\w.-]+$")) throw new ArgumentException("此技能不是 GitHub 来源，请查看发布源安装说明。");
        var slug = Text(item.Data, "skillId", Text(item.Data, "slug", item.Name)); SkillCatalog.ValidateName(slug);
        var bytes = await Download("https://api.github.com/repos/" + item.Source + "/zipball", 30_000_000, ct);
        using var archive = new ZipArchive(new MemoryStream(bytes));
        var matches = archive.Entries.Where(e => e.FullName.EndsWith("/" + slug + "/SKILL.md", StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) throw new IOException("仓库中未唯一找到该技能的 SKILL.md；没有安装任何内容，请查看来源。");
        var prefix = matches[0].FullName[..^"SKILL.md".Length];
        var entries = archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !e.FullName.EndsWith('/')).ToArray();
        if (entries.Length > 500 || entries.Sum(e => e.Length) > 20_000_000) throw new IOException("技能包超过 500 文件或 20 MB。");
        var temp = Path.Combine(Path.GetTempPath(), "allagent-store-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            foreach (var entry in entries)
            {
                var relative = entry.FullName[prefix.Length..];
                if (relative.Contains('\\') || relative.Split('/').Any(p => p is ".." or "." or "" || p.Contains(':'))) throw new IOException("技能包包含不安全路径。");
                var target = Path.GetFullPath(Path.Combine(temp, relative));
                if (!target.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("技能包路径越界。");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target);
            }
            var parsed = SkillCatalog.Parse(Path.Combine(temp, "SKILL.md"), scope);
            if (parsed.Name != slug) throw new IOException("技能名称与目录结果不一致。");
            return catalog.Import(temp, scope).Name;
        }
        finally { Directory.Delete(temp, true); }
    }
    public static McpServerConfig McpConfig(StoreItem item)
    {
        var config = new McpServerConfig { Name = item.Id, IsEnabled = false };
        if (item.Data.TryGetProperty("remotes", out var remotes))
            foreach (var remote in remotes.EnumerateArray())
                if (Text(remote, "type") == "streamable-http" && Uri.TryCreate(Text(remote, "url"), UriKind.Absolute, out var uri) && uri.Scheme == "https")
                {
                    config.Command = uri.AbsoluteUri; config.Transport = "http";
                    if (remote.TryGetProperty("headers", out var headers)) foreach (var header in headers.EnumerateArray()) config.Headers[Text(header, "name")] = Text(header, "value", "${" + Text(header, "name") + "}");
                    return config;
                }
        if (item.Data.TryGetProperty("packages", out var packages))
            foreach (var package in packages.EnumerateArray())
                if (Text(package, "registryType") == "npm" && package.TryGetProperty("transport", out var transport) && Text(transport, "type") == "stdio")
                {
                    var id = Text(package, "identifier"); var version = Text(package, "version");
                    if (!Regex.IsMatch(id, @"^(?:@[a-z0-9._-]+/)?[a-z0-9][a-z0-9._-]*$") || !Regex.IsMatch(version, @"^\d[\w.+-]*$")) throw new IOException("目录中的 npm 包或版本无效。");
                    config.Command = "npx"; config.Transport = "stdio";
                    var args = new List<string> { "-y", id + "@" + version };
                    foreach (var key in new[] { "runtimeArguments", "packageArguments" })
                        if (package.TryGetProperty(key, out var arguments)) foreach (var arg in arguments.EnumerateArray()) { if (Text(arg, "type") == "named") args.Add(Text(arg, "name")); args.Add(Text(arg, "value", "<请填写:" + Text(arg, "name", "参数") + ">")); }
                    config.ArgumentList = args.ToArray();
                    if (package.TryGetProperty("environmentVariables", out var env)) foreach (var variable in env.EnumerateArray()) config.EnvironmentVariables[Text(variable, "name")] = Text(variable, "value", "${" + Text(variable, "name") + "}");
                    return config;
                }
        throw new ArgumentException("此服务没有支持的 HTTPS 或 npm stdio 入口，请查看发布源说明。");
    }
}
