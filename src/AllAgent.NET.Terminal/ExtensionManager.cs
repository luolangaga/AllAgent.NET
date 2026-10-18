using System.Diagnostics;
using System.Text.Json;
using AllAgent.NET.Core.Extensions;
using AllAgent.NET.Core.Services;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal sealed class ExtensionManager(PluginHost host, BackgroundTasks tasks, string dataDirectory, TerminalUi ui, TerminalArguments args, DataStore store)
{
    private string ProfilesPath => Path.Combine(dataDirectory, "oauth-profiles.json");
    private IReadOnlyList<OAuthProfile> Profiles => File.Exists(ProfilesPath) ? JsonSerializer.Deserialize<OAuthProfile[]>(File.ReadAllText(ProfilesPath)) ?? [] : [];
    private readonly IReadOnlyList<IAuthenticationProvider> _builtins = [new ChatGptAuthentication(dataDirectory)];
    public IReadOnlyList<IAuthenticationProvider> Authentication => _builtins.Concat(Profiles.Where(p => !_builtins.Any(a => a.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase))).Select(p => (IAuthenticationProvider)new OAuthAuthentication(p, new TokenVault(Path.Combine(dataDirectory, "credentials"))))).Concat(host.Authentication.Where(a => !_builtins.Any(b => b.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase)))).ToArray();
    public IReadOnlyList<ModelFooterProvider> FooterProviders => _builtins.OfType<ChatGptAuthentication>().Select(a => a.FooterProvider).Concat(host.FooterProviders).ToArray();
    public IAuthenticationProvider FindAuthentication(string id) => Authentication.SingleOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("登录提供商不存在；使用 /login 查看内置入口，插件提供商请先 /plugins import 安装对应插件包。");
    private void Idle() { if (tasks.Active) throw new InvalidOperationException("请先取消或完成后台任务，再更改插件 / Hooks / 认证配置。"); }
    public void ListPlugins()
    {
        ManagementUi.Output(ui, host.Status, () =>
        {
            var table = new Table().Title(".NET 插件").AddColumn("包 / 版本").AddColumn("插件").AddColumn("状态");
            foreach (var plugin in host.Status) table.AddRow(Markup.Escape(plugin.PackageId + " · " + plugin.Version), Markup.Escape(plugin.Name), Markup.Escape(plugin.Error ?? (plugin.Enabled ? "启用" : "禁用")));
            TerminalOutput.Write(table); TerminalOutput.Write(new Text("/plugins search 关键词 · install 包ID 版本 · import 文件.nupkg · enable/disable/remove 包ID\n中文开发文档：docs/plugins.zh-CN.md\n"));
        });
    }
    public async Task PluginsAsync(IReadOnlyList<string> words, CancellationToken ct)
    {
        var action = words.FirstOrDefault() ?? (ui.Interactive && !ui.Json ? "panel" : "list");
        var installer = new NuGetPluginInstaller(dataDirectory, source: args.Get("nuget-source") ?? "https://api.nuget.org/v3/index.json");
        if (action == "panel")
        {
            ListPlugins(); var choice = ManagementUi.Choose("插件管理", "返回", "搜索 NuGet 插件", "本地包安装", "启用", "禁用", "卸载"); if (choice == "返回") return;
            action = choice switch { "搜索 NuGet 插件" => "search", "本地包安装" => "import", "启用" => "enable", "禁用" => "disable", _ => "remove" };
            if (action == "import") words = [action, await ManagementUi.Text(".nupkg 文件路径")];
            else if (action != "search") { if (host.Installed.Count == 0) return; var chosen = ManagementUi.Choose("选择插件", host.Installed, p => p.PackageId + " · " + p.Version); words = [action, chosen.PackageId]; }
        }
        if (action == "list") { ListPlugins(); return; }
        if (action == "search")
        {
            var search = await ManagementUi.Busy("搜索 NuGet · AllAgent.NET / LanAgent 标签", token => installer.SearchAsync(string.Join(' ', words.Skip(1)), ct: token), ct);
            var found = search.Packages;
            ManagementUi.Output(ui, search, () => { var table = new Table().AddColumn("包 / 版本").AddColumn("作者 / 简介"); foreach (var package in found) table.AddRow(Markup.Escape(package.Id + " · " + package.Version), Markup.Escape(package.Authors + "\n" + package.Description)); TerminalOutput.Write(table); });
            if (found.Count == 0) { ManagementUi.Success(ui, "没有匹配的 AllAgent.NET / LanAgent 标签插件；也可以安装本地 .nupkg。"); return; }
            if (ui.Interactive && !ui.Json)
            {
                var choice = ManagementUi.Choose("选择安装或返回", found.Select(p => p.Id + " · " + p.Version).Prepend("返回").ToArray());
                if (choice == "返回") return; var selected = found.Single(p => choice == p.Id + " · " + p.Version); await PluginsAsync(["install", selected.Id, selected.Version], ct);
            }
            return;
        }
        Idle();
        if (action is "install" or "import")
        {
            var installed = await ManagementUi.Busy("下载并校验插件及依赖", token => action == "import" ? installer.ImportAsync(ManagementUi.Require(words, 1, "包路径"), token) : installer.InstallAsync(ManagementUi.Require(words, 1, "包 ID"), ManagementUi.Require(words, 2, "版本"), token), ct);
            // Download and inspect first; loading the DLL is the final, explicit trust decision.
            var allow = args.Flags.Contains("trust-plugin") || await ManagementUi.Confirm(ui, $"安装并启用 {installed.PackageId} {installed.Version}？DLL 会以当前用户权限运行；AllAgent 标签不代表官方认证。");
            if (!allow) { ManagementUi.Success(ui, "包已校验，未加载或启用。可以重新安装后确认。"); return; }
            if (host.Installed.Any(p => p.PackageId.Equals(installed.PackageId, StringComparison.OrdinalIgnoreCase))) await host.SetEnabledAsync(installed.PackageId, false);
            host.Save(installed); await host.LoadEnabledAsync(ct); ListPlugins(); return;
        }
        var id = ManagementUi.Require(words, 1, "包 ID");
        if (action is "enable" or "disable") { await host.SetEnabledAsync(id, action == "enable"); ListPlugins(); return; }
        if (action == "remove") { await host.RemoveAsync(id); ManagementUi.Success(ui, "插件已卸载，文件保留以便恢复。"); return; }
        throw new ArgumentException("插件操作：list / search / install / import / enable / disable / remove。");
    }
    public void Hooks(IReadOnlyList<string> words)
    {
        var action = words.FirstOrDefault() ?? (ui.Interactive && !ui.Json ? "panel" : "list");
        if (action == "panel")
        {
            var choice = ManagementUi.Choose("生命周期 Hooks", "返回", "查看 Hooks", "启用 Hook", "禁用 Hook", "查看回调错误");
            if (choice == "返回") return;
            action = choice switch { "启用 Hook" => "enable", "禁用 Hook" => "disable", "查看回调错误" => "errors", _ => "list" };
            if (action is "enable" or "disable")
            {
                Idle();
                var candidates = host.HookStatus.Where(h => h.Enabled == (action == "disable")).ToArray();
                if (candidates.Length == 0) { ManagementUi.Success(ui, "没有可" + (action == "disable" ? "禁用" : "启用") + "的 Hook。"); return; }
                words = [action, ManagementUi.Choose("选择 Hook", candidates, h => h.Name).Name];
            }
        }
        if (action == "errors") { ManagementUi.Output(ui, host.Hooks.Errors, () => TerminalOutput.Write(new Text(string.Join('\n', host.Hooks.Errors) + "\n"))); return; }
        if (action is "enable" or "disable") { Idle(); host.SetHookEnabled(ManagementUi.Require(words, 1, "Hook 名称"), action == "enable"); }
        else if (action != "list") throw new ArgumentException("Hooks 操作：list / enable 名称 / disable 名称；由插件注册生命周期回调。");
        ManagementUi.Output(ui, host.HookStatus.Select(h => new { h.Name, h.Enabled }).ToArray(), () => { var table = new Table().Title("生命周期 Hooks").AddColumn("名称").AddColumn("状态"); foreach (var hook in host.HookStatus) table.AddRow(Markup.Escape(hook.Name), hook.Enabled ? "启用" : "禁用"); TerminalOutput.Write(table); TerminalOutput.Write(new Text("BeforeTurn · BeforeTool · AfterTool · AfterTurn · TurnError；每次回调限时 5 秒。\n安装插件即可扩展；示例插件提供操作审计 Hook。\n")); });
    }
    public async Task LoginAsync(IReadOnlyList<string> words, CancellationToken ct)
    {
        if (words.FirstOrDefault() is "status" or "list") { await LoginStatusAsync(ct); return; }
        Idle();
        await ModelCatalog.RefreshAsync(ct);
        IAuthenticationProvider selected;
        if (words.FirstOrDefault() is { } providerId && ProviderCatalog.Find(providerId) != null && !Authentication.Any(a => a.Id == providerId))
        {
            if (words.Count > 1) throw new ArgumentException("自定义提供商使用 /login custom。");
            await new ModelManager(store, ui, args, () => host.Providers, FindAuthentication, () => Authentication).AddCustomAsync(providerId);
            return;
        }
        if (words.Count > 0) selected = FindAuthentication(words[0]);
        else
        {
            ManagementUi.Interactive(ui);
            const string customChoice = "添加自定义提供商 · API Key / 本地服务";
            var apiChoices = ProviderCatalog.All.Concat(ModelCatalog.Providers).DistinctBy(p => p.Id).Where(p => p.Id != "custom" && !Authentication.Any(a => a.Id == p.Id)).ToArray();
            var choices = Authentication.Select(a => a.Name + " · " + a.Id).Concat(apiChoices.Select(p => p.Name + " · API Key · " + p.Id)).Append(customChoice).Prepend("返回").ToArray();
            var choice = ManagementUi.Choose("登录账号 · 选择提供商", choices);
            if (choice == "返回") return;
            if (choice == customChoice)
            {
                await new ModelManager(store, ui, args, () => host.Providers, FindAuthentication, () => Authentication).AddCustomAsync();
                return;
            }
            var apiPreset = apiChoices.FirstOrDefault(p => choice == p.Name + " · API Key · " + p.Id);
            if (apiPreset != null)
            {
                await new ModelManager(store, ui, args, () => host.Providers, FindAuthentication, () => Authentication).AddCustomAsync(apiPreset.Id);
                return;
            }
            selected = Authentication.Single(a => choice == a.Name + " · " + a.Id);
            var operation = ManagementUi.Choose(selected.Name, "登录", "查看状态", "退出登录", "返回");
            if (operation == "返回") return;
            if (operation == "查看状态") { await ShowLoginStatusAsync(selected, ct); return; }
            if (operation == "退出登录") { await LogoutAsync([selected.Id], ct); return; }
        }
        if (words.Count > 1 && !(selected.Id == "codex" && words[1] == "new")) throw new ArgumentException("登录方式：/login ID；Codex 可用 new 添加账号。");
        var newAccount = selected.Id == "codex" && words.ElementAtOrDefault(1) == "new";
        if (selected is ChatGptAuthentication chatGpt && words.Count == 0)
        {
            var accounts = await chatGpt.GetAccountsAsync(ct);
            if (accounts.Count > 0)
            {
                var accountOptions = accounts.Select(a => (a.Email ?? "ChatGPT 账号") + " · " + a.ClientId).Prepend("添加新的账号 / 工作区").ToArray();
                var choice = ManagementUi.Choose("选择 ChatGPT 账号", accountOptions);
                newAccount = choice == accountOptions[0];
                if (!newAccount) await chatGpt.SelectAccountAsync(accounts.Single(a => choice == (a.Email ?? "ChatGPT 账号") + " · " + a.ClientId).ClientId, ct);
            }
        }
        async Task<bool> Login(CancellationToken token)
        {
            Task OpenBrowser(Uri uri, CancellationToken browserToken)
            {
                browserToken.ThrowIfCancellationRequested();
                if (selected.LoginInstructions is { } note) TerminalOutput.Write(new Text(note + "\n"));
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return Task.CompletedTask;
            }
            if (selected is ChatGptAuthentication openAi) await openAi.LoginAsync(OpenBrowser, newAccount, token);
            else await selected.LoginAsync(OpenBrowser, token);
            return true;
        }
        if (selected is IInteractiveAuthentication interactive)
        {
            // Paste-in providers (API keys) ask for the secret first, then validate and store it.
            var entered = FullScreenTerminal.Current is { } interactiveScreen
                ? await interactiveScreen.TextAsync(interactive.InputPrompt, null, secret: true, empty: false, ct)
                : await ManagementUi.Text(interactive.InputPrompt, secret: true);
            async Task<bool> Validate(CancellationToken token) { await interactive.LoginWithInputAsync(entered, token); return true; }
            if (FullScreenTerminal.Current is { } validateScreen)
                await validateScreen.BusyAsync("验证凭据并保存登录", Validate, ct, () => "正在验证凭据");
            else await ManagementUi.Busy("验证凭据并保存登录", Validate, ct);
        }
        else if (FullScreenTerminal.Current is { } screen)
            await screen.BusyAsync("等待账号授权", Login, ct, () => selected.LoginInstructions ?? "等待浏览器登录", allowEscape: true);
        else await ManagementUi.Busy("等待账号授权（最多 5 分钟）", Login, ct);
        ui.FooterRevision++;
        await ShowLoginStatusAsync(selected, ct);
        if (selected is ChatGptAuthentication) await ConnectChatGptModelsAsync(selected, ct);
        else await ConnectAccountModelsAsync(selected, ct);
    }
    private async Task ConnectAccountModelsAsync(IAuthenticationProvider selected, CancellationToken ct)
    {
        if (selected is not IAccountModelProvider accountModels) return;
        var config = accountModels.CreateConfiguration();
        var previous = store.ApiConfigs.FirstOrDefault(c => c.AuthenticationProviderId == selected.Id);
        try
        {
            var models = await accountModels.ModelProvider.ListModelsAsync(config, ct);
            if (models.Count == 0) throw new InvalidOperationException("账号没有返回可用模型。");
            config.Models = models.Select(m => m.Id).ToList();
            ModelCatalog.Remember(config, models);
            config.MaxTokens = 0;
            config.DefaultModel = previous != null && config.Models.Contains(previous.DefaultModel)
                ? previous.DefaultModel : config.Models[0];
            if (previous != null) { config.Id = previous.Id; store.UpdateApiConfig(config); }
            else store.AddApiConfig(config);
            ManagementUi.Success(ui, "账号模型已连接：" + config.DefaultModel + "；使用 /model 切换。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (previous != null) { var disabled = ManagementUi.Clone(previous); disabled.IsEnabled = false; store.UpdateApiConfig(disabled); }
            ui.Error("账号已登录，模型目录连接失败：" + ex.Message + "；请重新 /login " + selected.Id);
        }
    }
    public async Task LogoutAsync(IReadOnlyList<string> words, CancellationToken ct)
    {
        Idle();
        if (words.Count == 0) ManagementUi.Interactive(ui);
        var selected = words.Count > 0 ? FindAuthentication(words[0])
            : ManagementUi.Choose("选择要退出的账号", Authentication, a => a.Name + " · " + a.Id);
        await selected.LogoutAsync(ct);
        ui.FooterRevision++;
        foreach (var config in store.ApiConfigs.Where(c => c.AuthenticationProviderId == selected.Id)) { var copy = ManagementUi.Clone(config); copy.IsEnabled = false; store.UpdateApiConfig(copy); }
        ManagementUi.Success(ui, "已退出 " + selected.Name + "。本地登录凭据已清除；服务端撤销以提供商说明为准。");
    }
    private async Task LoginStatusAsync(CancellationToken ct)
    {
        var statuses = new List<object>();
        var table = new Table().Title("账号登录状态").AddColumn("提供商").AddColumn("账号").AddColumn("模型连接").AddColumn("说明");
        foreach (var entry in Authentication)
        {
            try
            {
                var status = await entry.GetStatusAsync(ct);
                statuses.Add(new { entry.Id, entry.Name, status.LoggedIn, status.ModelConnectionAvailable, status.Account, status.Message });
                table.AddRow(Markup.Escape(entry.Name), status.LoggedIn ? "[green]已登录[/]" + (status.Account == null ? "" : "\n" + Markup.Escape(status.Account)) : "[dim]未登录[/]",
                    status.ModelConnectionAvailable ? (status.LoggedIn ? "可绑定模型" : "等待登录") : "[yellow]尚未接通[/]", Markup.Escape(status.Message ?? ""));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                statuses.Add(new { entry.Id, entry.Name, LoggedIn = false, Message = ex.Message });
                table.AddRow(Markup.Escape(entry.Name), "[red]状态检查失败[/]", "待检查", Markup.Escape(ex.Message));
            }
        }
        ManagementUi.Output(ui, statuses, () => TerminalOutput.Write(table));
    }
    private async Task ShowLoginStatusAsync(IAuthenticationProvider selected, CancellationToken ct)
    {
        var status = await selected.GetStatusAsync(ct);
        ManagementUi.Output(ui, new { selected.Id, selected.Name, status.LoggedIn, status.ModelConnectionAvailable, status.Account, status.Message }, () =>
        {
            TerminalOutput.Write(new Panel(new Text(selected.Name + "\n" + (status.LoggedIn ? "账号已登录" : "账号未登录") +
                (status.Account == null ? "" : " · " + status.Account) + "\n" +
                (status.ModelConnectionAvailable ? "支持模型认证绑定；还需获取模型并验证实际请求。" : "模型连接未就绪。") + "\n" + status.Message)).Header("登录结果"));
        });
    }
    private async Task ConnectChatGptModelsAsync(IAuthenticationProvider authentication, CancellationToken ct)
    {
        var previous = store.ApiConfigs.FirstOrDefault(c => c.ProviderId == "codex" && c.AuthenticationProviderId == authentication.Id);
        var config = previous == null ? new ApiConfig { ProviderId = "codex", Protocol = "responses", BaseUrl = "https://api.openai.com/v1", DisplayName = "Codex / ChatGPT", AuthenticationProviderId = "codex", ReasoningProtocol = "openai" } : ManagementUi.Clone(previous);
        try
        {
            var models = await ManagementUi.Busy("获取该 ChatGPT 账号可用的模型", token => new AuthenticatedAiProvider(new ResponsesProvider(), authentication).ListModelsAsync(config, token), ct);
            if (models.Count == 0) throw new InvalidOperationException("该账号暂未返回可展示的模型，请检查工作区权限后重试。");
            config.Models = models.Select(m => m.Id).ToList();
                ModelCatalog.Remember(config, models);
                config.MaxTokens = 0;
            if (!config.Models.Contains(config.DefaultModel)) config.DefaultModel = config.Models[0];
            config.IsEnabled = true;
            if (previous == null) store.AddApiConfig(config); else store.UpdateApiConfig(config);
            ManagementUi.Success(ui, "账号模型目录已连接：" + config.DefaultModel + "；使用 /model 切换。实际可用性将在首次请求确认。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An old account's catalog must not remain selectable after account switching fails.
            if (previous != null) { config.Models = []; config.DefaultModel = ""; config.IsEnabled = false; store.UpdateApiConfig(config); }
            ui.Error("账号已登录，但模型目录尚未连接：" + ex.Message + "。可通过 /login codex 重试。");
        }
    }
}
