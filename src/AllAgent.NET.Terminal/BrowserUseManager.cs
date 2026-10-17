using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text.Json;
using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AllAgent.NET.Terminal;

internal sealed class BrowserUseManager(DataStore store, BrowserUseState state, TerminalUi ui)
{
    public void Load() => state.Load(store);
    public async Task ExecuteAsync(IReadOnlyList<string> args)
    {
        var current = state.Snapshot().Configuration;
        var command = args.FirstOrDefault() ?? (ui.Interactive && !ui.Json ? "panel" : "status");
        switch (command)
        {
            case "panel": ManagementUi.Interactive(ui); await PanelAsync(); return;
            case "on": case "off":
                if (args.Count != 1) throw new ArgumentException("browser on|off");
                state.Save(store, current with { Enabled = command == "on" }); break;
            case "mode":
                if (args.Count != 2) throw new ArgumentException("browser mode builtin|chromium");
                state.Save(store, current with { Mode = args[1] }); break;
            case "executable": case "cdp":
                if (args.Count != 2) throw new ArgumentException("browser executable 浏览器绝对路径 | browser cdp http://127.0.0.1:9222");
                state.Save(store, command == "cdp" ? current with { CdpUrl = args[1], ExecutablePath = "", Mode = "chromium" } : current with { ExecutablePath = args[1], CdpUrl = "", Mode = "chromium" }); break;
            case "connect": await ConnectAsync(); return;
            case "reconnect": await ConnectAsync(reuseSaved: true); return;
            case "help": case "tutorial": Tutorial(); return;
            case "status": if (args.Count > 1) throw new ArgumentException("browser status"); break;
            default: throw new ArgumentException("browser on|off|status|mode builtin|chromium|connect|reconnect|tutorial|cdp URL|executable PATH");
        }
        var value = state.Snapshot().Configuration;
        ManagementUi.Output(ui, value, () => ManagementUi.Success(ui, $"Browser Use：{(value.Enabled ? "开启" : "关闭")} · {(value.Mode == "builtin" ? "内置浏览器（使用时自动打开窗口）" : "外置 " + value.BrowserName)} · {value.CdpUrl}{value.ExecutablePath}" +
            (value.ProfileDirectory.Length > 0 ? "\n已保存配置：" + value.ProfileDirectory : "")));
    }

    private void Write(string value) => ManagementUi.Output(ui, new { tutorial = value }, () => TerminalOutput.Write(new Text(value + "\n")));

    internal static string LaunchCommand(string executable, int port, string profile) =>
        "& '" + executable.Replace("'", "''") + "' --remote-debugging-address=127.0.0.1 --remote-debugging-port=" + port + " --user-data-dir='" + profile.Replace("'", "''") + "' --no-first-run about:blank";

    private void Tutorial() => Write("""
        外部浏览器连接教程（无需扩展）
        1. 输入 /browser，选择 Edge 或 Chrome，再选择你平时使用的配置；列表显示名称和完整目录。
        2. 未找到配置或使用其他目录时，选择“手动选择已有配置目录”。在 edge://version 或 chrome://version 查看“个人资料路径”，复制该目录。
        3. 程序会用所选配置打开远程调试设置页。在页面启用“允许对此浏览器实例进行远程调试”，如有授权弹窗请允许。向导自动等待并验证连接。
        4. 成功后浏览器和终端都会提示已连接，随后返回聊天。输入任务才开始操作。
        5. 不会新建或复制你的配置，不会强制关闭日常浏览器。该版本不支持远程调试设置页时，可连接已有本机 CDP 地址，或使用内置 WebView2。
        6. Chrome 136 起，旧的 --remote-debugging-port 参数不能用于默认数据目录；请使用浏览器自身远程调试设置，不能仅靠添加启动参数绕过。
        7. 内置模式无需连接，执行任务时弹出 WebView2 窗口。/browser off 关闭功能，/browser on 开启。
        8. 已选的用户目录会保存，下次在列表首项使用上次配置，或输入 /browser reconnect 直接重连。每次启动后首次连接请留意浏览器的授权弹窗并点击允许；授权不等于重新选择目录。
        详细教程：docs/browser-use.zh-CN.md
        """);

    internal static readonly string[] PanelActions = ["切换内置浏览器（任务时打开）", "连接 Microsoft Edge（自动启动）", "连接 Google Chrome（自动启动）", "连接其他 Chromium 浏览器", "连接已有调试地址", "切换总开关", "查看连接教程", "重连上次保存的浏览器配置", "返回聊天"];

    internal IRenderable PanelBody(string notice = "")
    {
        var value = state.Snapshot().Configuration;
        var lastBrowser = store.GetSetting("browser:last-browser");
        var lastPath = string.IsNullOrEmpty(lastBrowser) ? null : store.GetSetting("browser:last-profile:" + lastBrowser);
        return new Text(TerminalSafeText.Clean((notice.Length == 0 ? "" : notice + "\n\n") + $"Browser Use · {(value.Enabled ? "已开启" : "已关闭")} · {(value.Mode == "builtin" ? "内置 WebView2" : "外置 " + value.BrowserName)}\n" +
            (value.Mode == "builtin" ? "内置模式无需连接，使用时打开独立窗口。" : "连接地址：" + (value.CdpUrl.Length > 0 ? value.CdpUrl : "尚未配置")) +
            (value.Mode == "chromium" && value.ProfileDirectory.Length > 0 ? "\n已验证配置：" + value.ProfileDirectory : "") +
            (!string.IsNullOrEmpty(lastPath) && lastPath != value.ProfileDirectory ? "\n上次选择（已保存）：" + lastPath : "") +
            "\n首次连接请允许浏览器授权；可重连上次保存的配置，无需重选目录。"));
    }

    private async Task PanelAsync()
    {
        var notice = "";
        while (true)
        {
            string selected;
            try { selected = await ManagementUi.ChooseWithBody("浏览器管理", PanelActions, PanelBody(notice)); }
            catch (OperationCanceledException) { return; }
            var current = state.Snapshot().Configuration;
            try
            {
                switch (Array.IndexOf(PanelActions, selected))
                {
                    case 0:
                        state.Save(store, current with { Mode = "builtin" });
                        ManagementUi.Success(ui, "已切换内置 WebView2。已返回聊天，请输入浏览器任务；执行任务时会打开窗口。"); return;
                    case 1: case 2: case 3: case 4:
                        var browser = Array.IndexOf(PanelActions, selected) switch { 1 => "Microsoft Edge", 2 => "Google Chrome", 3 => "其他 Chromium 浏览器", _ => "已有调试地址" };
                        var result = await ConnectAsync(browser); notice = result.Message;
                        if (result.Connected) return;
                        break;
                    case 5: state.Save(store, current with { Enabled = !current.Enabled }); notice = current.Enabled ? "Browser Use 已关闭，操作已中止。" : "Browser Use 已开启。"; break;
                    case 6: Tutorial(); return;
                    case 7:
                        var reconnected = await ConnectAsync(reuseSaved: true); notice = reconnected.Message;
                        if (reconnected.Connected) return;
                        break;
                    default: return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException) { notice = ex.Message; }
        }
    }

    private sealed record ConnectionResult(bool Connected, string Message);
    private async Task<ConnectionResult> ConnectAsync(string? selected = null, bool reuseSaved = false)
    {
        ManagementUi.Interactive(ui);
        if (reuseSaved)
        {
            selected = store.GetSetting("browser:last-browser") ?? state.Snapshot().Configuration.BrowserName;
            if (string.IsNullOrEmpty(selected) || selected == "已有调试地址")
                throw new InvalidOperationException("尚未保存浏览器用户目录，请先选择 Edge/Chrome 和已有配置。");
        }
        var browser = selected ?? await ManagementUi.ChooseWithBody("选择浏览器", ["Microsoft Edge", "Google Chrome", "其他 Chromium 浏览器", "已有调试地址"], PanelBody());
        var endpoint = ""; var executable = ""; var phase = "准备连接";
        BrowserProfile? profile = null;
        var kind = browser == "Microsoft Edge" ? "edge" : browser == "Google Chrome" ? "chrome" : "custom";
        if (browser == "已有调试地址") endpoint = await ManagementUi.Text("粘贴本机 CDP 地址", "http://127.0.0.1:9222");
        else
        {
            var candidates = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }
                .Select(root => Path.Combine(root, kind == "edge" ? @"Microsoft\Edge\Application\msedge.exe" : @"Google\Chrome\Application\chrome.exe"));
            var savedExecutable = store.GetSetting("browser:last-executable:" + browser);
            executable = savedExecutable != null && File.Exists(savedExecutable) ? savedExecutable : kind == "custom" ? "" : candidates.FirstOrDefault(File.Exists) ?? "";
            if (executable.Length == 0) executable = (await ManagementUi.Text("未自动找到浏览器，请输入 exe 完整路径")).Trim().Trim('"');
            BrowserUseState.Validate(new(ExecutablePath: executable));
            var profiles = kind == "custom" ? [] : BrowserProfiles.Read(BrowserProfiles.DefaultRoot(kind));
            const string manual = "手动选择已有配置目录";
            var savedProfile = SavedProfile(store, state.Snapshot().Configuration, browser);
            var savedChoice = savedProfile == null ? null : "使用上次配置：" + savedProfile.Label;
            if (reuseSaved)
                profile = savedProfile ?? throw new InvalidOperationException("上次保存的用户目录已不可用，请重新选择已有配置。原路径仍保留在设置中。");
            else
            {
                var choices = profiles.Where(p => !string.Equals(p.Path, savedProfile?.Path, StringComparison.OrdinalIgnoreCase)).Select(p => p.Label).ToList();
                if (savedChoice != null) choices.Insert(0, savedChoice);
                choices.Add(manual);
                var choice = await ManagementUi.ChooseWithBody("选择 AI 使用的浏览器配置", choices,
                    new Text("上次选择的目录会保留并优先显示。使用已有登录资料，不创建新目录。\n可在 edge://version 或 chrome://version 查看“个人资料路径”。"));
                profile = choice == savedChoice ? savedProfile : choice == manual
                    ? BrowserProfiles.FromPath(await ManagementUi.Text("粘贴已有配置目录（例如 User Data\\Default）", savedProfile?.Path ?? ""))
                    : profiles.First(p => p.Label == choice);
            }
            RememberProfile(store, browser, executable, profile!);

        }
        await using var connectionPage = profile == null ? null : new BrowserConnectionPage(kind);
        var marker = connectionPage?.Url;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            IRenderable Progress() => new Text(TerminalSafeText.Clean($"{browser} · {phase}\n" + (executable.Length > 0 ? "程序：" + executable + "\n" : "") +
                (endpoint.Length > 0 ? "地址：" + endpoint + "\n" : "") + (profile != null ? "配置：" + profile.Path + "\n" : "") + "请在浏览器的 Remote debugging 页面启用允许调试；若出现授权弹窗，请点击允许。\n等待连接最多 60 秒，随后自动验证页面控制。"));
            async Task<bool> Connect(CancellationToken ct)
            {
                async Task Retain(AllAgent.NET.Core.Browser.CdpConnection connection, string address)
                {
                    ct.ThrowIfCancellationRequested();
                    state.Save(store, state.Snapshot().Configuration with { Mode = "chromium", CdpUrl = address, ExecutablePath = "", BrowserName = browser,
                        ProfileDirectory = profile?.Path ?? "", TabId = connection.ActiveTabId });
                    await state.AdoptExternalAsync(connection, ct);
                }
                var tabId = "";
                if (browser == "已有调试地址")
                {
                    phase = "检测已有调试地址"; await ProbeAsync(endpoint, ct);
                }
                else
                {
                    phase = "打开所选配置的远程调试设置";
                    var setup = ExistingProfileStartInfo(executable, profile!, kind, marker!);
                    // Use the browser's own opt-in for an existing daily profile. The legacy
                    // debugging-port flag is ignored by newer browsers on default roots.
                    using var launched = Process.Start(setup) ?? throw new InvalidOperationException("浏览器未启动。");
                    phase = "请在浏览器设置页勾选允许远程调试；等待所选目录的调试服务";
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(TimeSpan.FromSeconds(60));
                    try
                    {
                        while (true)
                        {
                            wait.Token.ThrowIfCancellationRequested();
                            var discovered = BrowserProfiles.DebugEndpoint(profile!.UserDataDirectory);
                            if (discovered != null)
                            {
                                try
                                {
                                    phase = "验证所选配置的页面控制权限；请允许浏览器授权";
                                    tabId = await VerifyControlAsync(discovered, wait.Token, showReadyPage: true, preferredUrl: marker, onVerified: connection => Retain(connection, discovered));
                                    endpoint = discovered; break;
                                }
                                catch (System.Net.WebSockets.WebSocketException ex) when (ex.InnerException is HttpRequestException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionRefused } })
                                { /* A stale DevToolsActivePort may briefly precede the new socket. */ }
                            }
                            await Task.Delay(500, wait.Token);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    { throw new TimeoutException("未能连接所选配置。请在远程调试设置页启用允许调试，并允许授权弹窗；旧版浏览器不支持时，请升级或连接已有调试地址。未创建替代资料目录。"); }
                }
                phase = "验证页面控制权限；若浏览器弹出授权，请点击允许";
                if (tabId.Length == 0) await VerifyControlAsync(endpoint, ct, showReadyPage: true, onVerified: connection => Retain(connection, endpoint));
                return true;
            }
            if (FullScreenTerminal.Current is { } screen)
                await screen.BusyAsync("连接外置浏览器", Connect, cancellation.Token, titleProvider: () => phase, allowEscape: true, bodyProvider: Progress);
            else { Write("正在自动启动并连接 " + browser + "。若浏览器提示远程调试授权，请点击允许；程序将自动验证页面控制……"); await Connect(cancellation.Token); }
            var message = "已连接 " + browser + (profile != null ? " · " + profile.Name + "\n配置：" + profile.Path : "") + "\n地址：" + endpoint + "，页面控制验证通过。\n已返回聊天，请输入任务，例如：打开必应并搜索今天的天气。" + (state.Snapshot().Configuration.Enabled ? "" : "\n总开关仍关闭，请先 /browser on。");
            ManagementUi.Success(ui, message); return new(true, message);
        }
        catch (OperationCanceledException) { Write("连接已取消，原配置保留。已打开的浏览器窗口可自行关闭。"); throw; }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.WebSockets.WebSocketException or JsonException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or ArgumentException or IOException)
        {
            var message = "连接未成功，原配置保留：" + ToolResult.Clip(ex.Message, 220) +
                "\n请检查浏览器是否打开，或企业策略是否禁止远程调试；可重新选择连接或使用内置模式。";
            Write(message); return new(false, message);
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }

    internal static BrowserProfile? SavedProfile(DataStore store, BrowserUseConfiguration configuration, string browser)
    {
        var path = store.GetSetting("browser:last-profile:" + browser) ?? (configuration.BrowserName == browser ? configuration.ProfileDirectory : "");
        if (string.IsNullOrEmpty(path)) return null;
        try { return BrowserProfiles.FromPath(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return null; }
    }

    internal static void RememberProfile(DataStore store, string browser, string executable, BrowserProfile profile)
    {
        // Persist the user's selection separately from the live connection. A consent
        // cancellation must retain it without replacing the last verified connection.
        store.SetSetting("browser:last-browser", browser);
        store.SetSetting("browser:last-profile:" + browser, profile.Path);
        store.SetSetting("browser:last-executable:" + browser, executable);
    }

    internal static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 9222);
        try { listener.Start(); return 9222; }
        catch (SocketException)
        {
            var alternate = new TcpListener(IPAddress.Loopback, 0);
            try { alternate.Start(); return ((IPEndPoint)alternate.LocalEndpoint).Port; }
            finally { alternate.Stop(); }
        }
        finally { listener.Stop(); }
    }

    internal static ProcessStartInfo ExistingProfileStartInfo(string executable, BrowserProfile profile, string kind, string marker)
    {
        BrowserUseState.Validate(new(ExecutablePath: executable));
        // Reject missing profiles before starting: Chromium otherwise silently creates them.
        BrowserProfiles.FromPath(profile.Path);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--user-data-dir=" + profile.UserDataDirectory);
        info.ArgumentList.Add("--profile-directory=" + profile.DirectoryName);
        info.ArgumentList.Add("--no-first-run");
        info.ArgumentList.Add("--no-default-browser-check");
        info.ArgumentList.Add(marker);
        info.ArgumentList.Add(kind == "edge" ? "edge://inspect/#remote-debugging" : "chrome://inspect/#remote-debugging");
        return info;
    }

    internal static ProcessStartInfo BrowserStartInfo(string executable, int port, string profile, string profileDirectory = "")
    {
        BrowserUseState.Validate(new(ExecutablePath: executable));
        if (port is < 1024 or > 65535 || !Path.IsPathFullyQualified(profile)) throw new ArgumentException("浏览器端口或资料目录无效。");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=" + port, "--user-data-dir=" + profile, "--no-first-run", "about:blank" }) info.ArgumentList.Add(arg);
        if (profileDirectory.Length > 0) info.ArgumentList.Add("--profile-directory=" + profileDirectory);
        return info;
    }

    internal static async Task<int> LaunchAndConnectAsync(string executable, int port, string profile, Action<string> progress, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(BrowserStartInfo(executable, port, profile)) ?? throw new InvalidOperationException("浏览器未启动。");
        var id = process.Id;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Exception? lastError = null;
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested(); progress("等待浏览器调试服务就绪");
                try { await ProbeAsync("http://127.0.0.1:" + port, timeout.Token); return id; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or JsonException or InvalidOperationException or ArgumentException)
                { timeout.Token.ThrowIfCancellationRequested(); lastError = ex; }
                await Task.Delay(250, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("浏览器已启动，但 15 秒内未能连接调试端口。" + (lastError == null ? "" : " " + lastError.Message)); }
    }

    internal static async Task ProbeAsync(string endpoint, CancellationToken ct = default)
    {
        BrowserUseState.Validate(new(CdpUrl: endpoint));
        if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("向导检测请使用 http://127.0.0.1:端口 地址。");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        string json;
        try { json = await http.GetStringAsync(endpoint.TrimEnd('/') + "/json/version", ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("本机调试地址在 2 秒内未响应：" + endpoint); }
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("webSocketDebuggerUrl", out var debugger) || debugger.GetString() is not { Length: > 0 } url)
            throw new InvalidOperationException("端口可访问，但不是浏览器调试服务。");
        BrowserUseState.Validate(new(CdpUrl: url));
    }

    internal const string ReadyPageTitle = "AllAgent · 浏览器已连接";
    internal static async Task<string> VerifyControlAsync(string endpoint, CancellationToken ct = default, bool showReadyPage = false, string? preferredUrl = null,
        Func<AllAgent.NET.Core.Browser.CdpConnection, Task>? onVerified = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var connection = await AllAgent.NET.Core.Browser.CdpConnection.ConnectAsync(endpoint, deadline.Token, preferredUrl: preferredUrl);
            var retained = false;
            try
            {
                var result = await connection.SendAsync("Runtime.evaluate", new { expression = "1 + 1", returnByValue = true }, deadline.Token);
                if (!result.TryGetProperty("result", out var remote) || !remote.TryGetProperty("value", out var value) || !value.TryGetInt32(out var number) || number != 2)
                    throw new InvalidOperationException("调试端口可访问，但页面控制验证失败；连接未保存。");
                if (showReadyPage)
                {
                    // Write through the same CDP path that will execute page tasks. Do not
                    // replace any user page; use an existing blank tab or our own new tab.
                    var tabs = await connection.TabsAsync(deadline.Token);
                    var blank = tabs.FirstOrDefault(tab => tab.Url == (preferredUrl ?? "about:blank"));
                    if (blank != null) await connection.SwitchTabAsync(blank.TabId, deadline.Token);
                    else await connection.NewTabAsync(deadline.Token);
                    var html = "<!doctype html><meta charset='utf-8'><title>" + ReadyPageTitle + "</title><style>body{font:18px system-ui;max-width:720px;margin:12vh auto;padding:32px;color:#e9e9ed;background:#171719}h1{font-size:32px}p{line-height:1.8}code{color:#f5bba5}</style><h1>浏览器已连接 ✓</h1><p>AllAgent 已通过 CDP 成功控制此页面。</p><p>请回到终端输入任务，例如：<code>打开必应，搜索今天的天气</code>。</p><p>选择浏览器完成的是连接设置。输入任务后，AI 才会开始打开网站、点击或输入。</p>";
                    var script = "document.open(); document.write(" + JsonSerializer.Serialize(html) + "); document.close(); document.title";
                    var proof = await connection.SendAsync("Runtime.evaluate", new { expression = script, returnByValue = true }, deadline.Token);
                    if (!proof.TryGetProperty("result", out var rendered) || !rendered.TryGetProperty("value", out var title) || title.GetString() != ReadyPageTitle)
                        throw new InvalidOperationException("浏览器打开了，但连接确认页未能显示；连接未保存。");
                }
                if (onVerified != null) { await onVerified(connection); retained = true; }
                return connection.ActiveTabId;
            }
            finally { if (!retained) await connection.DisposeAsync(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("等待浏览器控制授权超时。请在 Edge/Chrome 中允许远程调试，然后重新连接。"); }
    }
}
