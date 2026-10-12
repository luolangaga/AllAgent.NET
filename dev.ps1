# Start Vite dev server and Avalonia app together
$viteJob = Start-Job -ScriptBlock {
    Set-Location "C:\Users\luolan\LanAgent\src\LanAgent.Web"
    npm run dev
}

Write-Host "Waiting for Vite to start..." -ForegroundColor Cyan
Start-Sleep -Seconds 4

# Check Vite is up
try {
    $null = Invoke-WebRequest -Uri "http://localhost:5173" -TimeoutSec 5 -UseBasicParsing
    Write-Host "Vite ready at http://localhost:5173" -ForegroundColor Green
} catch {
    Write-Host "Vite may still be starting..." -ForegroundColor Yellow
}

Write-Host "Starting LanAgent desktop app..." -ForegroundColor Cyan
Set-Location "C:\Users\luolan\LanAgent"
dotnet run --project src/LanAgent.Desktop/LanAgent.Desktop.csproj -p:SkipWebBuild=true

# Cleanup
Stop-Job $viteJob
Remove-Job $viteJob
