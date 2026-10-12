using LanAgent.Core.Data;
using LanAgent.Core.Models;
using LanAgent.Core.Services.AI;
using Spectre.Console;

namespace LanAgent.Terminal;

internal sealed class ModelManager(DataStore store, TerminalUi ui, TerminalArguments cli, Func<IReadOnlyList<IAiProvider>>? pluginProviders = null, Func<string, LanAgent.Core.Extensions.IAuthenticationProvider>? authentication = null, Func<IReadOnlyList<LanAgent.Core.Extensions.IAuthenticationProvider>>? authenticationChoices = null)
{
    private IAiProvider Provider(ApiConfig config)
    {
        var provider = new AiProviderFactory(pluginProviders?.Invoke()).GetProvider(config);
        return config.AuthenticationProviderId is { Length: > 0 } id ? new LanAgent.Core.Extensions.AuthenticatedAiProvider(provider, authentication?.Invoke(id) ?? throw new ArgumentException("认证宿主不可用。")) : provider;
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
            TerminalOutput.MarkupLine("[dim]/providers add 添加 · edit 修改 · refresh 自动获取 · models 手动列表 · /model 切换[/]");
        });
    }
    public static object Safe(ApiConfig c) => new { c.Id, c.DisplayName, c.ProviderId, protocol = ProviderCatalog.ProtocolFor(c), c.BaseUrl, c.DefaultModel, c.Models, c.ReasoningProtocol, c.ReasoningEffort, c.ThinkingBudget, c.Temperature, c.MaxTokens, contextWindowTokens = ModelContext.Capacity(c), c.ModelContextWindows, c.IsEnabled, c.AuthenticationProviderId, key = c.ApiKeyEnvironmentVariable is { Length: > 0 } ? "env:" + c.ApiKeyEnvironmentVariable : c.ApiKey.Length > 0 ? "已设置（隐藏）" : "未设置" };
    public async Task SetContextAsync(ApiConfig config, string? value = null)
    {
        if (value == null)
        {
            ManagementUi.Interactive(ui);
            value = ManagementUi.Choose("模型上下文容量 · " + config.DefaultModel + " · 当前 " + ModelContext.Format(ModelContext.Capacity(config)), "100k", "200k", "400k", "1M", "自定义");
            if (value == "自定义") value = await ManagementUi.Text("上下文 Token 容量（如 131072 / 256k / 1.5M）", ModelContext.Capacity(config).ToString());
        }
        config.ModelContextWindows[config.DefaultModel] = ModelContext.Parse(value);
        if (store.ApiConfigs.FirstOrDefault(saved => saved.Id == config.Id) is { } saved)
        {
            var copy = ManagementUi.Clone(saved); copy.ModelContextWindows[config.DefaultModel] = ModelContext.Capacity(config); store.UpdateApiConfig(copy);
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
            List(); action = ManagementUi.Choose("提供商管理", "返回", "添加", "编辑", "自动获取模型", "手动模型列表", "启用", "禁用", "删除");
            if (action == "返回") return;
            action = action switch { "添加" => "add", "编辑" => "edit", "自动获取模型" => "refresh", "手动模型列表" => "models", "启用" => "enable", "禁用" => "disable", _ => "remove" };
            if (action != "add") { var chosen = PickConfig(); words = new[] { action, chosen.Id.ToString() }; }
        }
        if (action == "list") { List(); return; }
        if (action == "presets") { ManagementUi.Output(ui, ProviderCatalog.All, () => { foreach (var p in ProviderCatalog.All) TerminalOutput.Write(new Text(p.Id + " · " + p.Name + "\n")); }); return; }
        if (action == "add")
        {
            var config = cli.Get("provider") is { } provider ? FromArguments(provider) : await WizardAsync(null);
            store.AddApiConfig(config); ManagementUi.Success(ui, "提供商已添加：" + config.DisplayName); return;
        }
        var current = Find(ManagementUi.Require(words, 1, "提供商名称或 ID"));
        var copy = ManagementUi.Clone(current);
        switch (action)
        {
            case "auth": copy.AuthenticationProviderId = ManagementUi.Require(words, 2, "认证 ID（none 清除）") is var authId && authId != "none" ? authId : null; if (copy.AuthenticationProviderId != null) _ = authentication?.Invoke(copy.AuthenticationProviderId) ?? throw new ArgumentException("认证入口不存在。"); break;
            case "show": ManagementUi.Output(ui, Safe(copy), () => TerminalOutput.Write(new Panel(new Text(System.Text.Json.JsonSerializer.Serialize(Safe(copy), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }))).Header("脱敏配置"))); return;
            case "edit": copy = await WizardAsync(copy); break;
            case "refresh":
                var models = await ManagementUi.Busy("正在获取模型列表", ct => Provider(copy).ListModelsAsync(copy, ct));
                if (models.Count == 0) throw new InvalidOperationException("接口返回了空模型列表，已有列表未修改。");
                copy.Models = models.Select(m => m.Id).ToList();
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
            default: throw new ArgumentException("提供商操作：list / presets / add / edit / refresh / models / show / enable / disable / remove。");
        }
        store.UpdateApiConfig(copy); ManagementUi.Success(ui, "提供商配置已保存。");
    }
    private ApiConfig FromArguments(string id)
    {
        var preset = ProviderCatalog.Find(id) ?? throw new ArgumentException("未知预设；自定义使用 --provider custom。");
        var model = cli.Get("model") ?? throw new ArgumentException("需要 --model。");
        var config = new ApiConfig { ProviderId = preset.Id, Protocol = cli.Get("protocol") ?? preset.Protocol, BaseUrl = cli.Get("base-url") ?? preset.BaseUrl, DisplayName = cli.Get("name") ?? preset.Name, DefaultModel = model, Models = cli.Get("models") is { } list ? ParseModels(list) : new() { model }, ApiKeyEnvironmentVariable = cli.Get("key-env"), AuthenticationProviderId = preset.Id == "codex" ? "codex" : null, ReasoningProtocol = cli.Get("reasoning-protocol") ?? preset.ReasoningProtocol, MaxTokens = cli.Number("max-tokens", 16384), ThinkingBudget = cli.Number("budget", 8000) };
        if (cli.Get("context-window") is { } capacity) config.ModelContextWindows[model] = ModelContext.Parse(capacity);
        Validate(config); ReasoningSettings.SetEffort(config, cli.Get("effort") ?? "default"); return config;
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
    private async Task<ApiConfig> WizardAsync(ApiConfig? original)
    {
        ManagementUi.Interactive(ui);
        ApiConfig c;
        if (original == null)
        {
            var preset = ManagementUi.Choose("选择提供商（地址和协议均可修改）", ProviderCatalog.All, p => p.Name);
            c = new() { ProviderId = preset.Id, DisplayName = preset.Name, Protocol = preset.Protocol, BaseUrl = preset.BaseUrl, ReasoningProtocol = preset.ReasoningProtocol, AuthenticationProviderId = preset.Id == "codex" ? "codex" : null, MaxTokens = 16384 };
        }
        else c = original;
        c.DisplayName = await ManagementUi.Text("配置名称", c.DisplayName);
        c.Protocol = ManagementUi.Choose("接口协议 · 当前 " + ProviderCatalog.ProtocolFor(c), [ProviderCatalog.ProtocolFor(c), .. new[] { "openai", "responses", "anthropic", "gemini" }.Concat(pluginProviders?.Invoke().Select(p => p.ProviderId) ?? []).Where(p => p != ProviderCatalog.ProtocolFor(c))]);
        c.BaseUrl = (await ManagementUi.Text("Base URL", c.BaseUrl)).TrimEnd('/');
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
        c.ReasoningProtocol = ManagementUi.Choose("思考参数协议 · 按服务/模型文档选择", [ProviderCatalog.ReasoningFor(c), .. ReasoningSettings.Protocols.Where(p => p != ProviderCatalog.ReasoningFor(c))]);
        if (await ManagementUi.Confirm(ui, "现在从接口自动获取模型列表？"))
        {
            try { c.Models = (await ManagementUi.Busy("正在获取模型列表", ct => Provider(c).ListModelsAsync(c, ct))).Select(m => m.Id).ToList(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { ui.Error(ex.Message); }
        }
        var ids = await ManagementUi.Text("模型 ID 列表（逗号分隔，可手动补充）", c.Models.Count > 0 ? string.Join(',', c.Models) : string.IsNullOrEmpty(c.DefaultModel) ? null : c.DefaultModel);
        c.Models = ParseModels(ids);
        c.DefaultModel = ManagementUi.Choose("默认模型", c.Models.ToArray());
        c.ThinkingBudget = int.Parse(await ManagementUi.Text("思考 Token 预算（预算协议使用）", c.ThinkingBudget.ToString()));
        c.MaxTokens = int.Parse(await ManagementUi.Text("输出 Token 上限", c.MaxTokens.ToString()));
        var contextChoice = ManagementUi.Choose("默认模型上下文容量 · 当前 " + ModelContext.Format(ModelContext.Capacity(c)), "保留当前", "100k", "200k", "400k", "1M", "自定义");
        if (contextChoice != "保留当前") c.ModelContextWindows[c.DefaultModel] = ModelContext.Parse(contextChoice == "自定义" ? await ManagementUi.Text("上下文 Token 容量", ModelContext.Capacity(c).ToString()) : contextChoice);
        ReasoningSettings.SetEffort(c, ManagementUi.Choose("默认思考强度（可用 /effort 调整）", ReasoningSettings.Efforts(c).ToArray()));
        Validate(c); return c;
    }
    private ApiConfig PickConfig()
    {
        if (store.ApiConfigs.Count == 0) throw new ArgumentException("暂无配置，请先添加。");
        return ManagementUi.Choose("选择配置", store.ApiConfigs, c => c.DisplayName);
    }
    public async Task<ApiConfig> SelectAsync(string? selector = null, string? model = null, bool forceSelection = false)
    {
        ApiConfig c;
        if (selector != null) c = Find(selector);
        else if (!forceSelection && Environment.GetEnvironmentVariable("LANAGENT_MODEL") is { Length: > 0 } envModel)
        {
            var id = Environment.GetEnvironmentVariable("LANAGENT_PROVIDER") ?? "openai";
            var preset = ProviderCatalog.Find(id) ?? ProviderCatalog.Find("custom")!;
            c = new() { DisplayName = "环境变量", ProviderId = id, Protocol = Environment.GetEnvironmentVariable("LANAGENT_PROTOCOL") ?? preset.Protocol, BaseUrl = Environment.GetEnvironmentVariable("LANAGENT_BASE_URL") ?? preset.BaseUrl, DefaultModel = envModel, ApiKey = Environment.GetEnvironmentVariable("LANAGENT_API_KEY") ?? "", ReasoningProtocol = Environment.GetEnvironmentVariable("LANAGENT_REASONING_PROTOCOL") ?? preset.ReasoningProtocol };
            ReasoningSettings.SetEffort(c, Environment.GetEnvironmentVariable("LANAGENT_EFFORT") ?? "default");
        }
        else
        {
            var available = store.ApiConfigs.Where(c => c.IsEnabled).ToArray();
            if (available.Length == 0) { await ExecuteAsync(new[] { "add" }); available = store.ApiConfigs.Where(c => c.IsEnabled).ToArray(); }
            var remembered = available.FirstOrDefault(item => item.Id.ToString() == store.GetSetting("terminal:lastConfig"));
            if (!forceSelection && remembered != null) c = remembered;
            else if (!ui.Interactive || ui.Json || available.Length == 1) c = available[0];
            else c = ManagementUi.Choose("选择提供商", available, c => c.DisplayName);
        }
        if (!c.IsEnabled) throw new ArgumentException("此提供商已禁用。");
        c = ManagementUi.Clone(c);
        if (model != null) c.DefaultModel = model;
        else if (ui.Interactive && !ui.Json && c.Models.Count > 1 && (forceSelection || string.IsNullOrWhiteSpace(c.DefaultModel))) c.DefaultModel = ManagementUi.Choose("选择模型", c.Models.ToArray());
        if (string.IsNullOrWhiteSpace(c.DefaultModel)) c.DefaultModel = c.Models.FirstOrDefault() ?? throw new ArgumentException("此提供商尚未配置模型。");
        if (cli.Get("effort") is { } effort) ReasoningSettings.SetEffort(c, effort);
        if (cli.Get("budget") is { }) c.ThinkingBudget = cli.Number("budget", c.ThinkingBudget);
        if (cli.Get("max-tokens") is { }) c.MaxTokens = cli.Number("max-tokens", c.MaxTokens);
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
