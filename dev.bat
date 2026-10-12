@echo off
echo Starting LanAgent development environment...
echo.

:: Start Vite dev server in a new window
start "LanAgent - Vite Dev Server" cmd /k "cd /d C:\Users\luolan\LanAgent\src\LanAgent.Web && npm run dev"

:: Wait for Vite to start
echo Waiting for Vite to start (4 seconds)...
timeout /t 4 /nobreak > nul

:: Start the Avalonia app
echo Starting desktop app...
cd /d C:\Users\luolan\LanAgent
dotnet run --project src/LanAgent.Desktop/LanAgent.Desktop.csproj -p:SkipWebBuild=true
