using System.Text.Json;
using AllAgent.NET.Core.Bridge;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.Terminal;

/// <summary>
/// Account login for the browser workspace. Mirrors the terminal's /login list — built-in
/// Codex, oauth-profiles.json entries, plugin authentication and API-key presets — without
/// any UI coupling. OAuth flows open the system browser on the host; the outcome arrives
/// later as an `auth.changed` bridge event, with `auth.providers` as the polling fallback.
/// </summary>
internal sealed class WebAuthService(DataStore store, string dataDirectory, PluginHost plugins, BridgeEventPusher pusher, Func<bool> busy)
{
    private readonly object _sync = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<IAuthenticationProvider> _builtins = [new ChatGptAuthentication(dataDirectory)];
    private string ProfilesPath => Path.Combine(dataDirectory, "oauth-profiles.json");
    private IReadOnlyList<OAuthProfile> Profiles => File.Exists(ProfilesPath) ? JsonSerializer.Deserialize<OAuthProfile[]>(File.ReadAllText(ProfilesPath)) ?? [] : [];

    public IReadOnlyList<IAuthenticationProvider> Authentication => _builtins
        .Concat(Profiles.Where(p => !_builtins.Any(a => a.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase))).Select(p => (IAuthenticationProvider)new OAuthAuthentication(p, new TokenVault(Path.Combine(dataDirectory, "credentials")))))
        .Concat(plugins.Authentication.Where(a => !_builtins.Any(b => b.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase))))
        .ToArray();

    public IAuthenticationProvider Find(string id) => Authentication.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("登录提供商不存在；请刷新列表后重试。");

    /// <summary>Login providers plus the API-key presets from the terminal's /login list. Presets
    /// with a matching authentication provider are omitted so each entry has exactly one path.</summary>
    public async Task<List<AuthProviderInfo>> ListAsync(CancellationToken ct)
    {
        var items = new List<AuthProviderInfo>();
        foreach (var provider in Authentication) items.Add(await DescribeAsync(provider, ct));
        var covered = Authentication.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var presets = ProviderCatalog.All.Concat(ModelCatalog.Providers).DistinctBy(p => p.Id)
            .Where(p => p.Id != "custom" && !covered.Contains(p.Id)).ToArray();
        foreach (var preset in presets)
        {
            var configs = store.ApiConfigs.Where(c => c.ProviderId == preset.Id).ToArray();
            var configured = configs.FirstOrDefault(c => c.IsEnabled && (!string.IsNullOrWhiteSpace(c.ApiKey) || !string.IsNullOrWhiteSpace(c.ApiKeyEnvironmentVariable)));
            var message = configs.Length == 0 ? "尚未配置 · 粘贴 API Key 即可使用"
                : configured != null ? "密钥已就绪 · 可在「模型与 API」中管理"
                : "配置已存在，但还没有可用密钥";
            items.Add(new AuthProviderInfo(preset.Id, preset.Name, "apikey", configured != null, "", message, true, null, preset.Id, configured?.Id.ToString()));
        }
        return items;
    }

    public async Task<AuthProviderInfo> StatusAsync(string id, CancellationToken ct) => await DescribeAsync(Find(id), ct);

    private async Task<AuthProviderInfo> DescribeAsync(IAuthenticationProvider provider, CancellationToken ct)
    {
        var kind = provider is IInteractiveAuthentication ? "interactive" : "oauth";
        var prompt = provider is IInteractiveAuthentication interactive ? interactive.InputPrompt : null;
        try
        {
            var status = await provider.GetStatusAsync(ct);
            return new AuthProviderInfo(provider.Id, provider.Name, kind, status.LoggedIn, status.Account ?? "", status.Message ?? "", status.ModelConnectionAvailable, prompt, null, null, Pending(provider.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AuthProviderInfo(provider.Id, provider.Name, kind, false, "", "状态检查失败：" + ex.Message, false, prompt, null, null, false);
        }
    }

    /// <summary>Starts a browser OAuth flow without blocking the bridge call. The browser opens on
    /// the host; success or failure is pushed as `auth.changed` when the flow settles.</summary>
    public bool Login(string id, bool newAccount)
    {
        EnsureIdle();
        var provider = Find(id);
        lock (_sync) { if (!_pending.Add(id)) throw new InvalidOperationException("登录正在进行：请在浏览器完成授权。"); }
        _ = Task.Run(async () =>
        {
            string? error = null;
            try
            {
                Task OpenBrowser(Uri uri, CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    WebUiSession.OpenBrowser(uri.AbsoluteUri);
                    return Task.CompletedTask;
                }
                if (provider is ChatGptAuthentication chatGpt) await chatGpt.LoginAsync(OpenBrowser, newAccount, CancellationToken.None);
                else await provider.LoginAsync(OpenBrowser, CancellationToken.None);
                var status = await provider.GetStatusAsync(CancellationToken.None);
                error = status.LoggedIn ? await ConnectModelsAsync(provider, CancellationToken.None) : "浏览器授权未完成，登录未生效。";
            }
            catch (OperationCanceledException) { error = "登录已取消。"; }
            catch (Exception ex) { error = ex.Message; }
            finally { lock (_sync) _pending.Remove(id); }
            Push(id, error);
        });
        return true;
    }

    /// <summary>Paste-in providers (API keys): validate the secret, store it, then connect the model catalog.</summary>
    public async Task<AuthProviderInfo> LoginWithInputAsync(string id, string secret, CancellationToken ct)
    {
        EnsureIdle();
        var provider = Find(id);
        if (provider is not IInteractiveAuthentication interactive) throw new InvalidOperationException("该提供商不支持粘贴 API Key 登录。");
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("请输入 API Key。");
        await interactive.LoginWithInputAsync(secret.Trim(), ct);
        var error = await ConnectModelsAsync(provider, ct);
        Push(id, error);
        return await DescribeAsync(provider, ct);
    }

    public async Task<AuthProviderInfo> LogoutAsync(string id, CancellationToken ct)
    {
        EnsureIdle();
        var provider = Find(id);
        await provider.LogoutAsync(ct);
        foreach (var config in store.ApiConfigs.Where(c => c.AuthenticationProviderId == provider.Id))
        {
            var copy = ManagementUi.Clone(config);
            copy.IsEnabled = false;
            store.UpdateApiConfig(copy);
        }
        Push(id, null);
        return await DescribeAsync(provider, ct);
    }

    /// <summary>After a successful login, bind the account's model catalog to an ApiConfig the
    /// browser can select — the same follow-up the terminal performs on /login.</summary>
    private async Task<string?> ConnectModelsAsync(IAuthenticationProvider provider, CancellationToken ct)
    {
        if (provider is ChatGptAuthentication)
        {
            var previous = store.ApiConfigs.FirstOrDefault(c => c.ProviderId == "codex" && c.AuthenticationProviderId == provider.Id);
            var config = previous == null
                ? new ApiConfig { ProviderId = "codex", Protocol = "responses", BaseUrl = "https://api.openai.com/v1", DisplayName = "Codex / ChatGPT", AuthenticationProviderId = "codex", ReasoningProtocol = "openai" }
                : ManagementUi.Clone(previous);
            try
            {
                var models = await new AuthenticatedAiProvider(new ResponsesProvider(), provider).ListModelsAsync(config, ct);
                if (models.Count == 0) throw new InvalidOperationException("该账号暂未返回可用模型。");
                config.Models = models.Select(m => m.Id).ToList();
                ModelCatalog.Remember(config, models);
                config.MaxTokens = 0;
                if (!config.Models.Contains(config.DefaultModel)) config.DefaultModel = config.Models[0];
                config.IsEnabled = true;
                if (previous == null) store.AddApiConfig(config); else store.UpdateApiConfig(config);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An old account's catalog must not remain selectable after the refresh fails.
                if (previous != null) { config.Models = []; config.DefaultModel = ""; config.IsEnabled = false; store.UpdateApiConfig(config); }
                return "账号已登录，但模型目录尚未连接：" + ex.Message + "。可重新登录重试。";
            }
        }
        if (provider is not IAccountModelProvider accountModels) return null;
        var accountConfig = accountModels.CreateConfiguration();
        var existing = store.ApiConfigs.FirstOrDefault(c => c.AuthenticationProviderId == provider.Id);
        try
        {
            var models = await accountModels.ModelProvider.ListModelsAsync(accountConfig, ct);
            if (models.Count == 0) throw new InvalidOperationException("账号没有返回可用模型。");
            accountConfig.Models = models.Select(m => m.Id).ToList();
            ModelCatalog.Remember(accountConfig, models);
            accountConfig.MaxTokens = 0;
            accountConfig.DefaultModel = existing != null && accountConfig.Models.Contains(existing.DefaultModel)
                ? existing.DefaultModel : accountConfig.Models[0];
            if (existing != null) { accountConfig.Id = existing.Id; store.UpdateApiConfig(accountConfig); } else store.AddApiConfig(accountConfig);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (existing != null) { var disabled = ManagementUi.Clone(existing); disabled.IsEnabled = false; store.UpdateApiConfig(disabled); }
            return "账号已登录，模型目录连接失败：" + ex.Message + "。请重新登录重试。";
        }
    }

    private void EnsureIdle() { if (busy()) throw new InvalidOperationException("请先完成或取消正在运行的任务，再更改账号登录。"); }

    private bool Pending(string id) { lock (_sync) return _pending.Contains(id); }

    private void Push(string id, string? error) => pusher.Push(JsonSerializer.Serialize(new { type = "auth.changed", id, error }, JsonOptions.Default));
}
