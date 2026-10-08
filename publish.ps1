# Builds a ready-to-run copy of Launchpad into .\dist (framework-dependent: needs the .NET 8 Desktop Runtime).
# Add -SelfContained to bundle the runtime (bigger, no prerequisites).
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $sc = if ($SelfContained) { 'true' } else { 'false' }
    dotnet publish src/Launchpad.App -c Release -r win-x64 --self-contained $sc `
        -p:PublishReadyToRun=true -p:DebugType=none -o dist
    Write-Host "`nPublished to $PWD\dist\Launchpad.exe"
} finally { Pop-Location }
