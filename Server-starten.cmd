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
