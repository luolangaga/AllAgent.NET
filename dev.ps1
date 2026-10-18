# Start the Vite dev server and the native GUI window together.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$viteJob = Start-Job -ScriptBlock {
    param($web)
    Set-Location $web
    npm run dev
} -ArgumentList (Join-Path $root 'src\AllAgent.NET.Web')

Write-Host "Waiting for Vite to start..." -ForegroundColor Cyan
Start-Sleep -Seconds 4

# Check Vite is up
try {
    $null = Invoke-WebRequest -Uri "http://localhost:5173" -TimeoutSec 5 -UseBasicParsing
    Write-Host "Vite ready at http://localhost:5173" -ForegroundColor Green
} catch {
    Write-Host "Vite may still be starting..." -ForegroundColor Yellow
}

Write-Host "Starting the AllAgent.NET window (close it to stop)..." -ForegroundColor Cyan
Set-Location $root
dotnet run --project src/AllAgent.NET.Terminal -- gui --gui-url http://127.0.0.1:5173/

# Cleanup
Stop-Job $viteJob
Remove-Job $viteJob
