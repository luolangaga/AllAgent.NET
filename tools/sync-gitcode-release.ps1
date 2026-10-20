# Creates or updates a GitCode release and uploads the same assets as the GitHub
# release. The API is /api/v5 and authenticates with PRIVATE-TOKEN.
#
#   $env:GITCODE_TOKEN = '...'
#   tools\sync-gitcode-release.ps1 -Tag v1.0.7 -Name 'AllAgent.NET 1.0.7' `
#       -BodyFile release-notes.md -Assets (Get-ChildItem artifacts -File).FullName
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Name,
    [string]$BodyFile,
    [string[]]$Assets = @(),
    [string]$Repository = $(if ($env:GITCODE_REPOSITORY) { $env:GITCODE_REPOSITORY } else { 'luolangaga/AllAgent.NET' }),
    [string]$Token = $env:GITCODE_TOKEN,
    [string]$ApiBase = 'https://api.gitcode.com/api/v5',
    [string]$TargetCommitish = $(if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { 'main' }),
    [switch]$Prerelease,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if (!$Token) { throw 'GITCODE_TOKEN (or -Token) is required.' }
if (!$Name) { $Name = "AllAgent.NET $Tag" }
if (!$BodyFile) { $BodyFile = $null }

$headers = @{
    'PRIVATE-TOKEN' = $Token
    'Accept'        = 'application/json'
}
$repoPath = $Repository.Trim().Trim('/')
$releaseBase = "$ApiBase/repos/$repoPath/releases"

function Invoke-GitCodeApi {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [object]$Body
    )
    $arguments = @{
        Method  = $Method
        Uri     = $Uri
        Headers = $headers
    }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json; charset=utf-8'
        $arguments.Body = ($Body | ConvertTo-Json -Depth 8 -Compress)
    }
    return Invoke-RestMethod @arguments
}

function Get-ReleaseByTag {
    try {
        return Invoke-GitCodeApi -Method Get -Uri "$releaseBase/tags/$([uri]::EscapeDataString($Tag))"
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        if ($status -eq 404) { return $null }
        throw
    }
}

$body = ''
if ($BodyFile -and (Test-Path -LiteralPath $BodyFile)) {
    $body = Get-Content -LiteralPath $BodyFile -Raw -Encoding UTF8
}

$release = Get-ReleaseByTag
if ($DryRun) {
    Write-Host "[dry-run] Release $Tag $(if ($release) { 'exists; would patch' } else { 'missing; would create' })"
    foreach ($asset in $Assets) { Write-Host "[dry-run] Would upload $(Split-Path -Leaf $asset)" }
    return
}

if ($release) {
    Write-Host "Updating GitCode release $Tag"
    $payload = @{
        tag_name = $Tag
        name     = $Name
        body     = $body
    }
    $release = Invoke-GitCodeApi -Method Patch -Uri "$releaseBase/$([uri]::EscapeDataString($Tag))" -Body $payload
} else {
    Write-Host "Creating GitCode release $Tag"
    $payload = @{
        tag_name         = $Tag
        name             = $Name
        body             = $body
        target_commitish = $TargetCommitish
        release_status   = $(if ($Prerelease) { 'pre' } else { 'latest' })
    }
    $release = Invoke-GitCodeApi -Method Post -Uri $releaseBase -Body $payload
}

$existing = @{}
if ($release.assets) {
    foreach ($asset in $release.assets) { $existing[[string]$asset.name] = $true }
}

foreach ($file in $Assets) {
    if (!(Test-Path -LiteralPath $file)) { throw "Asset not found: $file" }
    $fileName = Split-Path -Leaf $file
    if ($existing.ContainsKey($fileName)) {
        Write-Host "Asset already present: $fileName"
        continue
    }
    Write-Host "Uploading $fileName"
    $uploadUri = "$releaseBase/$([uri]::EscapeDataString($Tag))/upload_url?file_name=$([uri]::EscapeDataString($fileName))"
    try {
        $upload = Invoke-GitCodeApi -Method Get -Uri $uploadUri
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        if ($status -ne 401 -and $status -ne 403) { throw }
        # Some GitCode deployments prefer a Bearer header on the upload-url route.
        # Keep the token out of the URL so it cannot leak into CI logs.
        $upload = Invoke-RestMethod -Method Get -Uri $uploadUri -Headers @{
            'Authorization' = "Bearer $Token"
            'Accept'        = 'application/json'
        }
    }
    if (!$upload.url) { throw "GitCode did not return an upload URL for $fileName." }

    $uploadHeaders = @{}
    if ($upload.headers) {
        $upload.headers.PSObject.Properties | ForEach-Object { $uploadHeaders[$_.Name] = [string]$_.Value }
    }
    $contentType = if ($uploadHeaders.ContainsKey('Content-Type')) { $uploadHeaders['Content-Type'] } else { 'application/octet-stream' }
    $uploadHeaders.Remove('Content-Type')
    Invoke-WebRequest -Method Put -Uri $upload.url -Headers $uploadHeaders -InFile $file -ContentType $contentType -UseBasicParsing | Out-Null
}
Write-Host "GitCode release $Tag is up to date."
