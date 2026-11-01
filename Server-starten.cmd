@echo off
setlocal
cd /d "%~dp0"
if not exist "src\LanLauncher.Server\LanLauncher.Server.csproj" (
  echo FEHLER: Serverprojekt nicht gefunden.
  pause
  exit /b 1
)
where dotnet >nul 2>nul
if errorlevel 1 (
  echo FEHLER: .NET SDK nicht gefunden. Bitte .NET 10 SDK installieren.
  pause
  exit /b 1
)
powershell -NoProfile -Command "try { $r=Invoke-WebRequest -Uri 'http://127.0.0.1:8080/api/health' -TimeoutSec 2 -UseBasicParsing; if ($r.StatusCode -eq 200) { exit 0 } } catch {} ; exit 1" >nul 2>nul
if not errorlevel 1 (
  echo LAN Launcher Server laeuft bereits auf Port 8080.
  echo Es wird kein zweiter Server gestartet.
  pause
  exit /b 0
)
if not defined LANLAUNCHER_AdminKey (
  echo Bitte Admin-Schluessel eingeben.
  echo Der Schluessel wird nicht im Repository gespeichert.
  set /p "LANLAUNCHER_AdminKey=Admin-Schluessel: "
)
if not defined LANLAUNCHER_AdminKey (
  echo FEHLER: Kein Admin-Schluessel eingegeben.
  pause
  exit /b 1
)
echo.
echo Starte LAN Launcher Server auf Port 8080 ueber dotnet run ...
echo Server beenden mit Strg+C.
dotnet run --project "src\LanLauncher.Server\LanLauncher.Server.csproj"
echo.
echo Server wurde beendet.
pause
