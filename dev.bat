@echo off
echo Starting AllAgent.NET development environment...
echo.

:: Start Vite dev server in a new window
start "AllAgent.NET - Vite Dev Server" cmd /k "cd /d %~dp0src\AllAgent.NET.Web && npm run dev"

:: Wait for Vite to start
echo Waiting for Vite to start (4 seconds)...
timeout /t 4 /nobreak > nul

:: Start the native GUI window (Vite dev server, hot reload)
echo Starting the AllAgent.NET window (close it to stop)...
cd /d %~dp0
dotnet run --project src/AllAgent.NET.Terminal -- gui --gui-url http://127.0.0.1:5173/
