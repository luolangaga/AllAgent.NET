@echo off
echo Starting AllAgent.NET development environment...
echo.

:: Start Vite dev server in a new window
start "AllAgent.NET - Vite Dev Server" cmd /k "cd /d C:\Users\luolan\AllAgent.NET\src\AllAgent.NET.Web && npm run dev"

:: Wait for Vite to start
echo Waiting for Vite to start (4 seconds)...
timeout /t 4 /nobreak > nul

:: Start the Avalonia app
echo Starting desktop app...
cd /d C:\Users\luolan\AllAgent.NET
dotnet run --project src/AllAgent.NET.Desktop/AllAgent.NET.Desktop.csproj -p:SkipWebBuild=true
