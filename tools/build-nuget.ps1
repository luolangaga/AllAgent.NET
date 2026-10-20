# Builds the .NET tool package for one Windows architecture.
#
#   tools\build-nuget.ps1 -Version 1.0.7 -Runtime win-x64
#   tools\build-nuget.ps1 -Version 1.0.7 -Runtime win-arm64
#
# Two package IDs keep the `allagent` command identical while letting NuGet
# resolve the matching worker apphosts and native assets:
#   win-x64   -> AllAgent.NET.Terminal
#   win-arm64 -> AllAgent.NET.Terminal.arm64
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [switch]$SkipWebBuild,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project = Join-Path $root 'src\AllAgent.NET.Terminal\AllAgent.NET.Terminal.csproj'
$powerShell = Join-Path $root 'src\AllAgent.NET.PowerShell\AllAgent.NET.PowerShell.csproj'
$web = Join-Path $root 'src\AllAgent.NET.Web'

if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\packages' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

if (!$Version) {
    [xml]$projectXml = Get-Content $project
    $Version = $projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (!$Version) { throw "No <Version> in $project; pass -Version instead." }
}
if ($Version -notmatch '^\d+(\.\d+){1,3}(-[0-9A-Za-z.-]+)?$') {
    throw "Version must look like 1.0.7 or 1.0.7-beta.1 (got '$Version')."
}

$packageId = if ($Runtime -eq 'win-arm64') { 'AllAgent.NET.Terminal.arm64' } else { 'AllAgent.NET.Terminal' }

function Invoke-Step([string]$Title, [scriptblock]$Action) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "$Title failed with exit code $LASTEXITCODE." }
}

if (!$SkipWebBuild) {
    if (!(Test-Path (Join-Path $web 'node_modules'))) {
        Invoke-Step "npm ci ($web)" { npm --prefix $web ci }
    }
    Invoke-Step "Build web frontend" { npm --prefix $web run build }
}

if (!$SkipRestore) {
    Invoke-Step "Restore terminal ($Runtime)" { dotnet restore $project -r $Runtime }
    Invoke-Step "Restore embedded PowerShell worker ($Runtime)" { dotnet restore $powerShell -r $Runtime }
}

# The embedded PowerShell worker is collected by path, not through a project
# reference that copies its output. Build it for the target RID before packing.
Invoke-Step "Build embedded PowerShell worker ($Runtime)" {
    dotnet build $powerShell -c $Configuration -r $Runtime --no-restore -v:m
}

$existing = Get-ChildItem $OutputDirectory -Filter "$packageId.$Version.nupkg" -ErrorAction SilentlyContinue
if ($existing) { $existing | Remove-Item -Force }

Invoke-Step "Pack $packageId $Version ($Runtime)" {
    dotnet pack $project -c $Configuration -o $OutputDirectory `
        -p:Version=$Version `
        -p:PackageId=$packageId `
        -p:RuntimeIdentifier=$Runtime `
        -p:SelfContained=false `
        -p:SkipWebBuild=true `
        --no-restore
}

$package = Join-Path $OutputDirectory "$packageId.$Version.nupkg"
if (!(Test-Path $package)) { throw "NuGet package was not produced: $package" }

# A tool package must keep the managed entry point under tools/<tfm>/any and must
# contain the worker apphosts for the selected RID. Fail here instead of shipping
# an ARM64 package whose workers are still x64.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $names = $archive.Entries | ForEach-Object { $_.FullName }
    $toolRoot = if ($names -contains 'tools/net10.0/any/allagent.dll') { 'tools/net10.0/any' }
        elseif ($names -contains 'tools/net10.0/win-x64/allagent.dll') { 'tools/net10.0/win-x64' }
        elseif ($names -contains 'tools/net10.0/win-arm64/allagent.dll') { 'tools/net10.0/win-arm64' }
        else { throw "Package is missing the tool entry point (tools/net10.0/<rid>/allagent.dll)." }
    $requiredWorkers = @(
        "$toolRoot/AllAgent.NET.Sandbox.exe",
        "$toolRoot/AllAgent.NET.PowerShell.exe",
        "$toolRoot/AllAgent.NET.Browser.exe",
        "$toolRoot/AllAgent.NET.ComputerUse.exe"
    )
    foreach ($worker in $requiredWorkers) {
        if ($names -notcontains $worker) { throw "Package is missing $worker." }
    }
    if ($names -notcontains "$toolRoot/web/index.html") { throw "Package is missing the Web UI assets." }
} finally {
    $archive.Dispose()
}

$hash = (Get-FileHash $package -Algorithm SHA256).Hash
"$hash *$(Split-Path -Leaf $package)" | Set-Content (Join-Path $OutputDirectory "SHA256SUMS.$Runtime.packages.txt") -Encoding ascii
Write-Host ""
Write-Host ("Package : {0}" -f $package)
Write-Host ("Tool ID : {0}" -f $packageId)
Write-Host ("Size    : {0:N1} MB" -f ((Get-Item $package).Length / 1MB))
Write-Host ("SHA256  : {0}" -f $hash)
