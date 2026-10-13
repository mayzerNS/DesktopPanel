@echo off
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo .NET 8 SDK not found: https://dotnet.microsoft.com/download/dotnet/8.0 & pause & exit /b 1)
dotnet publish src\DesktopPanel\DesktopPanel.csproj -c Release -o dist || (pause & exit /b 1)
echo dist\DesktopPanel.exe
pause
