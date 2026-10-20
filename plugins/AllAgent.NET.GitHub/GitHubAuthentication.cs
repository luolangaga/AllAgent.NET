using AllAgent.NET.Core.Extensions;

namespace AllAgent.NET.GitHub;

public sealed class GitHubAuthentication : IAuthenticationProvider, IInteractiveAuthentication
{
    private readonly TokenVault vault;
    private readonly GitHubClient api;
    private readonly OAuthAuthentication oauth;
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Id => "github";
    public string Name => "GitHub";
    public string InputPrompt => "GitHub Personal Access Token（通知需要 classic Token 的 notifications 权限，私有仓库需要 repo）";
    public const string DefaultClientId = "Ov23li0r1CgsUOlzM48x";
    public string? LoginInstructions => oauth.LoginInstructions ?? "粘贴 GitHub Token 登录，或点击“浏览器授权”使用设备码登录。";
    public string? DeviceUserCode => oauth.DeviceUserCode;
    public string? VerificationUri => oauth.VerificationUri;

    public GitHubAuthentication(string directory, HttpClient http)
    {
        vault = new TokenVault(Path.Combine(directory, "credentials"));
        api = new GitHubClient(http, GetAccessTokenAsync);
        oauth = new OAuthAuthentication(new("github", "GitHub", DefaultClientId, "https://github.com/login/oauth/access_token",
                DeviceEndpoint: "https://github.com/login/device/code", Scope: "repo notifications read:user", NonExpiringAccessToken: true), vault, http,
                async (token, ct) => { await api.RequestAsync("/user", ct, accessToken: token); },
                uri => uri.Host == "github.com");
    }
    public Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct) => oauth.LoginAsync(openBrowser, ct);
    public async Task LoginWithInputAsync(string input, CancellationToken ct)
    {
        var token = input.Trim();
        if (token.Length is < 10 or > 4096 || token.Any(char.IsWhiteSpace)) throw new ArgumentException("Token 格式无效。");
        await api.RequestAsync("/user", ct, accessToken: token);
        await gate.WaitAsync(ct);
        try { await vault.SaveAsync(Id, new(token, null, DateTimeOffset.MaxValue), ct); }
        finally { gate.Release(); }
    }
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct) =>
        await oauth.GetAccessTokenAsync(ct) ?? (await vault.ReadAsync(Id, ct))?.AccessToken;
    public async Task<AuthenticationStatus> GetStatusAsync(CancellationToken ct)
    {
        if (await GetAccessTokenAsync(ct) == null) return new(false, false, Message: LoginInstructions);
        var user = await api.RequestAsync("/user", ct);
        return new(true, false, GitHubClient.Text(user, "login"), "已连接 GitHub · 此账号用于仓库协作");
    }
    public async Task LogoutAsync(CancellationToken ct)
    {
        await oauth.LogoutAsync(ct);
        await gate.WaitAsync(ct);
        try { await vault.DeleteAsync(Id, ct); } finally { gate.Release(); }
    }
}
