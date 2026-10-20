using AllAgent.NET.Core.Data;
using AllAgent.NET.Core.Services.Updates;
using L = AllAgent.NET.Core.Localization.HostText;
using Spectre.Console;

namespace AllAgent.NET.Terminal;

internal static class UpdateStartupCheck
{
    public static string? Notice { get; private set; }
    public static string? Error { get; private set; }

    public static async Task<bool> TryAutoInstallAsync(DataStore store, TerminalUi ui, string[] raw, CancellationToken ct)
    {
        var settings = UpdateSettings.FromSettings(store.GetSetting);
        if (!settings.AutoInstall) return false;
        try
        {
            var service = UpdateServiceFactory.Create(store.SharedDirectoryPath ?? store.DirectoryPath, store.GetSetting);
            var check = await service.CheckAsync(settings, force: false, ct);
            if (check.Error != null || !check.UpdateAvailable) return false;
            Notice = L.Pick($"发现新版本 v{check.LatestVersion}（{check.Source}），正在静默更新…", $"Version {check.LatestVersion} is available ({check.Source}); installing silently...");
            TerminalOutput.Write(new Text(Notice + "\n", new Style(Color.Yellow)));
            var options = new UpdateInstallOptions(
                settings,
                Restart: true,
                RestartArguments: raw,
                WorkingDirectory: Environment.CurrentDirectory);
            var result = await service.InstallAsync(check, settings, options, ct);
            if (!result.Started)
            {
                Error = result.Message;
                TerminalOutput.Write(new Text(result.Message + "\n", new Style(Color.Grey)));
                return false;
            }
            Error = null;
            TerminalOutput.Write(new Text(L.Pick("更新程序已启动，应用将退出并在安装后自动重启。\n", "The updater is running; the application exits and restarts after installation.\n"), new Style(Color.Grey)));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
            return false;
        }
    }

    public static async Task RunAsync(DataStore store, TerminalUi ui, string stateDirectory, CancellationToken ct)
    {
        var settings = UpdateSettings.FromSettings(store.GetSetting);
        if (!settings.AutoCheck) return;
        try
        {
            var service = UpdateServiceFactory.Create(stateDirectory, store.GetSetting);
            var check = await service.CheckAsync(settings, force: false, ct);
            if (check.Error != null)
            {
                Error = check.Error;
                return;
            }
            if (!check.UpdateAvailable || string.IsNullOrWhiteSpace(check.LatestVersion))
            {
                Notice = null;
                return;
            }
            Error = null;
            Notice = L.Pick(
                $"发现新版本 v{check.LatestVersion}（{check.Source}）；运行 allagent update 或 /update 升级。",
                $"Version {check.LatestVersion} is available ({check.Source}); run allagent update or /update.");
            if (FullScreenTerminal.Current == null)
                ManagementUi.Output(ui, new { notice = Notice }, () => TerminalOutput.Write(new Text(Notice + "\n", new Style(Color.Yellow))));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
    }
}
