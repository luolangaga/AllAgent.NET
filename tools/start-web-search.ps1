# Optional legacy open-websearch daemon. LanAgent uses built-in C# search by default.
param(
    [ValidateRange(1024, 65535)][int]$Port = 3210,
    [string]$Registry = 'https://registry.npmjs.org',
    [ValidateSet('duckduckgo', 'bing', 'baidu', 'brave', 'startpage', 'sogou', 'exa', 'hackernews')][string]$SearchEngine = 'duckduckgo'
)
$ErrorActionPreference = 'Stop'
$searchRoot = Join-Path $PSScriptRoot 'open-websearch'
$searchUrl = "http://127.0.0.1:$Port"
function Test-SearchReady {
    try {
        $status = Invoke-RestMethod "$searchUrl/status" -TimeoutSec 2
        return $status.status -eq 'ok' -and $status.data.runtime -eq 'ready' -and $status.data.activation -eq 'active' -and $status.data.capabilities -contains 'search'
    } catch { return $false }
}
if (Test-SearchReady) {
    Write-Host "可选搜索服务已就绪：$searchUrl"
    Write-Host "使用旧服务时，启动 LanAgent 前设置：`$env:LANAGENT_SEARCH_URL='$searchUrl'"
    return
}
$node = (Get-Command node -ErrorAction Stop).Source
$npm = (Get-Command npm.cmd -ErrorAction Stop).Source
$entry = Join-Path $searchRoot 'node_modules/open-websearch/build/index.js'
if (!(Test-Path -LiteralPath $entry)) {
    New-Item -ItemType Directory -Path $searchRoot -Force | Out-Null
    Write-Host '安装 open-websearch 2.2.0（仅首次需要）…'
    & $npm install --prefix $searchRoot --no-audit --no-fund --registry $Registry 'open-websearch@2.2.0'
    if ($LASTEXITCODE -ne 0) { throw '搜索组件安装失败，请检查网络或使用 -Registry 指定 npm 镜像。' }
}
$previousSearchEngine = [Environment]::GetEnvironmentVariable('DEFAULT_SEARCH_ENGINE')
try {
    $env:DEFAULT_SEARCH_ENGINE = $SearchEngine
    $process = Start-Process -FilePath $node -ArgumentList @(('"' + $entry + '"'), 'serve', '--port', $Port) -WindowStyle Hidden -PassThru -WorkingDirectory $searchRoot -RedirectStandardOutput (Join-Path $searchRoot 'daemon.stdout.log') -RedirectStandardError (Join-Path $searchRoot 'daemon.stderr.log')
} finally { [Environment]::SetEnvironmentVariable('DEFAULT_SEARCH_ENGINE', $previousSearchEngine) }
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if (Test-SearchReady) {
        Set-Content -LiteralPath (Join-Path $searchRoot 'daemon.pid') -Value $process.Id
        Write-Host "搜索已就绪：$searchUrl（进程 $($process.Id)）"
        Write-Host "使用旧服务时，启动 LanAgent 前设置：`$env:LANAGENT_SEARCH_URL='$searchUrl'"
        return
    }
    if ($process.HasExited) { throw "搜索服务启动失败。查看 $searchRoot/daemon.stderr.log" }
    Start-Sleep -Milliseconds 500
}
if (!$process.HasExited) { Stop-Process -Id $process.Id }
throw "搜索服务未就绪。查看 $searchRoot/daemon.stderr.log"
