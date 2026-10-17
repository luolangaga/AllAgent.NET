using System.Runtime.CompilerServices;
using System.Text.Json;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.Core.Extensions;

public sealed class AuthenticatedAiProvider(IAiProvider provider, IAuthenticationProvider authentication) : IAiProvider
{
    public string ProviderId => provider.ProviderId;
    private async Task<ApiConfig> ResolveAsync(ApiConfig config, CancellationToken ct)
    {
        authentication.ValidateModelConfiguration(config);
        var copy = JsonSerializer.Deserialize<ApiConfig>(JsonSerializer.Serialize(config))!;
        var credential = await authentication.GetCredentialAsync(ct) ?? throw new InvalidOperationException("认证已过期；请使用 /login " + authentication.Id);
        if (credential.InferenceBaseUrl != null) copy.BaseUrl = credential.InferenceBaseUrl;
        if (credential.AccountId != null) copy.ExtraHeadersJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["ChatGPT-Account-Id"] = credential.AccountId });
        copy.ApiKey = credential.AccessToken;
        copy.AuthenticationContextId = credential.ContextId;
        copy.ApiKeyEnvironmentVariable = null; return copy;
    }
    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    { await foreach (var chunk in provider.StreamChatAsync(request with { Config = await ResolveAsync(request.Config, ct) }, ct)) yield return chunk; }
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(ApiConfig config, CancellationToken ct = default) => await provider.ListModelsAsync(await ResolveAsync(config, ct), ct);
}

public sealed class AuthenticationHttpHandler(IAuthenticationProvider authentication, Uri endpoint) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("请求缺少地址。");
        if (!uri.Scheme.Equals(endpoint.Scheme, StringComparison.OrdinalIgnoreCase) || !uri.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != endpoint.Port) throw new InvalidOperationException("拒绝向其他来源发送 OAuth 凭据。");
        var token = await authentication.GetAccessTokenAsync(ct) ?? throw new InvalidOperationException("认证过期；使用 /login " + authentication.Id);
        request.Headers.Authorization = new("Bearer", token); return await base.SendAsync(request, ct);
    }
}
