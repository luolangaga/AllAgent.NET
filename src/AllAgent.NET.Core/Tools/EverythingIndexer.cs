using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AllAgent.NET.Core.Runtime;

namespace AllAgent.NET.Core.Tools;

/// <summary>One owned, hidden folder indexer per host. Never controls the user's Everything.</summary>
internal static class EverythingIndexer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _process;
    private static FileStream? _cacheLock;
    private static string? _root;
    private static string? _instance;

    static EverythingIndexer() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();

    internal sealed class Lease(string instance) : IDisposable
    {
        public string Instance { get; } = instance;
        public void Dispose() => Gate.Release();
    }

    internal static async Task<Lease> AcquireAsync(string root, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_root == root && _process is { HasExited: false }) return new(_instance!);
            Stop();
            var executable = Path.Combine(Path.GetDirectoryName(EverythingSearch.ExecutablePath)!, "Everything.exe");
            if (!File.Exists(executable)) throw new IOException("Bundled Everything.exe is missing. Restore the application package.");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AllAgent.NET", "Everything", hash);
            Directory.CreateDirectory(cache);
            try { _cacheLock = new(Path.Combine(cache, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                // Concurrent hosts must not write the same database.
                cache = Path.Combine(cache, Environment.ProcessId.ToString());
                Directory.CreateDirectory(cache);
                _cacheLock = new(Path.Combine(cache, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            var config = Path.Combine(cache, "Everything.ini");
            await File.WriteAllTextAsync(config, Configuration(root), new UTF8Encoding(true), ct);
            _instance = $"AllAgent-{Environment.ProcessId}-{hash}";
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = cache };
            // A folder index has no USN journal to replay changes made while
            // stopped. Rebuild on worker start; reuse the live index thereafter.
            foreach (var arg in new[] { "-instance", _instance, "-startup", "-reindex", "-config", config, "-db", Path.Combine(cache, "Everything.db") }) info.ArgumentList.Add(arg);
            _process = Process.Start(info) ?? throw new IOException("Could not start the bundled Everything indexer.");
            _root = root;
            // ES may run before the IPC window exists. Retry startup readiness,
            // then ES's timeout waits for the initial database to finish loading.
            for (var attempt = 0; attempt < 100; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new IOException($"Bundled Everything exited during startup ({_process.ExitCode}).");
                var probe = await BoundedProcess.RunAsync(BoundedProcess.StartInfo(EverythingSearch.ExecutablePath,
                    ["-argv", "-instance", _instance, "-get-everything-version"]), TimeSpan.FromSeconds(2), ct);
                if (probe.ExitCode == 0) return new(_instance);
                await Task.Delay(100, ct);
            }
            throw new TimeoutException("Bundled Everything did not become ready.");
        }
        catch { Stop(); Gate.Release(); throw; }
    }

    internal static string Configuration(string root) => $"""
        [Everything]
        app_data=0
        run_as_admin=0
        run_in_background=1
        show_tray_icon=0
        check_for_updates_on_startup=0
        auto_include_fixed_volumes=0
        auto_include_removable_volumes=0
        auto_include_fixed_refs_volumes=0
        auto_include_removable_refs_volumes=0
        ntfs_volume_guids=
        ntfs_volume_paths=
        ntfs_volume_includes=
        refs_volume_guids=
        refs_volume_paths=
        refs_volume_includes=
        folders="{root.Replace("\\", "\\\\").Replace("\"", "\\\"")}"
        folder_monitor_changes=1
        folder_rescan_if_full_list=1
        folder_update_types=1
        folder_update_intervals=5
        folder_update_interval_types=0
        http_server_enabled=0
        etp_server_enabled=0
        """;

    internal static void Stop()
    {
        // Graceful exit saves the latest monitored changes, rather than an old
        // snapshot from the first query. Control only our private instance.
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited && _instance != null)
                {
                    using var exit = Process.Start(BoundedProcess.StartInfo(EverythingSearch.ExecutablePath,
                        ["-argv", "-instance", _instance, "-exit"]));
                    exit?.WaitForExit(2000);
                    _process.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { BoundedProcess.Kill(_process); _process.Dispose(); _process = null; }
        }
        _cacheLock?.Dispose(); _cacheLock = null; _root = null; _instance = null;
    }
}
