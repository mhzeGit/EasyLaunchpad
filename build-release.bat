@echo off
rem Build a self-contained, framework-dependent-free x64 release bundle for EasyLaunchpad.
rem Output: .\release\EasyLaunchpad-1.0.0-win-x64\ and .\release\EasyLaunchpad-1.0.0-win-x64.zip
setlocal EnableExtensions
cd /d "%~dp0"

set "APP_NAME=EasyLaunchpad"
set "VERSION=1.0.0"
set "RID=win-x64"
set "RELEASE_ROOT=%~dp0release"
set "RELEASE_DIR=%RELEASE_ROOT%\%APP_NAME%-%VERSION%-%RID%"
set "ZIP_FILE=%RELEASE_ROOT%\%APP_NAME%-%VERSION%-%RID%.zip"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [error] .NET 8 SDK was not found. Install it from https://dot.net and try again.
    exit /b 1
)

echo.
echo === Building %APP_NAME% %VERSION% (%RID%, self-contained) ===
if exist "%RELEASE_DIR%" rmdir /s /q "%RELEASE_DIR%"
if exist "%ZIP_FILE%" del /q "%ZIP_FILE%"
if not exist "%RELEASE_ROOT%" mkdir "%RELEASE_ROOT%"

dotnet publish "src\Launchpad.App\Launchpad.App.csproj" -c Release -r %RID% --self-contained true ^
    -p:PublishReadyToRun=true -p:DebugType=none -p:Version=%VERSION% -p:Product=%APP_NAME% ^
    -o "%RELEASE_DIR%" --nologo -v minimal
if errorlevel 1 (
    echo [error] Publish failed. See the build output above.
    exit /b 1
)

if not exist "%RELEASE_DIR%\Launchpad.exe" (
    echo [error] Expected app executable was not produced.
    exit /b 1
)

rem Provide the branded executable name at the top level of the release bundle.
copy /y "%RELEASE_DIR%\Launchpad.exe" "%RELEASE_DIR%\EasyLaunchpad.exe" >nul

powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%RELEASE_DIR%\*' -DestinationPath '%ZIP_FILE%' -CompressionLevel Optimal"
if errorlevel 1 (
    echo [error] Could not create the release ZIP.
    exit /b 1
)

echo.
echo Release folder: "%RELEASE_DIR%"
echo Release ZIP:    "%ZIP_FILE%"
echo.
echo This creates a portable ZIP bundle, not a setup installer. Microsoft Store's EXE/MSI URL field requires an offline installer.
endlocal
