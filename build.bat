@echo off
rem ---------------------------------------------------------------------------
rem  Clean build + run Launchpad.
rem    build.bat            stop running copy, clean, publish to .\dist, start it
rem    build.bat test       same, but run the unit tests first (aborts if they fail)
rem    build.bat nolaunch   build only, don't start the app
rem  Needs the .NET 8 SDK. Arguments can be combined, e.g.  build.bat test nolaunch
rem ---------------------------------------------------------------------------
setlocal EnableExtensions
cd /d "%~dp0"

set RUN_TESTS=0
set LAUNCH=1
for %%A in (%*) do (
    if /I "%%A"=="test" set RUN_TESTS=1
    if /I "%%A"=="nolaunch" set LAUNCH=0
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [error] The .NET SDK was not found. Install the .NET 8 SDK from https://dot.net and try again.
    pause
    exit /b 1
)

echo.
echo === 1/4  Stopping any running Launchpad ===
taskkill /F /IM Launchpad.exe >nul 2>&1
timeout /t 1 /nobreak >nul

echo.
echo === 2/4  Cleaning ===
dotnet clean Launchpad.sln -v q --nologo >nul 2>&1
for %%D in (dist dist-test src\Launchpad.Core\bin src\Launchpad.Core\obj src\Launchpad.App\bin src\Launchpad.App\obj) do (
    if exist "%%D" rmdir /s /q "%%D"
)
echo Done.

if "%RUN_TESTS%"=="1" (
    echo.
    echo === Running tests ===
    dotnet test tests\Launchpad.Tests --nologo -v q
    if errorlevel 1 (
        echo [error] Tests failed - not building.
        pause
        exit /b 1
    )
)

echo.
echo === 3/4  Building (Release, ReadyToRun) ===
dotnet publish src\Launchpad.App -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:DebugType=none -o dist --nologo -v q
if errorlevel 1 (
    echo.
    echo [error] Build failed - see the messages above.
    pause
    exit /b 1
)

echo.
echo === 4/4  Done: %~dp0dist\Launchpad.exe ===
if "%LAUNCH%"=="1" (
    start "" "%~dp0dist\Launchpad.exe"
    echo Launchpad started - press Ctrl+Alt+L, or click its tray icon.
)
endlocal
