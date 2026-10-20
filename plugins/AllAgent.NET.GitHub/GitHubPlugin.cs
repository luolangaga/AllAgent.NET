using System.Collections.Concurrent;
using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Extensions;

namespace AllAgent.NET.GitHub;

public sealed record GitHubQuery(string Kind = "issues", string? Repository = null, string? Query = null, int Page = 1);
public sealed record GitHubItemQuery(string Repository, int Number, string Kind = "issue");
public sealed record GitHubSecret(string Token);
public sealed record GitHubRead(string Id);
public sealed record GitHubOperationQuery(string Id);
public sealed record GitHubOperation(string Id, string State, PluginTaskResult? Result = null, string? Error = null);

public sealed class GitHubPlugin : IAllAgentPlugin
{
    public string Id => "allagent.github";
    public string Name => "GitHub";
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WorkspaceGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GitHubOperation> operations = new();
    private readonly ConcurrentDictionary<string, Task> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private int loginPending;
    private string? loginError;

    public void Register(PluginContext context)
    {
        var auth = new GitHubAuthentication(context.DataDirectory, http);
        var api = new GitHubClient(http, auth.GetAccessTokenAsync);
        context.AddWorkspaceIndependentTools(new GitHubTools(api));
        context.AddAuthentication(auth);
        context.AddWebBridge<object?, AuthenticationStatus>("status", (_, ct) => auth.GetStatusAsync(ct));
        context.AddWebBridge<GitHubSecret, AuthenticationStatus>("login", async (p, ct) =>
        {
            if (Interlocked.CompareExchange(ref loginPending, 1, 0) != 0) throw new InvalidOperationException("登录正在进行，请先完成授权。");
            try { await auth.LoginWithInputAsync(p.Token, ct); return await auth.GetStatusAsync(ct); }
            finally { Volatile.Write(ref loginPending, 0); }
        });
        context.AddWebBridge<object?, bool>("logout", async (_, ct) =>
        {
            if (Volatile.Read(ref loginPending) != 0) throw new InvalidOperationException("登录正在进行，请先完成授权。");
            await auth.LogoutAsync(ct); return true;
        });
        context.AddWebBridge<object?, bool>("browserLogin", (_, _) =>
        {
            if (Interlocked.CompareExchange(ref loginPending, 1, 0) != 0) throw new InvalidOperationException("登录正在进行。");
            loginError = null;
            pending["login"] = Task.Run(async () =>
            {
                try
                {
                    await auth.LoginAsync((uri, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (uri.Scheme != "https" || uri.Host != "github.com") throw new InvalidOperationException("无效的 GitHub 授权地址。");
                        using var browser = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                        return Task.CompletedTask;
                    }, lifetime.Token);
                    context.Web.Push("auth", new { loggedIn = true });
                }
                catch (Exception ex)
                {
                    loginError = ex is OperationCanceledException ? "授权已取消或超时。" : ex.Message;
                    context.Web.Push("auth", new { loggedIn = false, error = loginError });
                }
                finally { Volatile.Write(ref loginPending, 0); }
            });
            return Task.FromResult(true);
        });
        context.AddWebBridge<object?, object>("loginProgress", (_, _) => Task.FromResult<object>(new { pending = Volatile.Read(ref loginPending) != 0, instructions = auth.LoginInstructions, userCode = auth.DeviceUserCode, verificationUri = auth.VerificationUri, error = loginError }));
        context.AddWebBridge<GitHubQuery, GitHubPage>("list", (p, ct) => api.ListAsync(p.Kind, p.Repository, p.Query, p.Page, ct));
        context.AddWebBridge<GitHubItemQuery, GitHubDetail>("detail", (p, ct) => api.DetailAsync(p.Repository, p.Number, p.Kind, ct));
        context.AddWebBridge<GitHubRead, JsonElement>("read", (p, ct) => api.MarkReadAsync(p.Id, ct));
        context.AddWebBridge<object?, IReadOnlyList<PluginWorkspace>>("workspaces", (_, _) => Task.FromResult(context.GetService<PluginWorkspaceTasks>().List()));
        // Cloning may exceed the bridge's request timeout. Return a handle immediately and poll it;
        // background operation lifetime belongs to the plugin, never to a transient iframe.
        context.AddWebBridge<GitHubTask, GitHubOperation>("start", (p, _) =>
        {
            var id = Guid.NewGuid().ToString("N");
            var operation = new GitHubOperation(id, "running");
            operations[id] = operation;
            pending[id] = Task.Run(async () =>
            {
                try { operations[id] = new(id, "done", await StartAsync(context, api, auth, p, lifetime.Token)); }
                catch (Exception ex) { operations[id] = new(id, "error", Error: ex is OperationCanceledException ? "操作已取消。克隆产生的文件会保留。" : ex.Message); }
            });
            return Task.FromResult(operation);
        });
        context.AddWebBridge<GitHubOperationQuery, GitHubOperation>("operation", (p, _) => Task.FromResult(operations.GetValueOrDefault(p.Id) ?? throw new ArgumentException("操作不存在。")));
        context.AddWebBridge<object?, GitHubOperation[]>("operations", (_, _) => Task.FromResult(operations.Values.ToArray()));
        context.AddCommand(new("github", "GitHub：repos / issues / prs / notifications / show / solve / review；/ext github help 查看用法", async (args, ct) =>
        {
            var action = args.FirstOrDefault() ?? "help";
            if (action is "help" or "--help") return Help;
            if (action == "status") return JsonSerializer.Serialize(await auth.GetStatusAsync(ct), JsonOptions.Default);
            if (action == "logout") { await auth.LogoutAsync(ct); return "已退出 GitHub。"; }
            if (action == "workspaces") return JsonSerializer.Serialize(context.GetService<PluginWorkspaceTasks>().List(), JsonOptions.Default);
            if (action == "read") return JsonSerializer.Serialize(await api.MarkReadAsync(Required(args, 1), ct), JsonOptions.Default);
            if (action == "show") return JsonSerializer.Serialize(await api.DetailAsync(Required(args, 1), Number(args, 2), args.ElementAtOrDefault(3) == "pr" ? "pull" : "issue", ct), JsonOptions.Default);
            if (action is "solve" or "review")
            {
                var cloneAt = Array.IndexOf(args, "--clone");
                var workspace = cloneAt >= 0 ? Required(args, cloneAt + 1) : Required(args, 3);
                var extraAt = Array.IndexOf(args, "--instructions");
                var result = await StartAsync(context, api, auth, new(Required(args, 1), Number(args, 2), action, workspace, cloneAt >= 0, extraAt >= 0 ? string.Join(' ', args.Skip(extraAt + 1)) : null), ct);
                return $"已启动 GitHub {action} 流程。\n工作区：{result.Workspace}\n会话：{result.ConversationId}\n使用 /workspace 切换到该目录，再用 /session 打开会话；/web-ui 可查看实时进度。";
            }
            if (action is not ("repos" or "issues" or "prs" or "notifications")) throw new ArgumentException(Help);
            var pageAt = Array.IndexOf(args, "--page");
            var page = pageAt >= 0 ? Number(args, pageAt + 1) : 1;
            var queryAt = Array.IndexOf(args, "--query");
            var query = queryAt >= 0 ? Required(args, queryAt + 1) : null;
            var repo = args.ElementAtOrDefault(1) is { } candidate && !candidate.StartsWith("--") ? candidate : null;
            var data = await api.ListAsync(action == "repos" ? "repositories" : action == "prs" ? "pulls" : action, repo, query, page, ct);
            return JsonSerializer.Serialize(data, JsonOptions.Default);
        }));
    }

    public static async Task<PluginTaskResult> StartAsync(PluginContext context, GitHubClient api, GitHubAuthentication auth, GitHubTask request, CancellationToken ct)
    {
        if (request.Kind is not ("solve" or "review")) throw new ArgumentException("任务类型必须为 solve 或 review。");
        if (request.Instructions?.Length > 8000) throw new ArgumentException("补充要求最长 8000 字符。");
        if (string.IsNullOrWhiteSpace(request.Workspace) || !Path.IsPathFullyQualified(request.Workspace)) throw new ArgumentException("请选择工作区或输入绝对路径。");
        var repository = GitHubClient.Repository(request.Repository);
        var workspace = Path.GetFullPath(request.Workspace);
        var host = context.GetService<PluginWorkspaceTasks>();
        var gate = WorkspaceGates.GetOrAdd(workspace, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var detail = await api.DetailAsync(repository, request.Number, request.Kind == "review" ? "pull" : "issue", ct);
            if (request.Kind == "solve" && detail.Item.TryGetProperty("pull_request", out _)) throw new ArgumentException("此编号是 PR，请使用审查 PR 流程。");
            if (request.CloneRepository)
            {
                var token = await auth.GetAccessTokenAsync(ct) ?? throw new InvalidOperationException("请先登录 GitHub。");
                await GitHubGit.CloneAsync(workspace, repository, token, request.Kind == "review" ? new(request.Number, GitHubClient.Text(detail.Item.GetProperty("head"), "sha")) : null, ct);
            }
            else await GitHubGit.ValidateAsync(workspace, repository, ct);
            var prompt = GitHubWorkflows.Prompt(request with { Workspace = workspace, Repository = repository }, detail);
            var title = $"GitHub {(request.Kind == "review" ? "审查 PR" : "解决 Issue")} · {repository} #{request.Number}";
            var message = title + $"\nhttps://github.com/{repository}/{(request.Kind == "review" ? "pull" : "issues")}/{request.Number}";
            if (!string.IsNullOrWhiteSpace(request.Instructions)) message += "\n\n补充要求：\n" + request.Instructions;
            return await host.StartAsync(new(workspace, title, prompt, Message: message), ct);
        }
        finally { gate.Release(); }
    }
    private static string Required(string[] args, int index) => args.ElementAtOrDefault(index) ?? throw new ArgumentException(Help);
    private static int Number(string[] args, int index) => int.TryParse(Required(args, index), out var value) && value > 0 ? value : throw new ArgumentException("请输入大于零的编号或页码。");
    private const string Help = """
        /login github                              安全粘贴 Token 登录（不要把 Token 写在命令中）
        /ext github status | logout | workspaces
        /ext github repos [--page 2]
        /ext github issues [owner/repo] [--query "is:open assignee:@me"] [--page 2]
        /ext github prs [owner/repo] [--query "is:open review-requested:@me"]
        /ext github notifications [--page 2]
        /ext github read 通知ID
        /ext github show owner/repo 编号 [issue|pr]
        /ext github solve owner/repo 编号 "C:\已有工作区" [--instructions 补充要求]
        /ext github review owner/repo 编号 --clone "C:\空目录" [--instructions 补充要求]
        solve 和 review 均支持已有工作区或 --clone 空目录。流程只在本地运行，不自动发布到 GitHub。
        """;
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await Task.WhenAll(pending.Values);
        lifetime.Dispose(); http.Dispose();
    }
}
