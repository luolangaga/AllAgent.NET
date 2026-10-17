param([Parameter(ValueFromRemainingArguments = $true)][string[]]$AgentArguments)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --configuration Release --project src/AllAgent.NET.Terminal/AllAgent.NET.Terminal.csproj -- @AgentArguments
    exit $LASTEXITCODE
} finally { Pop-Location }
