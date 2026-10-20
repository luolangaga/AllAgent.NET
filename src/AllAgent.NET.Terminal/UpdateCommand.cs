using System.Text.Json;
using AllAgent.NET.Core.Services.Updates;
using AllAgent.NET.Core.Tools;
using L = AllAgent.NET.Core.Localization.HostText;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal sealed record UpdateCommandOutcome(int ExitCode, bool ExitApplication);

internal static class UpdateCommand
{
    internal static async Task<UpdateCommandOutcome> RunAsync(
        TerminalArguments args,
        TerminalUi ui,
        string stateDirectory,
        string[]? restartArgs,
        bool guiLauncher,
        Func<string, string?>? settings,
        CancellationToken ct = default)
    {
        if (args.Positionals.Count > 0)
            throw new ArgumentException(L.Pick("update 不接受位置参数。", "update does not accept positional arguments."));
        var configured = settings == null ? UpdateSettings.FromEnvironment() : UpdateSettings.FromSettings(settings);
        var sourceValue = args.Get("source");
        var parsedSource = UpdateSettings.ParseSource(sourceValue);
        if (sourceValue != null && parsedSource == null)
            throw new ArgumentException(L.Pick("无效的更新来源；可用 auto、gitcode、github、nuget。", "Invalid update source; use auto, gitcode, github or nuget."));
        var source = parsedSource ?? configured.Source;
        var channel = args.Flags.Contains("pre") ? UpdateChannel.Preview : configured.Channel;
        var requireSignature = args.Flags.Contains("require-signature") || configured.RequireSignature;
        var requireChecksum = !args.Flags.Contains("allow-missing-checksum") && configured.RequireChecksum;
        var effective = configured with
        {
            Source = source,
            Channel = channel,
            RequireSignature = requireSignature,
            RequireChecksum = requireChecksum,
        };
        var service = UpdateServiceFactory.Create(stateDirectory, settings);
        try
        {
            if (args.Flags.Contains("rollback"))
                return await RollbackAsync(service, effective, args, ui, restartArgs, guiLauncher, ct);

            var check = await service.CheckAsync(effective, force: true, ct);
            PrintCheck(ui, check);
            if (check.Error != null)
                return new UpdateCommandOutcome(1, false);
            if (args.Flags.Contains("check"))
                return new UpdateCommandOutcome(0, false);
            if (!check.UpdateAvailable && !args.Flags.Contains("force"))
                return new UpdateCommandOutcome(0, false);

            if (!args.Flags.Contains("yes"))
            {
                if (!ui.Interactive || ui.Json)
                    throw new UpdateException(L.Pick("非交互环境安装更新需要 --yes。", "Installing an update in a non-interactive session requires --yes."));
                var question = L.Pick(
                    $"发现新版本 {check.LatestVersion}（当前 {check.CurrentVersion}，来源 {check.Source}）。是否立即下载并安装？",
                    $"Update {check.LatestVersion} is available (current {check.CurrentVersion}, source {check.Source}). Download and install now?");
                if (!await ManagementUi.Confirm(ui, question))
                {
                    ManagementUi.Output(ui, new { started = false, message = "已取消。" }, () => TerminalOutput.Write(new Text(L.Pick("已取消。\n", "Cancelled.\n"))));
                    return new UpdateCommandOutcome(0, false);
                }
            }

            var options = new UpdateInstallOptions(
                effective,
                Force: args.Flags.Contains("force"),
                AllowUnsigned: !requireSignature && !args.Flags.Contains("require-signature"),
                Restart: restartArgs != null && !args.Flags.Contains("no-restart"),
                RestartArguments: restartArgs,
                WorkingDirectory: Environment.CurrentDirectory,
                GuiLauncher: guiLauncher);
            var result = await service.InstallAsync(check, effective, options, ct);
            ManagementUi.Output(ui, new
            {
                started = result.Started,
                version = result.Version,
                previousVersion = result.PreviousVersion,
                message = result.Message,
                logPath = result.LogPath,
                planPath = result.PlanPath,
                error = result.Error,
            }, () =>
            {
                if (result.Started)
                {
                    TerminalOutput.MarkupLine("[green]✓ " + Markup.Escape(result.Message) + "[/]");
                    if (result.LogPath != null) TerminalOutput.Write(new Text(L.Pick("更新日志：", "Update log: ") + result.LogPath + "\n", new Style(Color.Grey)));
                    TerminalOutput.Write(new Text(options.Restart
                        ? L.Pick("应用即将退出；更新程序会在安装完成后自动重启。\n", "The application will exit; the updater restarts it after installation.\n")
                        : L.Pick("应用即将退出；更新程序会在后台完成安装。\n", "The application will exit; the updater finishes the installation in the background.\n"), new Style(Color.Grey)));
                }
                else
                {
                    TerminalOutput.Write(new Text(result.Message + "\n"));
                }
            });
            // The detached updater waits for this process, so it must exit after a
            // successful launch even when --no-restart suppresses the restart.
            return new UpdateCommandOutcome(0, result.Started);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        {
            ManagementUi.Output(ui, new { started = false, error = ex.Message }, () => ui.Error(ex.Message));
            return new UpdateCommandOutcome(1, false);
        }
    }

    private static async Task<UpdateCommandOutcome> RollbackAsync(
        UpdateService service,
        UpdateSettings settings,
        TerminalArguments args,
        TerminalUi ui,
        string[]? restartArgs,
        bool guiLauncher,
        CancellationToken ct)
    {
        var state = service.State.Load();
        if (string.IsNullOrWhiteSpace(state.PreviousVersion))
            throw new UpdateException(L.Pick("没有记录可回滚的上一版本。", "No previous version is recorded for rollback."));
        if (!args.Flags.Contains("yes"))
        {
            if (!ui.Interactive || ui.Json)
                throw new UpdateException(L.Pick("非交互环境回滚需要 --yes。", "Rolling back in a non-interactive session requires --yes."));
            if (!await ManagementUi.Confirm(ui, L.Pick($"回滚到 {state.PreviousVersion}？", $"Roll back to {state.PreviousVersion}?")))
                return new UpdateCommandOutcome(0, false);
        }
        var options = new UpdateInstallOptions(
            settings,
            Force: true,
            AllowUnsigned: !settings.RequireSignature,
            Restart: restartArgs != null && !args.Flags.Contains("no-restart"),
            RestartArguments: restartArgs,
            WorkingDirectory: Environment.CurrentDirectory,
            GuiLauncher: guiLauncher);
        var result = await service.RollbackAsync(settings, options, ct);
        ManagementUi.Output(ui, new
        {
            started = result.Started,
            version = result.Version,
            message = result.Message,
            logPath = result.LogPath,
        }, () =>
        {
            TerminalOutput.MarkupLine("[green]✓ " + Markup.Escape(result.Message) + "[/]");
            if (result.LogPath != null) TerminalOutput.Write(new Text(L.Pick("更新日志：", "Update log: ") + result.LogPath + "\n", new Style(Color.Grey)));
        });
        return new UpdateCommandOutcome(0, result.Started);
    }

    private static void PrintCheck(TerminalUi ui, UpdateCheckResult check)
    {
        ManagementUi.Output(ui, new
        {
            currentVersion = check.CurrentVersion,
            latestVersion = check.LatestVersion,
            updateAvailable = check.UpdateAvailable,
            source = check.Source.ToString(),
            installKind = check.InstallKind.ToString(),
            architecture = check.Architecture,
            releaseUrl = check.ReleaseUrl,
            checkedAt = check.CheckedAt,
            fromCache = check.FromCache,
            error = check.Error,
        }, () =>
        {
            TerminalOutput.Write(new Text(
                L.Pick("当前版本：", "Current: ") + check.CurrentVersion +
                L.Pick(" · 安装形态：", " · install: ") + check.InstallKind +
                " · " + check.Architecture + "\n" +
                (check.LatestVersion == null
                    ? L.Pick("没有可用的发布版本。\n", "No published release was found.\n")
                    : L.Pick("最新版本：", "Latest: ") + check.LatestVersion + "（" + check.Source + "）" +
                      (check.UpdateAvailable ? L.Pick(" · 有可用更新\n", " · update available\n") : L.Pick(" · 已是最新\n", " · up to date\n"))) +
                (string.IsNullOrWhiteSpace(check.ReleaseUrl) ? "" : L.Pick("发布页：", "Release: ") + check.ReleaseUrl + "\n") +
                (string.IsNullOrWhiteSpace(check.Error) ? "" : L.Pick("检查错误：", "Check error: ") + check.Error + "\n")));
        });
    }
}
