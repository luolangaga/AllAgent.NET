# Start Vite dev server and Avalonia app together
$viteJob = Start-Job -ScriptBlock {
    Set-Location "C:\Users\luolan\AllAgent.NET\src\AllAgent.NET.Web"
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

Write-Host "Starting AllAgent.NET desktop app..." -ForegroundColor Cyan
Set-Location "C:\Users\luolan\AllAgent.NET"
dotnet run --project src/AllAgent.NET.Desktop/AllAgent.NET.Desktop.csproj -p:SkipWebBuild=true

# Cleanup
Stop-Job $viteJob
Remove-Job $viteJob
