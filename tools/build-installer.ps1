# Builds the AllAgent.NET Windows installer: publishes the self-contained
# win-x64 terminal host and compiles installer\AllAgent.NET.iss with Inno Setup.
#
#   tools\build-installer.ps1 -Version 1.0.7
#
# Requires: .NET 10 SDK, Node.js + npm, Inno Setup 6 (ISCC.exe).
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory,
    [string]$IsccPath,
    [switch]$SkipWebBuild,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project = Join-Path $root 'src\AllAgent.NET.Terminal\AllAgent.NET.Terminal.csproj'
$web = Join-Path $root 'src\AllAgent.NET.Web'
$script = Join-Path $root 'installer\AllAgent.NET.iss'

if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\installer' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$payload = Join-Path $OutputDirectory 'payload'

if (!$Version) {
    [xml]$projectXml = Get-Content $project
    $Version = $projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (!$Version) { throw "No <Version> in $project; pass -Version instead." }
}
# The installer stamps this into the exe version resource, which only takes numbers.
if ($Version -notmatch '^\d+(\.\d+){1,3}$') { throw "Version must be numeric like 1.0.7 (got '$Version')." }

function Invoke-Step([string]$Title, [scriptblock]$Action) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "$Title failed with exit code $LASTEXITCODE." }
}

if (!$SkipPublish) {
    # Restoring with the runtime identifier is what makes the per-RID worker publishes
    # resolvable; any plain build in between overwrites the assets file without them.
    Invoke-Step "Restore ($Runtime)" { dotnet restore (Join-Path $root 'src\AllAgent.NET.Terminal') -r $Runtime }
    # The embedded PowerShell worker is collected by path and is not a project reference.
    Invoke-Step "Restore embedded PowerShell worker ($Runtime)" { dotnet restore (Join-Path $root 'src\AllAgent.NET.PowerShell') -r $Runtime }
    Invoke-Step "Build embedded PowerShell worker" {
        dotnet build (Join-Path $root 'src\AllAgent.NET.PowerShell') -c $Configuration -r $Runtime --no-restore -v:m
    }
    if (!$SkipWebBuild -and !(Test-Path (Join-Path $web 'node_modules'))) {
        Invoke-Step "npm ci ($web)" { npm --prefix $web ci }
    }
    $publishArguments = @('publish', $project, '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true',
        '--no-restore', '-o', $payload, "-p:Version=$Version")
    if ($SkipWebBuild) { $publishArguments += '-p:SkipWebBuild=true' }
    # A fresh directory keeps files from an older layout out of the payload.
    if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
    Invoke-Step "Publish terminal host ($Version)" { dotnet @publishArguments }
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    # Debug symbols and compile-time reference assemblies are not part of a
    # release payload; they would add ~90 MB to the installer for nothing.
    $symbols = Get-ChildItem $payload -Recurse -File -Filter *.pdb
    $symbolBytes = ($symbols | Measure-Object Length -Sum).Sum
    $symbols | Remove-Item -Force
    $references = Get-ChildItem $payload -Recurse -Directory -Filter ref
    $references | Remove-Item -Recurse -Force
    Write-Host ("    payload trimmed: {0:N1} MB of symbols and {1} reference folders removed" -f ($symbolBytes / 1MB), $references.Count)
    if (!(Test-Path (Join-Path $payload 'allagent.exe'))) { throw "Publish output is missing allagent.exe." }
    # Every worker lives in the same directory as the host so one self-contained runtime is shared.
    foreach ($required in @('web\index.html', 'AllAgent.NET.Gui.exe', 'AllAgent.NET.Sandbox.exe', 'AllAgent.NET.PowerShell.exe', 'AllAgent.NET.Browser.exe', 'AllAgent.NET.ComputerUse.exe', 'builtin-plugins\AllAgent.NET.CommandCode\allagent-plugin.json')) {
        if (!(Test-Path (Join-Path $payload $required))) { throw "Publish output is missing $required." }
    }
}

if (!$IsccPath) {
    $candidatePaths = @()
    if (${env:ProgramFiles(x86)}) { $candidatePaths += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
    if ($env:ProgramFiles) { $candidatePaths += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe' }
    $IsccPath = @($candidatePaths | Where-Object { Test-Path $_ })[0]
    if (!$IsccPath -and (Get-Command ISCC.exe -ErrorAction SilentlyContinue)) { $IsccPath = (Get-Command ISCC.exe).Source }
    if (!$IsccPath) { throw "Inno Setup 6 (ISCC.exe) not found; install it or pass -IsccPath." }
}

Invoke-Step "Compile installer (Inno Setup)" {
    & $IsccPath "/DAppVersion=$Version" "/DPayloadDir=$payload" "/DOutputDir=$OutputDirectory" $script
}

$setup = Join-Path $OutputDirectory "AllAgent.NET-$Version-$Runtime-setup.exe"
if (!(Test-Path $setup)) { throw "Installer was not produced: $setup" }
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
"$hash *$(Split-Path -Leaf $setup)" | Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
""
Write-Host ("Installer : {0}" -f $setup)
Write-Host ("Size      : {0:N1} MB" -f ((Get-Item $setup).Length / 1MB))
Write-Host ("SHA256    : {0}" -f $hash)
