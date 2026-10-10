@echo off
rem ---------------------------------------------------------------------------
rem  Publish the current build to GitHub: tests -> release zip -> GitHub Release.
rem    publish-release.bat            run the tests, build the zip, upload it to the GitHub Release
rem    publish-release.bat notest     same, without running the tests
rem    publish-release.bat nopause    don't wait for a key at the end (for scripts / agents)
rem  The zip replaces the file already attached to release v<VERSION> (VERSION is read from build-release.bat),
rem  so GitHub always holds exactly one, current build and the git history never grows.
rem  The first run opens a browser window to sign in to GitHub (one time only).
rem ---------------------------------------------------------------------------
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "REPO=mhzeGit/EasyLaunchpad"
set "RUN_TESTS=1"
set "PAUSE_AT_END=1"
for %%A in (%*) do (
    if /I "%%A"=="notest" set "RUN_TESTS=0"
    if /I "%%A"=="nopause" set "PAUSE_AT_END=0"
)

set "EXITCODE=0"
call :main
set "EXITCODE=%errorlevel%"
if "%PAUSE_AT_END%"=="1" (
    echo.
    pause
)
exit /b %EXITCODE%

:main
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [error] The .NET 8 SDK was not found. Install it from https://dot.net and try again.
    exit /b 1
)

rem --- GitHub CLI -------------------------------------------------------------
set "GH="
for /f "delims=" %%G in ('where gh 2^>nul') do if not defined GH set "GH=%%G"
if not defined GH if exist "%ProgramFiles%\GitHub CLI\gh.exe" set "GH=%ProgramFiles%\GitHub CLI\gh.exe"
if not defined GH (
    echo [error] The GitHub CLI was not found. Install it with:  winget install --id GitHub.cli
    exit /b 1
)

call "%GH%" auth status >nul 2>&1
if errorlevel 1 (
    echo.
    echo === Signing in to GitHub - a browser window opens, this is only needed once ===
    call "%GH%" auth login --hostname github.com --git-protocol https --web
    if errorlevel 1 (
        echo [error] Could not sign in to GitHub.
        exit /b 1
    )
)

rem --- version, read from the one place it is defined ---------------------------
set "VERSION="
for /f "tokens=2 delims==" %%V in ('findstr /r /c:"^set .VERSION=" build-release.bat') do (
    set "RAW=%%V"
    set "VERSION=!RAW:"=!"
)
if not defined VERSION (
    echo [error] Could not read VERSION from build-release.bat.
    exit /b 1
)
set "ZIP=%~dp0release\EasyLaunchpad-%VERSION%-win-x64.zip"

rem --- tests ------------------------------------------------------------------
if "%RUN_TESTS%"=="1" (
    echo.
    echo === 1/3  Running tests ===
    dotnet test tests\Launchpad.Tests --nologo -v q
    if errorlevel 1 (
        echo [error] Tests failed - nothing was published.
        exit /b 1
    )
) else (
    echo.
    echo === 1/3  Tests skipped ===
)

rem --- build ------------------------------------------------------------------
echo.
echo === 2/3  Building the release zip ===
call "%~dp0build-release.bat"
if errorlevel 1 (
    echo [error] Release build failed - nothing was published.
    exit /b 1
)
if not exist "%ZIP%" (
    echo [error] Expected "%ZIP%" was not produced.
    exit /b 1
)

set "DIRTY="
for /f "delims=" %%S in ('git status --porcelain 2^>nul') do set "DIRTY=1"
if defined DIRTY echo [note] This build includes uncommitted changes from the working tree.

rem --- publish ----------------------------------------------------------------
echo.
echo === 3/3  Uploading to GitHub release v%VERSION% ===
call "%GH%" release view v%VERSION% --repo %REPO% >nul 2>&1
if errorlevel 1 (
    call "%GH%" release create v%VERSION% "%ZIP%" --repo %REPO% --target main --title "EasyLaunchpad %VERSION%" --notes "Portable Windows x64 build of EasyLaunchpad %VERSION%. Unzip it and run EasyLaunchpad.exe."
) else (
    call "%GH%" release upload v%VERSION% "%ZIP%" --repo %REPO% --clobber
)
if errorlevel 1 (
    echo [error] Upload to GitHub failed.
    exit /b 1
)

echo.
echo Published: https://github.com/%REPO%/releases/tag/v%VERSION%
exit /b 0
