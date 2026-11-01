@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo FEHLER: .NET 10 SDK nicht gefunden.
  pause
  exit /b 1
)
echo Erstelle LAN_Party_Client.exe ...
dotnet publish "LanLauncher.Client.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "bin\Release\client-publish" 
if errorlevel 1 (
  echo.
  echo FEHLER: Client-EXE konnte nicht erstellt werden.
  pause
  exit /b 1
)
if exist "bin\Release\client-publish\LanLauncher.Client.exe" (
  copy /Y "bin\Release\client-publish\LanLauncher.Client.exe" "LAN_Party_Client.exe" >nul
)
if not exist "LAN_Party_Client.exe" (
  echo FEHLER: LAN_Party_Client.exe wurde nicht gefunden.
  pause
  exit /b 1
)
echo.
echo Fertig: "%CD%\LAN_Party_Client.exe"
echo Falls Smart App Control die EXE blockiert, Client per dotnet run starten.
pause
