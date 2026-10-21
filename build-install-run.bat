@echo off
rem ---------------------------------------------------------------------------
rem  Build Launchpad from the current source, install it for this user and start it.
rem  Installs to %LOCALAPPDATA%\Programs\EasyLaunchpad (self-contained win-x64, so
rem  it does not need the .NET SDK to run) and adds a Start Menu shortcut.
rem  Settings, icons and the app catalog are not touched: they stay in
rem  %LOCALAPPDATA%\Launchpad.
rem ---------------------------------------------------------------------------
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "APP=EasyLaunchpad"
set "EXE=Launchpad"
set "RID=win-x64"
set "INSTALL=%LOCALAPPDATA%\Programs\%APP%"
set "STAGE=%TEMP%\%APP%-build\%RID%"
set "SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Launchpad.lnk"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [error] The .NET SDK was not found. Install the .NET 8 SDK from https://dot.net and try again.
    goto :fail
)

rem --- version, read from the one place it is defined -------------------------
set "VERSION="
for /f "tokens=2 delims==" %%V in ('findstr /r /c:"^set .VERSION=" build-release.bat') do (
    set "RAW=%%V"
    set "VERSION=!RAW:"=!"
)
if not defined VERSION set "VERSION=unknown"

rem --- stop the running copy, it locks its own files ---------------------------
echo === 1/4  Stopping the running copy ===
taskkill /F /IM %EXE%.exe >nul 2>&1
for /l %%I in (1,1,20) do (
    tasklist /FI "IMAGENAME eq %EXE%.exe" 2>nul | find /I "%EXE%.exe" >nul
    if errorlevel 1 goto :stopped
    ping -n 2 127.0.0.1 >nul
)
echo [error] Launchpad is still running and could not be stopped.
goto :fail
:stopped

rem --- build -------------------------------------------------------------------
echo.
echo === 2/4  Building %APP% %VERSION% ^(%RID%, self-contained^) ===
if exist "%STAGE%" rmdir /s /q "%STAGE%"
dotnet publish src\Launchpad.App\Launchpad.App.csproj -c Release -r %RID% --self-contained true ^
    -p:PublishReadyToRun=true -p:DebugType=none -p:Version=%VERSION% -p:Product=%APP% ^
    -o "%STAGE%" --nologo -v minimal
if errorlevel 1 (
    echo [error] Build failed - the installed copy was left untouched.
    goto :fail
)
if not exist "%STAGE%\%EXE%.exe" (
    echo [error] Expected "%STAGE%\%EXE%.exe" was not produced.
    goto :fail
)

rem --- install -----------------------------------------------------------------
echo.
echo === 3/4  Installing to %INSTALL% ===
if not exist "%INSTALL%" mkdir "%INSTALL%"
robocopy "%STAGE%" "%INSTALL%" /MIR /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
    echo [error] Could not copy the build to "%INSTALL%".
    goto :fail
)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('%SHORTCUT%');" ^
    "$s.TargetPath = '%INSTALL%\%EXE%.exe';" ^
    "$s.WorkingDirectory = '%INSTALL%';" ^
    "$s.IconLocation = '%INSTALL%\%EXE%.exe,0';" ^
    "$s.Description = 'A macOS-style app launcher for Windows';" ^
    "$s.Save()"
if errorlevel 1 echo [warn] Could not create the Start Menu shortcut.

rem --- run ---------------------------------------------------------------------
echo.
echo === 4/4  Starting Launchpad ===
start "" "%INSTALL%\%EXE%.exe"
ping -n 4 127.0.0.1 >nul
tasklist /FI "IMAGENAME eq %EXE%.exe" 2>nul | find /I "%EXE%.exe" >nul
if errorlevel 1 (
    echo [error] Launchpad did not stay running. Start "%INSTALL%\%EXE%.exe" to see the error.
    goto :fail
)

if exist "%STAGE%" rmdir /s /q "%STAGE%"

echo.
echo Installed Launchpad %VERSION% to "%INSTALL%" and started it.
echo Press Ctrl+Alt+L, or click its tray icon. It is also in the Start Menu.
exit /b 0

:fail
echo.
pause
exit /b 1
