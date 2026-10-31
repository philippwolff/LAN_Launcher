@echo off
setlocal
cd /d "%~dp0"
if not exist "Server\LanLauncher.Server.exe" (
  echo Server-EXE nicht vorhanden.
  echo Bitte zuerst Server-EXE-erstellen.cmd ausfuehren.
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
echo Starte LAN Launcher Server auf Port 8080 ...
echo Server beenden mit Strg+C.
"Server\LanLauncher.Server.exe"
echo.
echo Server wurde beendet.
pause
