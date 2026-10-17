using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Terminal;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ModelSelectionTests
{
    [Fact]
    public async Task PickerFiltersCredentialsAndRechecksLogoutWithoutChangingStoredConfigs()
    {
        using var workspace = new TestWorkspace();
        using var store = new DataStore(Path.Combine(workspace.Root, "data"));
        var account = new Account();
        var envName = "ALLAGENT_SELECTION_" + Guid.NewGuid().ToString("N");
        ApiConfig Config(string name, string? auth = null, string key = "", string url = "https://fixture.example/v1") =>
            new() { DisplayName = name, DefaultModel = "model", AuthenticationProviderId = auth, ApiKey = key, BaseUrl = url };
        store.AddApiConfig(Config("signed-in", "account"));
        store.AddApiConfig(Config("same-account", "account"));
        store.AddApiConfig(Config("signed-out", "logged-out"));
        store.AddApiConfig(Config("missing-plugin", "missing"));
        store.AddApiConfig(Config("api-key", key: "fixture"));
        store.AddApiConfig(Config("local", url: "http://127.0.0.1:11434/v1"));
        store.AddApiConfig(Config("no-key"));
        var envConfig = Config("environment"); envConfig.ApiKeyEnvironmentVariable = envName; store.AddApiConfig(envConfig);
        var disabled = Config("disabled", key: "fixture"); disabled.IsEnabled = false; store.AddApiConfig(disabled);
        var manager = new ModelManager(store, new TerminalUi(true, false), TerminalArguments.Parse([]),
            authentication: id => id == "account" ? account : id == "logged-out" ? new Account { LoggedIn = false } : throw new InvalidOperationException());
        Assert.Equal(new[] { "signed-in", "same-account", "api-key", "local" }, (await manager.AvailableConfigsAsync()).Select(c => c.DisplayName));
        Assert.Equal(1, account.Reads);
        try
        {
            Environment.SetEnvironmentVariable(envName, "fixture"); account.LoggedIn = false;
            Assert.Equal(new[] { "api-key", "local", "environment" }, (await manager.AvailableConfigsAsync()).Select(c => c.DisplayName));
            Assert.Equal(9, store.ApiConfigs.Count);
            account.LoggedIn = true; account.ModelConnectionAvailable = false;
            Assert.DoesNotContain(await manager.AvailableConfigsAsync(), c => c.AuthenticationProviderId == "account");
        }
        finally { Environment.SetEnvironmentVariable(envName, null); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupHeadingsAreNotSelectableAndSearchFindsModels(bool search)
    {
        var console = new TestConsole(); console.Profile.Width = 100;
        console.Profile.Capabilities.Interactive = true;
        var choices = new[] { (Group: "DeepSeek", Model: "deepseek-flash"), (Group: "Codex", Model: "gpt-model") };
        var prompt = TerminalSelectionPrompt.Create("选择模型", choices, c => c.Model, 14,
            new Style(Color.Cyan1), c => c.Group, c => c.Group == "DeepSeek" ? "#7dcfff" : "#bb9af7");
        if (search) console.Input.PushText("gpt-model"); else console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        var selected = console.Prompt(prompt);
        Assert.Null(selected.Heading); Assert.Equal("gpt-model", selected.Value.Model);
        Assert.Contains("DeepSeek", console.Output); Assert.Contains("Codex", console.Output); Assert.Contains("●", console.Output);
    }

    [Fact]
    public void ConfigurationsFromSameProviderShareHeadingAndColor()
    {
        var first = new ApiConfig { ProviderId = "deepseek", DisplayName = "deepseek" };
        var second = new ApiConfig { ProviderId = "deepseek", DisplayName = "deepseektest" };
        Assert.Equal("DeepSeek", ModelManager.ProviderGroup(first));
        Assert.Equal(ModelManager.ProviderGroup(first), ModelManager.ProviderGroup(second));
        Assert.Equal(ModelManager.ProviderColor(first), ModelManager.ProviderColor(second));
        Assert.NotEqual(ModelManager.ProviderColor(first), ModelManager.ProviderColor(new ApiConfig { ProviderId = "codex" }));
    }

    private sealed class Account : IAuthenticationProvider
    {
        public string Id => "account";
        public string Name => "Fixture";
        public bool LoggedIn = true;
        public bool ModelConnectionAvailable = true;
        public int Reads;
        public Task<AuthenticationStatus> GetStatusAsync(CancellationToken ct) { Reads++; return Task.FromResult(new AuthenticationStatus(LoggedIn, ModelConnectionAvailable)); }
        public Task<string?> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task LoginAsync(Func<Uri, CancellationToken, Task> openBrowser, CancellationToken ct) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
