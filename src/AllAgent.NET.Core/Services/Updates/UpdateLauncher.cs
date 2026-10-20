using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllAgent.NET.Core.Services.Updates;

public sealed record UpdatePlan
{
    public string Kind { get; init; } = "";
    public int WaitPid { get; init; }
    public string Version { get; init; } = "";
    public string PreviousVersion { get; init; } = "";
    public string InstallDirectory { get; init; } = "";
    public string Artifact { get; init; } = "";
    public string InstallCommandLine { get; init; } = "";
    public string RollbackInstallCommandLine { get; init; } = "";
    public string? PreviousInstaller { get; init; }
    public string? NuGetPackageId { get; init; }
    public string? NuGetToolPath { get; init; }
    public string? DotNetPath { get; init; }
    public bool Restart { get; init; }
    public bool AllowLauncherExit { get; init; }
    public bool RequireElevation { get; init; }
    public string RestartFileName { get; init; } = "";
    public string RestartCommandLine { get; init; } = "";
    public string RestartCommandLineWithHealth { get; init; } = "";
    public string RestartCommandLineWithResult { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string HealthMarker { get; init; } = "";
    public int HealthTimeoutSeconds { get; init; } = 90;
    public string LogPath { get; init; } = "";
    public string ResultPath { get; init; } = "";
}

public sealed record UpdateExecutionResult(string? Status, string? Error, string? Version, string? PreviousVersion, DateTimeOffset? FinishedAt);

public static class UpdateLauncher
{
    private const string Script = """
param([Parameter(Mandatory=$true)][string]$PlanPath)
$ErrorActionPreference = 'Stop'
$plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
$logDirectory = Split-Path -Parent $plan.LogPath
if ($logDirectory) { New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null }
function Write-UpdateLog([string]$Message) {
    $line = ('{0:yyyy-MM-dd HH:mm:ss.fff} {1}' -f (Get-Date), $Message)
    Add-Content -LiteralPath $plan.LogPath -Value $line -Encoding UTF8
}
function Write-UpdateResult([string]$Status, [string]$Error) {
    $result = [ordered]@{
        Status = $Status
        Version = $plan.Version
        PreviousVersion = $plan.PreviousVersion
        Error = $Error
        FinishedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $plan.ResultPath -Encoding UTF8
}
function Wait-ProcessExit([int]$ProcessId, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}
function Invoke-Installer([string]$Installer, [string]$Arguments) {
    Write-UpdateLog ("Running installer: {0} {1}" -f $Installer, $Arguments)
    if ($plan.RequireElevation) {
        Write-UpdateLog 'Requesting elevation for the all-users install.'
        $process = Start-Process -FilePath $Installer -ArgumentList $Arguments -Verb RunAs -Wait -PassThru
    } else {
        $process = Start-Process -FilePath $Installer -ArgumentList $Arguments -Wait -PassThru
    }
    return $process.ExitCode
}
function Start-UpdatedApplication([string]$CommandLine) {
    Write-UpdateLog ("Starting: {0} {1}" -f $plan.RestartFileName, $CommandLine)
    if ([string]::IsNullOrWhiteSpace($CommandLine)) {
        return Start-Process -FilePath $plan.RestartFileName -WorkingDirectory $plan.WorkingDirectory -PassThru
    }
    return Start-Process -FilePath $plan.RestartFileName -ArgumentList $CommandLine -WorkingDirectory $plan.WorkingDirectory -PassThru
}
function Wait-Health([int]$ProcessId, [string]$Marker) {
    $deadline = (Get-Date).AddSeconds([int]$plan.HealthTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Marker) {
            Start-Sleep -Seconds 2
            if (-not $plan.AllowLauncherExit -and -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { return $false }
            return $true
        }
        if (-not $plan.AllowLauncherExit) {
            if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { return $false }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function Update-Package([string]$Version) {
    $dotnet = if ($plan.DotNetPath) { [string]$plan.DotNetPath } else { 'dotnet' }
    $toolArgs = @('tool', 'update')
    if ($plan.NuGetToolPath) { $toolArgs += @('--tool-path', [string]$plan.NuGetToolPath) } else { $toolArgs += '--global' }
    $toolArgs += @([string]$plan.NuGetPackageId, '--version', $Version)
    Write-UpdateLog ("Running: {0} {1}" -f $dotnet, ($toolArgs -join ' '))
    & $dotnet @toolArgs 2>&1 | ForEach-Object { Write-UpdateLog ([string]$_) }
    return $LASTEXITCODE
}
try {
    Write-UpdateLog ("Waiting for process {0} to exit" -f $plan.WaitPid)
    if (-not (Wait-ProcessExit -ProcessId ([int]$plan.WaitPid) -TimeoutSeconds 180)) {
        throw 'The running application did not exit.'
    }
    if ($plan.Kind -eq 'installer') {
        $code = Invoke-Installer -Installer ([string]$plan.Artifact) -Arguments ([string]$plan.InstallCommandLine)
        if ($code -ne 0 -and $code -ne 3010) { throw "Installer exited with code $code." }
    }
    elseif ($plan.Kind -eq 'nuget') {
        $code = Update-Package -Version ([string]$plan.Version)
        if ($code -ne 0) { throw "dotnet tool update exited with code $code." }
    }
    else {
        throw "Unknown update kind: $($plan.Kind)"
    }

    if (-not $plan.Restart) {
        Write-UpdateResult -Status 'success' -Error ''
        Write-UpdateLog 'Update succeeded; restart was not requested.'
        exit 0
    }

    if (Test-Path -LiteralPath $plan.HealthMarker) { Remove-Item -LiteralPath $plan.HealthMarker -Force -ErrorAction SilentlyContinue }
    $process = Start-UpdatedApplication -CommandLine ([string]$plan.RestartCommandLineWithHealth)
    if (-not (Wait-Health -ProcessId $process.Id -Marker ([string]$plan.HealthMarker))) {
        throw 'The updated application did not report a healthy start.'
    }
    Write-UpdateResult -Status 'success' -Error ''
    Write-UpdateLog 'Update succeeded; the new version is running.'
    exit 0
}
catch {
    $message = $_.Exception.Message
    Write-UpdateLog ("Update failed: {0}" -f $message)
    try {
        if ($plan.Kind -eq 'installer' -and $plan.PreviousInstaller -and (Test-Path -LiteralPath $plan.PreviousInstaller)) {
            Write-UpdateLog ("Rolling back with {0}" -f $plan.PreviousInstaller)
            $code = Invoke-Installer -Installer ([string]$plan.PreviousInstaller) -Arguments ([string]$plan.RollbackInstallCommandLine)
            if ($code -ne 0 -and $code -ne 3010) { Write-UpdateLog ("Rollback installer exited with code {0}." -f $code) }
        }
        elseif ($plan.Kind -eq 'nuget' -and $plan.PreviousVersion) {
            $code = Update-Package -Version ([string]$plan.PreviousVersion)
            if ($code -ne 0) { Write-UpdateLog ("Rollback package update exited with code {0}." -f $code) }
        }
    }
    catch {
        Write-UpdateLog ("Rollback failed: {0}" -f $_.Exception.Message)
    }
    Write-UpdateResult -Status 'failed' -Error $message
    if ($plan.Restart) {
        try { Start-UpdatedApplication -CommandLine ([string]$plan.RestartCommandLineWithResult) | Out-Null }
        catch { Write-UpdateLog ("Could not restart the application: {0}" -f $_.Exception.Message) }
    }
    exit 1
}
""";

    private static readonly JsonSerializerOptions PlanOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static UpdatePlan BuildPlan(
        UpdateWorkDirectory work,
        UpdateInstallContext context,
        UpdateSource source,
        string version,
        string previousVersion,
        string artifact,
        string? previousInstaller,
        bool restart,
        string[]? restartArguments,
        string? workingDirectory)
    {
        var installDirectory = context.InstallDirectory;
        var allUsers = IsUnderProgramFiles(installDirectory);
        var installCommandLine = string.Join(' ', new[]
        {
            "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS",
            allUsers ? "/ALLUSERS" : null,
            "/DIR=" + Quote(installDirectory),
        }.Where(part => !string.IsNullOrEmpty(part)).Select(part => part!));
        var rollbackCommandLine = installCommandLine;
        var restartFileName = ResolveRestart(context, restartArguments, out var arguments);
        var commandLine = string.Join(' ', arguments.Select(Quote));
        var healthCommandLine = string.Join(' ', arguments.Concat(["--update-health", work.HealthMarkerPath]).Select(Quote));
        var resultCommandLine = string.Join(' ', arguments.Concat(["--update-result", work.ResultPath]).Select(Quote));
        return new UpdatePlan
        {
            Kind = context.Kind switch
            {
                UpdateInstallKind.NuGet => "nuget",
                UpdateInstallKind.Installer => "installer",
                _ => throw new UpdateException("无法识别当前安装形态，已拒绝自动覆盖。"),
            },
            WaitPid = Environment.ProcessId,
            Version = version,
            PreviousVersion = previousVersion,
            InstallDirectory = installDirectory,
            Artifact = artifact,
            InstallCommandLine = installCommandLine,
            RollbackInstallCommandLine = rollbackCommandLine,
            PreviousInstaller = previousInstaller,
            NuGetPackageId = context.NuGetPackageId,
            NuGetToolPath = context.NuGetToolPath,
            DotNetPath = context.DotNetPath,
            Restart = restart,
            AllowLauncherExit = restartArguments == null ||
                                restartArguments.Any(argument => argument.Equals("gui", StringComparison.OrdinalIgnoreCase) ||
                                                                  argument.Equals("-guimode", StringComparison.OrdinalIgnoreCase) ||
                                                                  argument.Equals("--guimode", StringComparison.OrdinalIgnoreCase)),
            RequireElevation = context.Kind == UpdateInstallKind.Installer && allUsers && !Environment.IsPrivilegedProcess,
            RestartFileName = restartFileName,
            RestartCommandLine = commandLine,
            RestartCommandLineWithHealth = healthCommandLine,
            RestartCommandLineWithResult = resultCommandLine,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            HealthMarker = work.HealthMarkerPath,
            HealthTimeoutSeconds = 90,
            LogPath = work.LogPath,
            ResultPath = work.ResultPath,
        };
    }

    public static string ResolveRestart(UpdateInstallContext context, string[]? restartArguments, out string[] arguments)
    {
        // A .NET tool is updated into a new .store/<version> directory. Restarting the
        // old DLL path would silently run the previous version, so use the shim that
        // dotnet tool install/update rewrites.
        if (context.Kind == UpdateInstallKind.NuGet)
        {
            var toolPath = context.NuGetToolPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
            foreach (var name in new[] { "allagent.exe", "allagent.cmd", "allagent" })
            {
                var shim = Path.Combine(toolPath, name);
                if (!File.Exists(shim)) continue;
                arguments = restartArguments ?? [];
                return shim;
            }
        }

        var processPath = Environment.ProcessPath;
        var entry = Assembly.GetEntryAssembly();
        var raw = restartArguments ?? (Environment.GetCommandLineArgs() is { Length: > 0 } command ? command[1..] : []);
        if (processPath == null || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            arguments = entry == null ? raw : [entry.Location, .. raw];
            return processPath ?? "dotnet";
        }
        arguments = raw;
        return processPath;
    }

    public static string WritePlan(UpdateWorkDirectory work, UpdatePlan plan)
    {
        File.WriteAllText(work.PlanPath, JsonSerializer.Serialize(plan, PlanOptions), new UTF8Encoding(false));
        File.WriteAllText(work.ScriptPath, Script, new UTF8Encoding(false));
        return work.PlanPath;
    }

    public static int Launch(UpdateWorkDirectory work)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";
        var info = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = work.Directory,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", work.ScriptPath, "-PlanPath", work.PlanPath })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new UpdateException("无法启动更新进程。");
        return process.Id;
    }

    public static void WriteHealthMarker(string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker)) return;
        try
        {
            var full = Path.GetFullPath(marker);
            var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AllAgent.NET", "updates"));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var payload = JsonSerializer.Serialize(new
            {
                version = UpdateVersion.Current(),
                pid = Environment.ProcessId,
                startedAt = DateTimeOffset.UtcNow,
            });
            File.WriteAllText(full, payload, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public static UpdateExecutionResult? ReadResult(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<UpdateExecutionResult>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsUnderProgramFiles(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
        var builder = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"')
            {
                builder.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            builder.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return builder.Append('\\', slashes * 2).Append('"').ToString();
    }
}

public static class UpdateRuntimeContext
{
    public static string[] RestartArguments { get; set; } = [];
    public static string WorkingDirectory { get; set; } = Environment.CurrentDirectory;
    public static bool GuiLauncher { get; set; }
    public static bool WebUiHost { get; set; }

    public static void Configure(string[] raw, bool guiLauncher)
    {
        RestartArguments = raw;
        WorkingDirectory = Environment.CurrentDirectory;
        GuiLauncher = guiLauncher;
    }
}
