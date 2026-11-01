@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo FEHLER: .NET 10 SDK nicht gefunden.
  pause
  exit /b 1
)
echo.
echo Erstelle LAN Party Client als WPF-kompatible Mehrdateien-Version ...
echo.
set "OUTPUT=%CD%\LAN_Party_Client"
if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"
dotnet publish "LanLauncher.Client.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -o "%OUTPUT%"
if errorlevel 1 (
  echo.
  echo FEHLER: Client konnte nicht erstellt werden.
  pause
  exit /b 1
)
if not exist "%OUTPUT%\LanLauncher.Client.exe" (
  echo FEHLER: Client-EXE wurde nicht gefunden.
  pause
  exit /b 1
)
ren "%OUTPUT%\LanLauncher.Client.exe" "LAN_Party_Client.exe"
if exist "%CD%\LAN_Party_Client.exe" del /q "%CD%\LAN_Party_Client.exe"
echo.
echo Fertig: "%OUTPUT%\LAN_Party_Client.exe"
echo.
echo Erstelle Desktopverknuepfung ...
set "SHORTCUT_TARGET=%OUTPUT%\LAN_Party_Client.exe"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $desktop=[Environment]::GetFolderPath('DesktopDirectory'); $link=Join-Path $desktop 'LAN Party Client.lnk'; $shell=New-Object -ComObject WScript.Shell; $shortcut=$shell.CreateShortcut($link); $shortcut.TargetPath=$env:SHORTCUT_TARGET; $shortcut.WorkingDirectory=$env:OUTPUT; $shortcut.IconLocation=$env:SHORTCUT_TARGET+',0'; $shortcut.Description='LAN Party Client starten'; $shortcut.Save(); Write-Host ('Desktopverknuepfung erstellt: '+$link)"
if errorlevel 1 (
  echo WARNUNG: Desktopverknuepfung konnte nicht erstellt werden.
  echo Die EXE wurde trotzdem erfolgreich erstellt.
) else (
  echo Desktopverknuepfung ist bereit.
)
echo WICHTIG: Die EXE muss zusammen mit den Dateien in diesem Ordner bleiben.
echo Die alte EXE im Projektordner wurde entfernt, um Fehlstarts zu vermeiden.
pause
