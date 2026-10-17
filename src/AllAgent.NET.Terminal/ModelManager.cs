using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal sealed class ModelManager(DataStore store, TerminalUi ui, TerminalArguments cli, Func<IReadOnlyList<IAiProvider>>? pluginProviders = null, Func<string, AllAgent.NET.Core.Extensions.IAuthenticationProvider>? authentication = null, Func<IReadOnlyList<AllAgent.NET.Core.Extensions.IAuthenticationProvider>>? authenticationChoices = null, Func<string, CancellationToken, Task>? login = null)
{
    private IAiProvider Provider(ApiConfig config)
    {
        var provider = new AiProviderFactory(pluginProviders?.Invoke()).GetProvider(config);
        return config.AuthenticationProviderId is { Length: > 0 } id ? new AllAgent.NET.Core.Extensions.AuthenticatedAiProvider(provider, authentication?.Invoke(id) ?? throw new ArgumentException("认证宿主不可用。")) : provider;
    }
    public ApiConfig Find(string selector) => store.ApiConfigs.SingleOrDefault(c => c.Id.ToString().StartsWith(selector, StringComparison.OrdinalIgnoreCase) || c.DisplayName.Equals(selector, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("提供商名称或 ID 未唯一匹配。");
    public void List()
    {
        var rows = store.ApiConfigs.Select(Safe).ToArray();
        ManagementUi.Output(ui, rows, () =>
        {
            var table = new Table().Title("[bold]模型提供商[/]").AddColumn("ID / 名称").AddColumn("协议 / 地址").AddColumn("模型").AddColumn("思考 / 状态");
            foreach (var c in store.ApiConfigs) table.AddRow(Markup.Escape(c.Id.ToString()[..8] + " · " + c.DisplayName), Markup.Escape(ProviderCatalog.ProtocolFor(c) + "\n" + c.BaseUrl), Markup.Escape(c.DefaultModel + $"\n列表 {c.Models.Count} 项"), Markup.Escape(c.ReasoningEffort + " · " + (c.IsEnabled ? "启用" : "禁用")));
            TerminalOutput.Write(table);
            TerminalOutput.MarkupLine("[dim]/login 添加 · /providers edit 修改 · refresh 自动获取 · models 手动列表 · /model 切换[/]");
        });
    }
    public static object Safe(ApiConfig c) => new { c.Id, c.DisplayName, c.ProviderId, protocol = ProviderCatalog.ProtocolFor(c), c.BaseUrl, c.DefaultModel, c.Models, c.ReasoningProtocol, c.ReasoningEffort, c.ThinkingBudget, c.Temperature, c.MaxTokens, contextWindowTokens = ModelContext.Capacity(c), c.ModelContextWindows, c.IsEnabled, c.AuthenticationProviderId, key = c.ApiKeyEnvironmentVariable is { Length: > 0 } ? "env:" + c.ApiKeyEnvironmentVariable : c.ApiKey.Length > 0 ? "已设置（隐藏）" : "未设置" };
    public async Task SetContextAsync(ApiConfig config, string? value = null)
    {
        if (value == null)
        {
            ManagementUi.Interactive(ui);
            value = ManagementUi.Choose("模型上下文容量 · " + config.DefaultModel + " · 当前 " + ModelContext.Format(ModelContext.Capacity(config)), "auto", "32k", "64k", "128k", "200k", "256k", "400k", "500k", "1M", "1050k", "自定义");
            if (value == "自定义") value = await ManagementUi.Text("上下文 Token 容量（如 131072 / 256k / 1.5M）", ModelContext.Capacity(config).ToString());
        }
        if (value == "auto") config.ModelContextWindows.Remove(config.DefaultModel);
        else config.ModelContextWindows[config.DefaultModel] = ModelContext.Parse(value);
        if (store.ApiConfigs.FirstOrDefault(saved => saved.Id == config.Id) is { } saved)
        {
            var copy = ManagementUi.Clone(saved);
            if (value == "auto") copy.ModelContextWindows.Remove(config.DefaultModel);
            else copy.ModelContextWindows[config.DefaultModel] = ModelContext.Capacity(config);
            store.UpdateApiConfig(copy);
        }
        ui.SetModelContext(config);
        ManagementUi.Success(ui, config.DefaultModel + " 上下文容量已设为 " + ModelContext.Format(ModelContext.Capacity(config)));
    }
    public void ShowModels()
    {
        var rows = store.ApiConfigs.SelectMany(c => (c.Models.Count > 0 ? c.Models : new List<string> { c.DefaultModel }).Select(id => new { provider = c.DisplayName, configId = c.Id, model = id, isDefault = id == c.DefaultModel, c.IsEnabled })).ToArray();
        ManagementUi.Output(ui, rows, () => { var table = new Table().AddColumn("提供商").AddColumn("模型 ID").AddColumn("状态"); foreach (var row in rows) table.AddRow(Markup.Escape(row.provider), Markup.Escape(row.model), (row.isDefault ? "默认 · " : "") + (row.IsEnabled ? "启用" : "禁用")); TerminalOutput.Write(table); });
    }
    public async Task ExecuteAsync(IReadOnlyList<string> words)
    {
        var action = words.FirstOrDefault() ?? (ui.Interactive && !ui.Json ? "panel" : "list");
        if (action == "panel")
        {
            List(); action = ManagementUi.Choose("提供商管理", "返回", "编辑", "自动获取模型", "手动模型列表", "启用", "禁用", "删除");
            if (action == "返回") return;
            action = action switch { "添加" => "add", "编辑" => "edit", "自动获取模型" => "refresh", "手动模型列表" => "models", "启用" => "enable", "禁用" => "disable", _ => "remove" };
            if (action != "add") { var chosen = PickConfig(); words = new[] { action, chosen.Id.ToString() }; }
        }
        if (action == "list") { List(); return; }
        if (action == "add") throw new ArgumentException("添加提供商请使用 /login。");
        var current = Find(ManagementUi.Require(words, 1, "提供商名称或 ID"));
        var copy = ManagementUi.Clone(current);
        switch (action)
        {
            case "auth": copy.AuthenticationProviderId = ManagementUi.Require(words, 2, "认证 ID（none 清除）") is var authId && authId != "none" ? authId : null; if (copy.AuthenticationProviderId != null) _ = authentication?.Invoke(copy.AuthenticationProviderId) ?? throw new ArgumentException("认证入口不存在。"); break;
            case "show": ManagementUi.Output(ui, Safe(copy), () => TerminalOutput.Write(new Panel(new Text(System.Text.Json.JsonSerializer.Serialize(Safe(copy), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }))).Header("脱敏配置"))); return;
            case "edit": copy = await WizardAsync(copy) ?? copy; break;
            case "refresh":
                var models = await ManagementUi.Busy("正在获取模型列表", ct => Provider(copy).ListModelsAsync(copy, ct));
                if (models.Count == 0) throw new InvalidOperationException("接口返回了空模型列表，已有列表未修改。");
                copy.Models = models.Select(m => m.Id).ToList();
                ModelCatalog.Remember(copy, models);
                ManagementUi.Output(ui, models, () => { foreach (var m in models) TerminalOutput.Write(new Text(m.Id + "\n")); }); break;
            case "models":
                var raw = cli.Get("models");
                if (raw == null) { ManagementUi.Interactive(ui); raw = await ManagementUi.Text("模型 ID 列表（逗号分隔）", string.Join(',', copy.Models)); }
                copy.Models = ParseModels(raw);
                if (!copy.Models.Contains(copy.DefaultModel)) copy.DefaultModel = copy.Models[0]; break;
            case "enable": copy.IsEnabled = true; break;
            case "disable": copy.IsEnabled = false; break;
            case "remove":
                if (!await ManagementUi.Confirm(ui, "删除提供商配置 " + copy.DisplayName + "？")) throw new OperationCanceledException("删除已取消。");
                store.DeleteApiConfig(copy.Id); ManagementUi.Success(ui, "提供商已删除。"); return;
            default: throw new ArgumentException("提供商操作：list / edit / refresh / models / show / enable / disable / remove；添加使用 /login。");
        }
        store.UpdateApiConfig(copy); ManagementUi.Success(ui, "提供商配置已保存。");
    }
    private static List<string> ParseModels(string raw)
    {
        var models = raw.Split(new[] { ',', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
        if (models.Count is 0 or > 2000 || models.Any(m => m.Length > 300 || m.Any(char.IsControl))) throw new ArgumentException("模型列表需要 1–2000 个有效 ID。");
        return models;
    }
    private void Validate(ApiConfig config)
    {
        _ = ModelContext.Capacity(config);
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new ArgumentException("Base URL 必须是无凭据、查询参数和片段的 HTTP(S) 地址。");
        if (!new[] { "openai", "responses", "anthropic", "gemini" }.Concat(pluginProviders?.Invoke().Select(p => p.ProviderId) ?? []).Contains(config.Protocol)) throw new ArgumentException("接口协议必须为内置协议或已启用的插件提供商 ID。");
        if (!ReasoningSettings.Protocols.Contains(config.ReasoningProtocol)) throw new ArgumentException("未知思考协议。");
        if (!config.Models.Contains(config.DefaultModel)) config.Models.Insert(0, config.DefaultModel);
    }
    public async Task AddCustomAsync(string providerId = "custom")
    {
        var config = await WizardAsync(null, ProviderCatalog.Find(providerId));
        if (config == null) return; // Interactive account login already stored and enabled the configuration.
        store.AddApiConfig(config);
        ManagementUi.Success(ui, "提供商已添加：" + config.DisplayName);
    }
    /// <summary>Account-backed providers sort ahead of plain API-key entries in every provider list.</summary>
    internal static bool IsAccountProvider(ApiConfig config) => !string.IsNullOrEmpty(config.AuthenticationProviderId);
    private async Task<ApiConfig?> WizardAsync(ApiConfig? original, ProviderPreset? presetOverride = null)
    {
        ManagementUi.Interactive(ui);
        await ModelCatalog.RefreshAsync();
        ApiConfig c;
        if (original == null)
        {
            ProviderPreset? preset = presetOverride;
            if (preset == null)
            {
                var accountProviders = authenticationChoices?.Invoke() ?? [];
                if (accountProviders.Count > 0)
                {
                    // One rewritten picker: OAuth / account providers first, API-key presets after, keyed by ID.
                    var accountLabels = accountProviders.Select(a => "◆ " + a.Name + " · 账号登录 · " + a.Id).ToArray();
                    var presets = ProviderCatalog.All.Where(p => !accountProviders.Any(a => a.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase))).ToArray();
                    const string customLabel = "◇ 自定义 · 手填地址与协议";
                    var choice = ManagementUi.Choose("选择提供商 · 账号登录优先，也可继续输入过滤", accountLabels.Concat(presets.Select(p => "◇ " + p.Name + " · API Key · " + p.Id)).Append(customLabel).ToArray());
                    if (choice == customLabel) preset = ProviderCatalog.Find("custom");
                    else
                    {
                        var account = accountProviders.FirstOrDefault(a => choice == "◆ " + a.Name + " · 账号登录 · " + a.Id);
                        if (account != null)
                        {
                            if (login == null) throw new InvalidOperationException("请使用 /login " + account.Id + " 登录账号提供商。");
                            await login(account.Id, default);
                            return null;
                        }
                        preset = presets.Single(p => choice == "◇ " + p.Name + " · API Key · " + p.Id);
                    }
                }
                else preset = ManagementUi.Choose("选择提供商（地址和协议均可修改）", ProviderCatalog.All, p => p.Name);
            }
            c = new() { ProviderId = preset.Id, DisplayName = preset.Name, Protocol = preset.Protocol, BaseUrl = preset.BaseUrl, ReasoningProtocol = "auto", AuthenticationProviderId = preset.Id == "codex" ? "codex" : null, MaxTokens = 16384 };
        }
        else c = original;
        if (c.ProviderId == "custom") c.DisplayName = await ManagementUi.Text("配置名称", c.DisplayName);
        if (c.ProviderId == "custom") c.Protocol = ManagementUi.Choose("接口协议 · 当前 " + ProviderCatalog.ProtocolFor(c), [ProviderCatalog.ProtocolFor(c), .. new[] { "openai", "responses", "anthropic", "gemini" }.Concat(pluginProviders?.Invoke().Select(p => p.ProviderId) ?? []).Where(p => p != ProviderCatalog.ProtocolFor(c))]);
        if (c.ProviderId == "custom") c.BaseUrl = (await ManagementUi.Text("Base URL", c.BaseUrl)).TrimEnd('/');
        var accounts = (authenticationChoices?.Invoke() ?? []).Where(a =>
        {
            try { a.ValidateModelConfiguration(c); return true; } catch (InvalidOperationException) { return false; }
        }).ToArray();
        const string apiKeyChoice = "API Key / 环境变量 / 本地服务";
        var selectedAccount = accounts.FirstOrDefault(a => a.Id == c.AuthenticationProviderId);
        var currentLabel = selectedAccount == null ? apiKeyChoice : selectedAccount.Name + " · " + selectedAccount.Id;
        var accountChoice = ManagementUi.Choose("认证方式 · 账号登录使用 /login", new[] { currentLabel, apiKeyChoice }.Concat(accounts.Select(a => a.Name + " · " + a.Id)).Distinct().ToArray());
        c.AuthenticationProviderId = accountChoice == apiKeyChoice ? null : accounts.Single(a => accountChoice == a.Name + " · " + a.Id).Id;
        if (c.AuthenticationProviderId == null)
        {
            var keyAction = ManagementUi.Choose("API Key（已有密钥不会显示）", "保留当前", "输入新 Key", "使用环境变量", "清空（本地服务）");
            if (keyAction == "输入新 Key") { c.ApiKey = await ManagementUi.Text("API Key", secret: true); c.ApiKeyEnvironmentVariable = null; }
            if (keyAction == "使用环境变量") { c.ApiKeyEnvironmentVariable = await ManagementUi.Text("密钥环境变量名称", c.ApiKeyEnvironmentVariable); c.ApiKey = ""; }
            if (keyAction == "清空（本地服务）") { c.ApiKey = ""; c.ApiKeyEnvironmentVariable = null; }
        }
        else { c.ApiKey = ""; c.ApiKeyEnvironmentVariable = null; }
        c.MaxTokens = 0;
        try
        {
            var models = await ManagementUi.Busy("正在获取模型列表", ct => Provider(c).ListModelsAsync(c, ct));
            c.Models = models.Select(m => m.Id).ToList();
            ModelCatalog.Remember(c, models);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ui.Error(ex.Message);
            var catalog = ModelCatalog.List(c);
            c.Models = catalog.Select(m => m.Id).ToList();
        }
        if (c.Models.Count == 0)
            c.Models = ParseModels(await ManagementUi.Text("模型 ID（接口未提供列表）", c.DefaultModel));
        ReasoningSettings.RememberForModel(c);
        c.DefaultModel = ManagementUi.Choose("选择模型（自动应用上下文与输出限制）", c.Models.ToArray());
        ReasoningSettings.RestoreForModel(c);
        Validate(c); return c;
    }
    private ApiConfig PickConfig()
    {
        if (store.ApiConfigs.Count == 0) throw new ArgumentException("暂无配置，请先添加。");
        var ordered = store.ApiConfigs.OrderByDescending(IsAccountProvider).ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        return ManagementUi.Choose("选择配置", ordered, c => c.DisplayName + (IsAccountProvider(c) ? " · 账号登录" : " · API Key"));
    }
    internal async Task<ApiConfig[]> AvailableConfigsAsync(CancellationToken ct = default)
    {
        var accounts = new Dictionary<string, bool>(StringComparer.Ordinal);
        var available = new List<ApiConfig>();
        foreach (var config in store.ApiConfigs.Where(config => config.IsEnabled))
        {
            if (config.AuthenticationProviderId is { Length: > 0 } id)
            {
                if (!accounts.TryGetValue(id, out var loggedIn))
                {
                    try
                    {
                        var account = authentication?.Invoke(id);
                        var status = account == null ? null : await account.GetStatusAsync(ct);
                        loggedIn = status is { LoggedIn: true, ModelConnectionAvailable: true };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { loggedIn = false; }
                    accounts[id] = loggedIn;
                }
                if (loggedIn) available.Add(config);
            }
            else if (!string.IsNullOrWhiteSpace(config.ApiKeyEnvironmentVariable)
                ? !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.ApiKeyEnvironmentVariable))
                : !string.IsNullOrWhiteSpace(config.ApiKey) || Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback)
                available.Add(config);
        }
        return available.ToArray();
    }
    internal static string ProviderGroup(ApiConfig config) => config.ProviderId is "" or "custom"
        ? config.DisplayName : ProviderCatalog.Find(config.ProviderId)?.Name ?? config.DisplayName;
    internal static string ProviderColor(ApiConfig config)
    {
        string[] palette = ["#7dcfff", "#bb9af7", "#9ece6a", "#e0af68", "#f7768e", "#73daca", "#ff9e64"];
        var key = ProviderGroup(config);
        uint hash = 0;
        foreach (var ch in key) hash = unchecked(hash * 31 + ch);
        return palette[hash % palette.Length];
    }
    public async Task<ApiConfig> SelectAsync(string? selector = null, string? model = null, bool forceSelection = false)
    {
        await ModelCatalog.RefreshAsync();
        ApiConfig c;
        if (selector != null) c = Find(selector);
        else if (!forceSelection && Environment.GetEnvironmentVariable("ALLAGENT_MODEL") is { Length: > 0 } envModel)
        {
            var id = Environment.GetEnvironmentVariable("ALLAGENT_PROVIDER") ?? "openai";
            var preset = ProviderCatalog.Find(id) ?? ProviderCatalog.Find("custom")!;
            c = new() { DisplayName = "环境变量", ProviderId = id, Protocol = Environment.GetEnvironmentVariable("ALLAGENT_PROTOCOL") ?? preset.Protocol, BaseUrl = Environment.GetEnvironmentVariable("ALLAGENT_BASE_URL") ?? preset.BaseUrl, DefaultModel = envModel, ApiKey = Environment.GetEnvironmentVariable("ALLAGENT_API_KEY") ?? "", ReasoningProtocol = Environment.GetEnvironmentVariable("ALLAGENT_REASONING_PROTOCOL") ?? preset.ReasoningProtocol };
            ReasoningSettings.SetEffort(c, Environment.GetEnvironmentVariable("ALLAGENT_EFFORT") ?? "default");
        }
        else
        {
            var available = await ManagementUi.Busy("检查提供商登录状态", AvailableConfigsAsync);
            if (available.Length == 0) throw new ArgumentException("暂无已登录或已配置密钥的提供商，请先使用 /login 登录或添加配置。");
            var remembered = available.FirstOrDefault(item => item.Id.ToString() == store.GetSetting("terminal:lastConfig"));
            if (forceSelection && ui.Interactive && !ui.Json)
            {
                foreach (var provider in available.Where(provider => provider.AuthenticationProviderId == "codex"))
                {
                    try
                    {
                        var discovered = await ManagementUi.Busy("刷新 Codex 模型目录", ct => Provider(provider).ListModelsAsync(provider, ct));
                        if (discovered.Count == 0) throw new InvalidOperationException("Codex 返回了空模型目录。");
                        var refreshed = ManagementUi.Clone(provider);
                        refreshed.Models = discovered.Select(item => item.Id).Distinct(StringComparer.Ordinal).ToList();
                        ModelCatalog.Remember(refreshed, discovered);
                        store.UpdateApiConfig(refreshed);
                        provider.Models = refreshed.Models;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ui.Error("刷新 Codex 模型失败，使用已保存列表：" + ex.Message);
                    }
                }
                var choices = available.SelectMany(provider => (provider.Models.Count > 0 ? provider.Models : [provider.DefaultModel])
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Select(id => (Provider: provider, Model: id))).ToArray();
                if (choices.Length == 0) throw new ArgumentException("尚未配置模型，请先添加。");
                var ordered = choices.GroupBy(choice => ProviderGroup(choice.Provider))
                    .OrderByDescending(group => IsAccountProvider(group.First().Provider))
                    .ThenByDescending(group => group.Any(choice => choice.Provider.Id == remembered?.Id))
                    .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(group => group.OrderByDescending(choice => choice.Provider.Id == remembered?.Id && choice.Model == remembered.DefaultModel)
                        .ThenBy(choice => choice.Model, StringComparer.OrdinalIgnoreCase)).ToArray();
                var choice = ManagementUi.Choose("选择模型 · 账号登录优先，可输入过滤", ordered, choice => choice.Model + " · " + choice.Provider.DisplayName,
                    choice => ProviderGroup(choice.Provider) + (IsAccountProvider(choice.Provider) ? " · 账号登录" : ""), choice => ProviderColor(choice.Provider));
                c = choice.Provider; model = choice.Model;
            }
            else if (remembered != null) c = remembered;
            else if (!ui.Interactive || ui.Json || available.Length == 1) c = available[0];
            else c = ManagementUi.Choose("选择提供商 · 账号登录优先", available.OrderByDescending(IsAccountProvider).ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(),
                p => p.DisplayName + (IsAccountProvider(p) ? " · 账号登录" : " · API Key"));
        }
        if (!c.IsEnabled) throw new ArgumentException("此提供商已禁用。");
        c = ManagementUi.Clone(c);
        ReasoningSettings.RememberForModel(c);
        if (model != null) c.DefaultModel = model;
        else if (ui.Interactive && !ui.Json && c.Models.Count > 1 && (forceSelection || string.IsNullOrWhiteSpace(c.DefaultModel))) c.DefaultModel = ManagementUi.Choose("选择模型", c.Models.ToArray());
        if (string.IsNullOrWhiteSpace(c.DefaultModel)) c.DefaultModel = c.Models.FirstOrDefault() ?? throw new ArgumentException("此提供商尚未配置模型。");
        if (cli.Get("max-tokens") is { }) c.MaxTokens = cli.Number("max-tokens", c.MaxTokens);
        ReasoningSettings.RestoreForModel(c);
        if (cli.Get("effort") != null || cli.Get("budget") != null)
            ReasoningSettings.Configure(c, cli.Get("effort") ?? c.ReasoningEffort, cli.Get("budget") == null ? null : cli.Number("budget", c.ThinkingBudget));
        if (cli.Get("context-window") is { } capacity) c.ModelContextWindows[c.DefaultModel] = ModelContext.Parse(capacity);
        ui.SetModelContext(c);
        if (store.ApiConfigs.Any(item => item.Id == c.Id))
        {
            store.UpdateApiConfig(c);
            store.SetSetting("terminal:lastConfig", c.Id.ToString());
        }
        return c;
    }
}
