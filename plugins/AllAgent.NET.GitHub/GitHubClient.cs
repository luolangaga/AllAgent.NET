using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AllAgent.NET.GitHub;

public sealed record GitHubPage(JsonElement[] Items, int Page, bool HasMore, int? Total = null);
public sealed record GitHubDetail(JsonElement Item, JsonElement[] Comments, JsonElement[] Files, JsonElement[] Reviews, bool Truncated);

public sealed partial class GitHubClient(HttpClient http, Func<CancellationToken, Task<string?>> token)
{
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();
    public static string Repository(string? value)
    {
        value = value?.Trim();
        if (value == null || value.Length > 201 || !RepositoryPattern().IsMatch(value) || value.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException("仓库格式应为 owner/repository。");
        return value;
    }

    public async Task<JsonElement> RequestAsync(string path, CancellationToken ct, HttpMethod? method = null, string? accessToken = null)
    {
        // Paths are assembled by this client, never from GitHub's response URLs.
        if (!path.StartsWith('/') || path.StartsWith("//") || path.Contains('\\') || path.Contains('#')) throw new ArgumentException("无效的 GitHub API 路径。");
        var credential = accessToken ?? await token(ct);
        if (string.IsNullOrWhiteSpace(credential)) throw new InvalidOperationException("请先登录 GitHub。");
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, "https://api.github.com" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.UserAgent.ParseAdd("AllAgent.NET-GitHub/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var reason = (int)response.StatusCode switch
            {
                401 => "登录已失效，请重新登录。",
                403 or 429 => "权限不足或请求额度已用尽；检查 Token 权限、组织 SSO 和 GitHub 限流。",
                404 => "内容不存在，或当前账号无权访问该仓库。",
                422 => "查询条件无效，请检查仓库、编号或搜索词。",
                _ => "请求失败，请稍后重试。"
            };
            throw new InvalidOperationException($"GitHub HTTP {(int)response.StatusCode}：{reason}");
        }
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent || response.StatusCode == System.Net.HttpStatusCode.ResetContent)
            return JsonSerializer.SerializeToElement(new { ok = true });
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > 8_000_000) throw new InvalidDataException("GitHub 响应过大，请缩小查询范围。");
            output.Write(buffer, 0, count);
        }
        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.Clone();
    }

    public async Task<GitHubPage> ListAsync(string kind, string? repository, string? query, int page, CancellationToken ct)
    {
        page = Math.Clamp(page, 1, kind is "issues" or "pulls" ? 34 : 1000);
        string path;
        if (kind == "repositories") path = $"/user/repos?sort=updated&per_page=30&page={page}";
        else if (kind == "notifications") path = $"/notifications?all=true&per_page=30&page={page}";
        else if (kind is "issues" or "pulls")
        {
            var scope = string.IsNullOrWhiteSpace(repository) ? "involves:@me" : "repo:" + Repository(repository);
            var search = $"is:{(kind == "pulls" ? "pr" : "issue")} {scope} {(string.IsNullOrWhiteSpace(query) ? "is:open" : query.Trim())}";
            if (search.Length > 1000) throw new ArgumentException("搜索词过长。");
            path = $"/search/issues?q={Uri.EscapeDataString(search)}&sort=updated&order=desc&per_page=30&page={page}";
        }
        else throw new ArgumentException("列表类型必须为 issues、pulls、notifications 或 repositories。");
        var result = await RequestAsync(path, ct);
        var items = (result.ValueKind == JsonValueKind.Array ? result : result.GetProperty("items")).EnumerateArray().ToArray();
        int? total = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("total_count", out var totalValue) ? totalValue.GetInt32() : null;
        return new(items, page, items.Length == 30 && (total == null || page * 30 < Math.Min(total.Value, 1000)), total);
    }

    public async Task<GitHubDetail> DetailAsync(string repository, int number, string kind, CancellationToken ct)
    {
        repository = Repository(repository);
        if (number < 1) throw new ArgumentException("Issue / PR 编号必须大于零。");
        if (kind is not ("issue" or "pull")) throw new ArgumentException("详情类型必须为 issue 或 pull。");
        var root = $"/repos/{repository}";
        var item = await RequestAsync($"{root}/{(kind == "pull" ? "pulls" : "issues")}/{number}", ct);
        var (comments, commentsMore) = await CollectAsync($"{root}/issues/{number}/comments", 3, ct);
        var files = Array.Empty<JsonElement>(); var reviews = Array.Empty<JsonElement>(); var more = commentsMore;
        if (kind == "pull")
        {
            (files, var fileMore) = await CollectAsync($"{root}/pulls/{number}/files", 10, ct);
            (reviews, var reviewsMore) = await CollectAsync($"{root}/pulls/{number}/comments", 3, ct);
            var refreshed = await RequestAsync($"{root}/pulls/{number}", ct);
            if (Text(item.GetProperty("head"), "sha") != Text(refreshed.GetProperty("head"), "sha") || Text(item.GetProperty("base"), "sha") != Text(refreshed.GetProperty("base"), "sha"))
                throw new InvalidOperationException("PR 在读取期间更新了，请刷新后重试。");
            more |= fileMore || reviewsMore || files.Length < item.GetProperty("changed_files").GetInt32();
        }
        return new(item, comments, files, reviews, more);
    }

    private async Task<(JsonElement[] Items, bool More)> CollectAsync(string path, int pages, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var characters = 0;
        for (var page = 1; page <= pages; page++)
        {
            var part = (await RequestAsync($"{path}?per_page=100&page={page}", ct)).EnumerateArray().ToArray();
            foreach (var item in part)
            {
                characters += item.GetRawText().Length;
                if (characters > 1_000_000) return (items.ToArray(), true);
                items.Add(item);
            }
            if (part.Length < 100) return (items.ToArray(), false);
        }
        return (items.ToArray(), true);
    }

    public Task<JsonElement> MarkReadAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 40 || !id.All(char.IsAsciiDigit)) throw new ArgumentException("通知 ID 无效。");
        return RequestAsync("/notifications/threads/" + id, ct, HttpMethod.Patch);
    }
    public static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
}
