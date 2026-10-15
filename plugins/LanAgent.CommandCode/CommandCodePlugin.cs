
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LanAgent.Core.Extensions;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;

namespace LanAgent.CommandCode;

public sealed class CommandCodePlugin : ILanAgentPlugin
{
    private CommandCodeAccount? account;
    public string Id => "lanagent.commandcode";
    public string Name => "CommandCode";
    public void Register(PluginContext context)
    {
        account = new(context.DataDirectory);
        context.AddAuthentication(account);
        context.AddProvider(account.ModelProvider);
        context.AddCommand(new("commandcode-status", "查看 CommandCode 登录状态", async (_, ct) =>
            (await account.GetStatusAsync(ct)).Message!));
    }
    public ValueTask DisposeAsync() { account?.Dispose(); return ValueTask.CompletedTask; }
}

public sealed class CommandCodeAccount : IAuthenticationProvider, IAccountModelProvider, IDisposable
{
    public const string Endpoint = "https://api.commandcode.ai";
    private readonly TokenVault vault;


    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };



    public string Id => "commandcode";
    public string Name => "CommandCode";
    public string LoginInstructions => "先设置 COMMANDCODE_API_KEY=user_...，再 /login commandcode；密钥通过 TokenVault 加密保存。无需额外运行时。";
    public IAiProvider ModelProvider { get; }
    public CommandCodeAccount(string directory, HttpClient? client = null)
    {

        vault = new(Path.Combine(directory, "credentials"));
        if (client != null) http = client;
        ModelProvider = new CommandCodeProvider(this, http);
    }
    public ApiConfig CreateConfiguration() => new() { ProviderId = Id, Protocol = Id, AuthenticationProviderId = Id, DisplayName = Name, BaseUrl = Endpoint, ReasoningProtocol = "openai" };
    public void ValidateModelConfiguration(ApiConfig config)
    {
        if (config.Protocol != Id || config.BaseUrl.TrimEnd('/') != Endpoint)
            throw new InvalidOperationException("CommandCode 凭据只能绑定 CommandCode 插件协议及官方地址。");
    }
    public async Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("COMMANDCODE_API_KEY")?.Trim();
        if (token == null || !token.StartsWith("user_", StringComparison.Ordinal) || token.Length <= 5 || token.Any(char.IsWhiteSpace))
            throw new InvalidOperationException(LoginInstructions);
        // Verify before replacing an existing credential; never save a rejected key.
        await FetchModelsAsync(token, ct);
        await vault.SaveAsync(Id, new(token, null, DateTimeOffset.MaxValue), ct);
    }
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct) => (await vault.ReadAsync(Id, ct))?.AccessToken;
    public async Task LogoutAsync(CancellationToken ct) => await vault.DeleteAsync(Id, ct);
    public async Task<AuthenticationStatus> GetStatusAsync(CancellationToken ct)
    {
        var loggedIn = await GetAccessTokenAsync(ct) != null;
        return new(loggedIn, loggedIn, Message: loggedIn ? "CommandCode 已登录；/logout commandcode 可清除凭据。" : LoginInstructions);
    }
    internal async Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + "/provider/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("x-cli-environment", "production");
        request.Headers.Add("x-command-code-version", "1.53.1");
        request.Headers.Add("User-Agent", "cli");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("CommandCode 模型查询失败：HTTP " + (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Select(id => new ModelInfo(id!, id!)).ToArray();
    }
    public void Dispose() => http.Dispose();
}
