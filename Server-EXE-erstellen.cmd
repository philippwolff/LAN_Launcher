@echo off
setlocal
cd /d "%~dp0"
echo ======================================
echo LAN Launcher - Server EXE erstellen
echo ======================================
where dotnet >nul 2>nul
if errorlevel 1 (
  echo FEHLER: .NET SDK nicht gefunden.
  echo Bitte .NET 10 SDK installieren.
  pause
  exit /b 1
)
dotnet publish "src\LanLauncher.Server\LanLauncher.Server.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "Server"
if errorlevel 1 (
  echo.
  echo FEHLER: Server konnte nicht erstellt werden.
  pause
  exit /b 1
)
echo.
echo Server erstellt: "%~dp0Server\LanLauncher.Server.exe"
echo Starte ihn mit "Server-starten.cmd".
pause
