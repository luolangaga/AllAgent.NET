using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AllAgent.NET.Core.Services.AI;

namespace AllAgent.NET.Core.Services.Updates;

public sealed record UpdateServiceOptions(
    string CurrentVersion,
    string StateDirectory,
    UpdateInstallContext InstallContext,
    string? GitHubRepository = null,
    string? GitCodeRepository = null,
    string? GitHubToken = null,
    string? GitCodeToken = null,
    string? NuGetPackageId = null);

public sealed class UpdateService
{
    private readonly HttpClient _http;
    private readonly UpdateStateStore _state;
    private readonly UpdateReleaseSourceFactory _sources;
    private readonly UpdateInstallContext _context;

    public UpdateService(UpdateServiceOptions options, HttpClient? http = null)
    {
        CurrentVersion = string.IsNullOrWhiteSpace(options.CurrentVersion) ? UpdateVersion.Current() : options.CurrentVersion;
        _context = options.InstallContext;
        _state = new UpdateStateStore(options.StateDirectory);
        _http = http ?? CreateHttpClient();
        _sources = new UpdateReleaseSourceFactory(
            _http,
            options.GitHubRepository ?? Environment.GetEnvironmentVariable("ALLAGENT_GITHUB_REPO") ?? UpdateReleaseSourceFactory.DefaultGitHubRepository,
            options.GitCodeRepository ?? Environment.GetEnvironmentVariable("ALLAGENT_GITCODE_REPO") ?? UpdateReleaseSourceFactory.DefaultGitCodeRepository,
            options.GitHubToken ?? Environment.GetEnvironmentVariable("ALLAGENT_GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
            options.GitCodeToken ?? Environment.GetEnvironmentVariable("ALLAGENT_GITCODE_TOKEN") ?? Environment.GetEnvironmentVariable("GITCODE_TOKEN"));
        _ = options.NuGetPackageId;
    }

    public string CurrentVersion { get; }
    public UpdateInstallContext InstallContext => _context;
    public UpdateStateStore State => _state;

    public UpdateCheckResult GetCachedResult(bool allowPrerelease = true)
    {
        var state = _state.Load();
        if (state.LatestVersion == null)
            return new UpdateCheckResult(false, CurrentVersion, null, ParseSource(state.LatestSource), _context.Kind, _context.Architecture, null, null, state.LastCheckUtc ?? DateTimeOffset.MinValue, true);
        var parsedPrerelease = UpdateVersion.TryParse(state.LatestVersion, out var parsed) && parsed.IsPrerelease;
        var prerelease = state.LatestPrerelease ?? parsedPrerelease;
        var release = new UpdateRelease(
            state.LatestVersion,
            state.LatestVersion,
            state.LatestVersion,
            state.ReleaseNotes ?? "",
            state.ReleaseUrl ?? "",
            ParseSource(state.LatestSource),
            [],
            prerelease);
        var available = UpdateVersion.IsNewer(state.LatestVersion, CurrentVersion) && (allowPrerelease || !prerelease);
        return new UpdateCheckResult(available, CurrentVersion, state.LatestVersion, release.Source, _context.Kind, _context.Architecture, release, state.LastError, state.LastCheckUtc ?? DateTimeOffset.MinValue, true);
    }

    public async Task<UpdateCheckResult> CheckAsync(UpdateSettings settings, bool force, CancellationToken ct)
    {
        var cached = GetCachedResult(settings.Channel == UpdateChannel.Preview);
        var interval = TimeSpan.FromHours(Math.Clamp(settings.CheckIntervalHours, 1, 24 * 30));
        if (!force && cached.CheckedAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - cached.CheckedAt < interval)
            return cached;

        var source = settings.Source;
        if (_context.Kind == UpdateInstallKind.NuGet)
            source = UpdateSource.NuGet;
        else if (source == UpdateSource.NuGet)
            throw new UpdateException("当前安装形态不能从 NuGet 更新。请改用 GitCode、GitHub 或 auto。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var release = await _sources.GetLatestAsync(source, settings.Channel, _context, timeout.Token);
            var checkedAt = DateTimeOffset.UtcNow;
            if (release == null)
            {
                _state.Update(state => state with
                {
                    LastCheckUtc = checkedAt,
                    LatestVersion = null,
                    LatestSource = source.ToString(),
                    ReleaseUrl = null,
                    ReleaseNotes = null,
                    LastError = null,
                });
                return new UpdateCheckResult(false, CurrentVersion, null, source, _context.Kind, _context.Architecture, null, null, checkedAt, false);
            }

            var available = settings.Channel == UpdateChannel.Preview || !release.Prerelease
                ? UpdateVersion.IsNewer(release.Version, CurrentVersion)
                : false;
            _state.Update(state => state with
            {
                LastCheckUtc = checkedAt,
                LatestVersion = release.Version,
                LatestSource = release.Source.ToString(),
                LatestPrerelease = release.Prerelease,
                ReleaseUrl = release.PageUrl,
                ReleaseNotes = release.Notes,
                LastError = null,
            });
            return new UpdateCheckResult(available, CurrentVersion, release.Version, release.Source, _context.Kind, _context.Architecture, release, null, checkedAt, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _state.Update(state => state with { LastError = "检查更新超时。" });
            return new UpdateCheckResult(false, CurrentVersion, cached.LatestVersion, source, _context.Kind, _context.Architecture, cached.Release, "检查更新超时。", DateTimeOffset.UtcNow, false);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            _state.Update(state => state with { LastError = ex.Message });
            return new UpdateCheckResult(false, CurrentVersion, cached.LatestVersion, source, _context.Kind, _context.Architecture, cached.Release, ex.Message, DateTimeOffset.UtcNow, false);
        }
    }

    public async Task<UpdateCheckResult> CheckVersionAsync(string version, UpdateSource source, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var release = await _sources.GetByVersionAsync(source, version, _context, timeout.Token)
                      ?? throw new UpdateException($"找不到版本 {version} 的发布记录。");
        return new UpdateCheckResult(false, CurrentVersion, release.Version, release.Source, _context.Kind, _context.Architecture, release, null, DateTimeOffset.UtcNow, false);
    }

    public async Task<UpdateInstallResult> InstallAsync(UpdateCheckResult check, UpdateSettings settings, UpdateInstallOptions options, CancellationToken ct)
    {
        if (check.LatestVersion == null)
            return new UpdateInstallResult(false, "已是最新版本。", Version: CurrentVersion);
        if (!check.UpdateAvailable && !options.Force)
            return new UpdateInstallResult(false, $"已是最新版本：{CurrentVersion}。", Version: CurrentVersion);

        var source = check.Source == UpdateSource.Auto ? settings.Source : check.Source;
        if (_context.Kind == UpdateInstallKind.Unknown)
            throw new UpdateException("无法识别当前安装形态（可能是源码构建），已拒绝自动覆盖。");
        if (_context.Kind == UpdateInstallKind.NuGet) source = UpdateSource.NuGet;
        if (_context.Kind != UpdateInstallKind.NuGet && source == UpdateSource.NuGet)
            throw new UpdateException("当前安装形态不能通过 NuGet 更新。");

        var release = check.Release;
        if (release == null || release.Assets.Count == 0)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            release = await _sources.GetByVersionAsync(source, check.LatestVersion, _context, timeout.Token)
                      ?? throw new UpdateException($"找不到版本 {check.LatestVersion} 的发布资源。");
        }

        var work = UpdateWorkDirectory.Create(release.Version);
        UpdateWorkDirectory.Cleanup(work.Root, TimeSpan.FromDays(30));
        var previousVersion = CurrentVersion;
        string artifact = "";
        string? previousInstaller = null;
        var warning = "";

        if (_context.Kind == UpdateInstallKind.NuGet)
        {
            if (string.IsNullOrWhiteSpace(_context.NuGetPackageId))
                throw new UpdateException("无法确定 NuGet 包 ID。");
        }
        else if (_context.Kind == UpdateInstallKind.Installer)
        {
            var installer = FindInstallerAsset(release)
                            ?? throw new UpdateException("发布中没有找到适配当前架构的安装包。");
            var checksums = await DownloadChecksumsAsync(release, ct);
            artifact = work.ArtifactPath(Path.GetFileName(installer.Name));
            await DownloadAsync(installer, artifact, ct);
            var verification = await UpdateVerification.VerifyChecksumAsync(artifact, checksums, installer.Name, settings.RequireChecksum, ct);
            if (!verification.Ok) throw new UpdateException(verification.Message);
            if (verification.Missing) warning = "发布未提供 SHA256SUMS.txt，仅跳过校验和检查。";
            var signature = UpdateVerification.VerifyAuthenticode(artifact, Environment.GetEnvironmentVariable("ALLAGENT_UPDATE_PUBLISHER"));
            if (signature.IsBlocking) throw new UpdateException(signature.Message ?? "更新包签名无效。");
            if (signature.Status == UpdateSignatureStatus.Unsigned)
            {
                if (settings.RequireSignature || !options.AllowUnsigned)
                    throw new UpdateException("更新包没有 Authenticode 签名；启用签名要求后拒绝安装。");
                warning = string.IsNullOrEmpty(warning) ? "更新包未签名，已按校验和验证。" : warning + " 更新包未签名。";
            }
            previousInstaller = await GetOrDownloadPreviousInstallerAsync(work, source, previousVersion, ct);
            CacheInstallerForRollback(work, release.Version, artifact, installer.Name);
        }
        else
        {
            throw new UpdateException("无法识别当前安装形态，已拒绝自动覆盖。");
        }

        var plan = UpdateLauncher.BuildPlan(
            work,
            _context,
            release.Source,
            release.Version,
            previousVersion,
            artifact,
            previousInstaller,
            restart: options.Restart,
            restartArguments: options.RestartArguments,
            workingDirectory: options.WorkingDirectory);

        UpdateLauncher.WritePlan(work, plan);
        UpdateLauncher.Launch(work);
        _state.Update(state => state with
        {
            PreviousVersion = previousVersion,
            LastUpdateUtc = DateTimeOffset.UtcNow,
            LastResult = "started",
            LastError = null,
            LastLogPath = work.LogPath,
        });

        var message = string.IsNullOrEmpty(warning)
            ? $"已开始更新到 {release.Version}（{release.Source}）。"
            : $"已开始更新到 {release.Version}（{release.Source}）；{warning}";
        return new UpdateInstallResult(true, message, work.LogPath, release.Version, previousVersion, work.PlanPath);
    }

    public async Task<UpdateInstallResult> RollbackAsync(UpdateSettings settings, UpdateInstallOptions options, CancellationToken ct)
    {
        var state = _state.Load();
        if (string.IsNullOrWhiteSpace(state.PreviousVersion))
            throw new UpdateException("没有记录可回滚的上一版本。");
        var source = _context.Kind == UpdateInstallKind.NuGet ? UpdateSource.NuGet : settings.Source;
        var check = await CheckVersionAsync(state.PreviousVersion, source, ct);
        return await InstallAsync(check, settings, options with { Force = true }, ct);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = OperatingSystem.IsWindows() ? new CurrentWindowsProxy() : null,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AllAgent.NET-Updater", UpdateVersion.Normalize(UpdateVersion.Current())));
        return client;
    }

    private async Task<string?> DownloadChecksumsAsync(UpdateRelease release, CancellationToken ct)
    {
        var asset = release.FindAsset("SHA256SUMS.txt", "sha256sums.txt", "checksums.txt");
        return asset == null ? null : await DownloadTextAsync(asset, ct);
    }

    private async Task<string> DownloadTextAsync(UpdateAsset asset, CancellationToken ct)
    {
        using var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);
        return text.Length > 4 * 1024 * 1024 ? text[..(4 * 1024 * 1024)] : text;
    }

    private async Task DownloadAsync(UpdateAsset asset, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".part";
        using var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length is > 1024L * 1024 * 1024) throw new UpdateException("更新文件超过 1GB，已拒绝下载。");
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.SequentialScan))
        {
            var buffer = new byte[131072];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > 1024L * 1024 * 1024) throw new UpdateException("更新文件超过 1GB，已中止下载。");
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            target.Flush(flushToDisk: true);
        }
        File.Move(temporary, destination, overwrite: true);
    }

    private static UpdateAsset? FindInstallerAsset(UpdateRelease release)
    {
        var candidates = release.Assets.Where(asset => asset.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = candidates.FirstOrDefault(asset => asset.Name.Contains("-" + UpdateArchitecture.Host + "-", StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        return UpdateArchitecture.Host == "win-arm64"
            ? candidates.FirstOrDefault(asset => asset.Name.Contains("-win-x64-", StringComparison.OrdinalIgnoreCase))
            : candidates.FirstOrDefault();
    }

    private async Task<string?> GetOrDownloadPreviousInstallerAsync(UpdateWorkDirectory work, UpdateSource source, string previousVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(previousVersion)) return null;
        var rollbackRoot = Path.Combine(work.Root, "rollback", UpdateVersion.Normalize(previousVersion));
        if (Directory.Exists(rollbackRoot))
        {
            var cached = Directory.EnumerateFiles(rollbackRoot, "*-setup.exe")
                .FirstOrDefault(path => Path.GetFileName(path).Contains("-" + _context.Architecture + "-", StringComparison.OrdinalIgnoreCase))
                ?? Directory.EnumerateFiles(rollbackRoot, "*-setup.exe").FirstOrDefault();
            if (cached != null) return cached;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var release = await _sources.GetByVersionAsync(source, previousVersion, _context, timeout.Token);
            if (release == null) return null;
            var installer = FindInstallerAsset(release);
            if (installer == null) return null;
            Directory.CreateDirectory(rollbackRoot);
            var destination = Path.Combine(rollbackRoot, Path.GetFileName(installer.Name));
            if (!File.Exists(destination)) await DownloadAsync(installer, destination, ct);
            return destination;
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    private static void CacheInstallerForRollback(UpdateWorkDirectory work, string version, string artifact, string assetName)
    {
        try
        {
            var directory = Path.Combine(work.Root, "rollback", UpdateVersion.Normalize(version));
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, assetName);
            if (!File.Exists(target)) File.Copy(artifact, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static UpdateSource ParseSource(string? value) =>
        Enum.TryParse<UpdateSource>(value, ignoreCase: true, out var parsed) ? parsed : UpdateSource.Auto;
}

public static class UpdateServiceFactory
{
    public static UpdateService Create(string stateDirectory, Func<string, string?>? settings = null, HttpClient? http = null)
    {
        _ = settings;
        var context = UpdateInstallContext.Detect();
        var current = UpdateVersion.Current();
        var options = new UpdateServiceOptions(current, stateDirectory, context);
        return new UpdateService(options, http);
    }

    public static UpdateService Create(UpdateServiceOptions options, HttpClient? http = null) => new(options, http);
}

public static class UpdateHealth
{
    public static void WriteIfRequested(string? marker) => UpdateLauncher.WriteHealthMarker(marker);

    public static UpdateExecutionResult? ReadAndApply(string? resultPath, UpdateStateStore state)
    {
        var result = UpdateLauncher.ReadResult(resultPath);
        if (result == null) return null;
        state.Update(current => current with
        {
            LastResult = result.Status,
            LastError = result.Status == "failed" ? result.Error : null,
            LastInstalledVersion = result.Status == "success" ? result.Version : current.LastInstalledVersion,
            LastUpdateUtc = result.FinishedAt ?? DateTimeOffset.UtcNow,
            PreviousVersion = result.PreviousVersion ?? current.PreviousVersion,
        });
        return result;
    }
}
