using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NuGet.Versioning;

namespace AllAgent.NET.Core.Services.Updates;

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

public interface IUpdateReleaseSource
{
    UpdateSource Source { get; }
    Task<UpdateRelease?> GetLatestAsync(UpdateChannel channel, CancellationToken ct);
    Task<UpdateRelease?> GetByVersionAsync(string version, CancellationToken ct);
}

public sealed class GitHubUpdateSource(HttpClient http, string repository, string? token = null) : IUpdateReleaseSource
{
    public UpdateSource Source => UpdateSource.GitHub;

    public async Task<UpdateRelease?> GetLatestAsync(UpdateChannel channel, CancellationToken ct)
    {
        var repositoryPath = NormalizeRepository(repository);
        if (channel == UpdateChannel.Stable)
        {
            using var document = await GetJsonAsync($"https://api.github.com/repos/{repositoryPath}/releases/latest", ct);
            return document == null ? null : ParseRelease(document.RootElement);
        }

        using var list = await GetJsonAsync($"https://api.github.com/repos/{repositoryPath}/releases?per_page=30", ct);
        if (list == null) return null;
        return list.RootElement.EnumerateArray()
            .Select(ParseRelease)
            .Where(release => release != null)
            .Select(release => release!)
            .Where(release => release.Prerelease)
            .OrderByDescending(release => VersionOrZero(release.Version))
            .FirstOrDefault();
    }

    public async Task<UpdateRelease?> GetByVersionAsync(string version, CancellationToken ct)
    {
        var repositoryPath = NormalizeRepository(repository);
        foreach (var tag in new[] { "v" + UpdateVersion.Normalize(version), UpdateVersion.Normalize(version) })
        {
            using var document = await GetJsonAsync($"https://api.github.com/repos/{repositoryPath}/releases/tags/{Uri.EscapeDataString(tag)}", ct);
            if (document != null) return ParseRelease(document.RootElement);
        }
        return null;
    }

    private UpdateRelease? ParseRelease(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var tag = String(element, "tag_name");
        var name = String(element, "name") ?? tag;
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var version = tag.TrimStart('v', 'V');
        var assets = element.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array
            ? assetArray.EnumerateArray()
                .Select(asset => new UpdateAsset(
                    String(asset, "name") ?? "",
                    String(asset, "browser_download_url") ?? "",
                    asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : null,
                    String(asset, "digest")))
                .Where(asset => asset.Name.Length > 0 && asset.Url.Length > 0)
                .ToArray()
            : [];
        return new UpdateRelease(
            version,
            tag,
            name ?? tag,
            String(element, "body") ?? "",
            String(element, "html_url") ?? $"https://github.com/{repository}/releases/tag/{tag}",
            UpdateSource.GitHub,
            assets,
            element.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True);
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new UpdateException("GitHub 暂时拒绝请求（可能是速率限制）。请稍后重试，或设置 ALLAGENT_GITHUB_TOKEN。");
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"GitHub 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string NormalizeRepository(string value) => value.Trim().Trim('/');
    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static NuGetVersion VersionOrZero(string value) => UpdateVersion.TryParse(value, out var parsed) ? parsed : new NuGetVersion(0, 0, 0);
}

public sealed class GitCodeUpdateSource(HttpClient http, string repository, string? token = null) : IUpdateReleaseSource
{
    public UpdateSource Source => UpdateSource.GitCode;

    public async Task<UpdateRelease?> GetLatestAsync(UpdateChannel channel, CancellationToken ct)
    {
        var repositoryPath = NormalizeRepository(repository);
        using var document = await GetJsonAsync($"https://api.gitcode.com/api/v5/repos/{repositoryPath}/releases/latest?type=latest", ct);
        if (document == null) return null;
        var release = ParseRelease(document.RootElement, repositoryPath);
        if (release == null) return null;
        return channel == UpdateChannel.Preview || !release.Prerelease ? release : null;
    }

    public async Task<UpdateRelease?> GetByVersionAsync(string version, CancellationToken ct)
    {
        var repositoryPath = NormalizeRepository(repository);
        var normalized = UpdateVersion.Normalize(version);
        foreach (var tag in new[] { "v" + normalized, normalized })
        {
            using var document = await GetJsonAsync($"https://api.gitcode.com/api/v5/repos/{repositoryPath}/releases/tags/{Uri.EscapeDataString(tag)}", ct);
            if (document != null) return ParseRelease(document.RootElement, repositoryPath);
        }
        return null;
    }

    private UpdateRelease? ParseRelease(JsonElement element, string repositoryPath)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var tag = String(element, "tag_name");
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var name = String(element, "name") ?? tag;
        var assets = element.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array
            ? assetArray.EnumerateArray()
                .Select(asset => new UpdateAsset(
                    String(asset, "name") ?? "",
                    String(asset, "browser_download_url") ?? ""))
                .Where(asset => asset.Name.Length > 0 && asset.Url.Length > 0)
                .ToArray()
            : [];
        var status = String(element, "release_status");
        var prerelease = element.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True ||
                         string.Equals(status, "pre", StringComparison.OrdinalIgnoreCase);
        return new UpdateRelease(
            tag.TrimStart('v', 'V'),
            tag,
            name,
            String(element, "body") ?? "",
            $"https://gitcode.com/{repositoryPath}/-/releases/{Uri.EscapeDataString(tag)}",
            UpdateSource.GitCode,
            assets,
            prerelease);
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UpdateException(token == null
                ? "GitCode 需要访问令牌。请设置 ALLAGENT_GITCODE_TOKEN。"
                : "GitCode 拒绝了访问令牌。");
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"GitCode 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string NormalizeRepository(string value) => value.Trim().Trim('/');
    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed class NuGetUpdateSource(HttpClient http, string packageId) : IUpdateReleaseSource
{
    public UpdateSource Source => UpdateSource.NuGet;

    public async Task<UpdateRelease?> GetLatestAsync(UpdateChannel channel, CancellationToken ct)
    {
        using var document = await GetVersionsAsync(ct);
        if (document == null) return null;
        var versions = document.RootElement.TryGetProperty("versions", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(value => value.GetString()).Where(value => value != null).Select(value => value!).ToArray()
            : [];
        var latest = versions
            .Select(value => UpdateVersion.TryParse(value, out var parsed) ? parsed : null)
            .Where(value => value != null)
            .Select(value => value!)
            .Where(value => channel == UpdateChannel.Preview || !value.IsPrerelease)
            .OrderByDescending(value => value)
            .FirstOrDefault();
        return latest == null ? null : ForVersion(latest.ToNormalizedString());
    }

    public async Task<UpdateRelease?> GetByVersionAsync(string version, CancellationToken ct)
    {
        using var document = await GetVersionsAsync(ct);
        if (document == null) return null;
        var normalized = UpdateVersion.Normalize(version);
        var exists = document.RootElement.TryGetProperty("versions", out var array) &&
                     array.ValueKind == JsonValueKind.Array &&
                     array.EnumerateArray().Any(value => string.Equals(value.GetString(), normalized, StringComparison.OrdinalIgnoreCase));
        return exists ? ForVersion(normalized) : null;
    }

    private UpdateRelease ForVersion(string version) => new(
        version,
        version,
        packageId + " " + version,
        "",
        $"https://www.nuget.org/packages/{packageId}/{version}",
        UpdateSource.NuGet,
        [],
        UpdateVersion.TryParse(version, out var parsed) && parsed.IsPrerelease);

    private async Task<JsonDocument?> GetVersionsAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"https://api.nuget.org/v3-flatcontainer/{Uri.EscapeDataString(packageId.ToLowerInvariant())}/index.json",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"NuGet 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}

public sealed class UpdateReleaseSourceFactory(HttpClient http, string? gitHubRepository, string? gitCodeRepository, string? gitHubToken, string? gitCodeToken)
{
    public static string DefaultGitHubRepository { get; } = "luolangaga/AllAgent.NET";
    public static string DefaultGitCodeRepository { get; } = "luolangaga/AllAgent.NET";

    public IUpdateReleaseSource Create(UpdateSource source, string? nugetPackageId)
    {
        return source switch
        {
            UpdateSource.GitHub => new GitHubUpdateSource(http, gitHubRepository ?? DefaultGitHubRepository, gitHubToken),
            UpdateSource.GitCode => new GitCodeUpdateSource(http, gitCodeRepository ?? DefaultGitCodeRepository, gitCodeToken),
            UpdateSource.NuGet => new NuGetUpdateSource(http, nugetPackageId ?? throw new UpdateException("无法确定 NuGet 包 ID。")),
            _ => throw new UpdateException("请选择更新来源。"),
        };
    }

    public IEnumerable<UpdateSource> AutoSources(UpdateInstallContext context) => context.Kind == UpdateInstallKind.NuGet
        ? [UpdateSource.NuGet]
        : [UpdateSource.GitCode, UpdateSource.GitHub];

    public async Task<UpdateRelease?> GetLatestAsync(UpdateSource source, UpdateChannel channel, UpdateInstallContext context, CancellationToken ct)
    {
        if (source == UpdateSource.Auto)
        {
            var errors = new List<string>();
            foreach (var candidate in AutoSources(context))
            {
                try
                {
                    var release = await Create(candidate, context.NuGetPackageId).GetLatestAsync(channel, ct);
                    // A release with no assets cannot install an installer/portable build;
                    // treat it as unavailable and let the next source answer.
                    if (release != null && (context.Kind == UpdateInstallKind.NuGet || release.Assets.Count > 0)) return release;
                }
                catch (Exception ex) when (ex is UpdateException or HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
                {
                    errors.Add(candidate + ": " + ex.Message);
                }
            }
            if (errors.Count > 0)
                throw new UpdateException("检查更新失败：" + string.Join("；", errors));
            return null;
        }
        return await Create(source, context.NuGetPackageId).GetLatestAsync(channel, ct);
    }

    public async Task<UpdateRelease?> GetByVersionAsync(UpdateSource source, string version, UpdateInstallContext context, CancellationToken ct)
    {
        if (source != UpdateSource.Auto)
            return await Create(source, context.NuGetPackageId).GetByVersionAsync(version, ct);
        foreach (var candidate in AutoSources(context))
        {
            try
            {
                var release = await Create(candidate, context.NuGetPackageId).GetByVersionAsync(version, ct);
                if (release != null) return release;
            }
            catch (Exception ex) when (ex is UpdateException or HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { }
        }
        return null;
    }
}
