# Integration tests use tiny .NET Framework apps in temporary folders. They never touch an installed app.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web.Extensions
$root = Join-Path ([IO.Path]::GetTempPath()) ('updater-integration-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$source = @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
public class FIXTURETYPE {
    public static void Main(string[] args) {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        File.WriteAllText(Path.Combine(root, ".fixture-pid"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
        if (BROKEN) return;
        if (Array.IndexOf(args, "--update-complete") >= 0 && File.Exists(Path.Combine(root, ".easy-update-pending.json"))) {
            try {
                var pending = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(root, ".easy-update-pending.json")));
                File.WriteAllText(Path.Combine((string)pending["work"], "healthy"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
            } catch (FileNotFoundException) { }
        }
        System.Threading.Thread.Sleep(120000);
    }
}
'@
$works = @()
function Assert([bool]$condition, [string]$message) { if (!$condition) { throw $message } }
function StopFixture([string]$install) {
    $pidFile = Join-Path $install '.fixture-pid'
    if (Test-Path -LiteralPath $pidFile) {
        $fixture = Get-Process -Id ([int](Get-Content -LiteralPath $pidFile -Raw)) -ErrorAction SilentlyContinue
        if ($fixture -and $fixture.Path -eq (Join-Path $install 'Fixture.exe')) { $fixture.Kill(); $fixture.WaitForExit() }
    }
}
try {
    foreach ($kind in @('Old', 'New', 'Broken')) {
        $code = $source.Replace('FIXTURETYPE', ('Fixture' + $kind)).Replace('BROKEN', $(if ($kind -eq 'Broken') { 'true' } else { 'false' }))
        Add-Type -TypeDefinition $code -ReferencedAssemblies 'System.Web.Extensions' -OutputAssembly (Join-Path $root ($kind + '.exe')) -OutputType WindowsApplication -IgnoreWarnings
    }
    foreach ($scenario in @('success', 'rollback', 'locked', 'recovery', 'committed')) {
        $install = Join-Path $root $scenario
        [IO.Directory]::CreateDirectory($install) | Out-Null
        Copy-Item -LiteralPath (Join-Path $root 'Old.exe') -Destination (Join-Path $install 'Fixture.exe')
        [IO.File]::WriteAllText((Join-Path $install 'obsolete.dll'), 'obsolete')
        & (Join-Path $PSScriptRoot 'WriteUpdateManifest.ps1') -Directory $install -Product Fixture -Version '1.0.0'
        $oldManifest = [IO.File]::ReadAllText((Join-Path $install 'update-manifest.json'))
        # Personal data is deliberately outside the package inventory.
        [IO.File]::WriteAllText((Join-Path $install 'settings.json'), 'personal-settings')
        $work = Join-Path ([IO.Path]::GetTempPath()) ('Fixture-update-' + [Guid]::NewGuid().ToString('N'))
        $works += $work
        $stage = Join-Path $work 'stage'
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $(if ($scenario -eq 'rollback') { 'Broken.exe' } else { 'New.exe' })) -Destination (Join-Path $stage 'Fixture.exe')
        [IO.File]::WriteAllText((Join-Path $stage 'added.dll'), 'added')
        $nativeFolder = Join-Path $stage 'runtimes\win-x64\native'
        [IO.Directory]::CreateDirectory($nativeFolder) | Out-Null
        [IO.File]::WriteAllText((Join-Path $nativeFolder 'loader.dll'), 'native-runtime')
        & (Join-Path $PSScriptRoot 'WriteUpdateManifest.ps1') -Directory $stage -Product Fixture -Version '1.0.0'
        $stageManifest = Get-Content -LiteralPath (Join-Path $stage 'update-manifest.json') -Raw | ConvertFrom-Json
        Assert ([bool]$stageManifest.files.'runtimes/win-x64/native/loader.dll') 'The inventory omitted the native runtime.'
        Assert ($stageManifest.buildId -match '^[a-f0-9]{64}$') 'Missing build identity.'
        if ($scenario -eq 'success') {
            & (Join-Path $PSScriptRoot 'WriteUpdateManifest.ps1') -Directory $stage -Product Fixture -Version '1.0.0'
            $repeatedManifest = Get-Content -LiteralPath (Join-Path $stage 'update-manifest.json') -Raw | ConvertFrom-Json
            Assert ($stageManifest.buildId -eq $repeatedManifest.buildId) 'Identical files produced a different build identity.'
        }
        $appPath = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\src') -Filter ApplyUpdate.ps1 -Recurse | Select-Object -First 1 -ExpandProperty FullName
        Copy-Item -LiteralPath $appPath -Destination (Join-Path $work 'apply.ps1')
        $token = [Guid]::NewGuid().ToString('N')
        $config = @{ installDirectory = $install; work = $work; executable = 'Fixture.exe'; product = 'Fixture'; parentId = 2147483647; parentStarted = ''; token = $token; receipt = @{ assetId = 42; digest = 'sha256:test' } }
        [IO.File]::WriteAllText((Join-Path $work 'config.json'), ($config | ConvertTo-Json))
        $recover = $scenario -in @('recovery', 'committed')
        if ($recover) {
            $backup = Join-Path $work 'backup'
            [IO.Directory]::CreateDirectory($backup) | Out-Null
            $journal = @('Fixture.exe', 'obsolete.dll', 'update-manifest.json') | ForEach-Object { @{ path = $_; existed = $true } }
            $journal += @{ path = 'added.dll'; existed = $false }
            [IO.File]::WriteAllText((Join-Path $work 'journal.json'), ($journal | ConvertTo-Json))
            foreach ($path in @('Fixture.exe', 'obsolete.dll', 'update-manifest.json')) { Move-Item -LiteralPath (Join-Path $install $path) -Destination (Join-Path $backup $path) }
            Copy-Item -Path (Join-Path $stage '*') -Destination $install -Recurse
            $pending = @{ work = $work; token = $token; workerId = 2147483647; workerStarted = '' }
            [IO.File]::WriteAllText((Join-Path $install '.easy-update-pending.json'), ($pending | ConvertTo-Json))
            if ($scenario -eq 'committed') { [IO.File]::WriteAllText((Join-Path $work 'committed'), '') }
        }
        $lockStream = $null
        if ($scenario -eq 'locked') { $lockStream = [IO.File]::Open((Join-Path $install 'obsolete.dll'), 'Open', 'Read', 'None') }
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = Join-Path $PSHOME 'powershell.exe'
        $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $work 'apply.ps1') + '" -ConfigPath "' + (Join-Path $work 'config.json') + '"'
        if ($recover) { $start.Arguments += ' -Recover' }
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WindowStyle = 'Hidden'
        $worker = [Diagnostics.Process]::Start($start)
        try { Assert ($worker.WaitForExit(30000)) ($scenario + ': worker timed out') }
        finally { if ($lockStream) { $lockStream.Dispose() }; if (!$worker.HasExited) { $worker.Kill(); $worker.WaitForExit() } }
        Start-Sleep -Milliseconds 500
        Assert ([IO.File]::ReadAllText((Join-Path $install 'settings.json')) -eq 'personal-settings') ($scenario + ': settings changed')
        Assert (!(Test-Path -LiteralPath (Join-Path $install '.easy-update-pending.json'))) ($scenario + ': pending lock was not cleaned')
        Assert (!(Test-Path -LiteralPath $work)) ($scenario + ': temporary update/backup files remain')
        if ($scenario -in @('rollback', 'locked', 'recovery')) {
            Assert ([IO.File]::ReadAllText((Join-Path $install 'update-manifest.json')) -eq $oldManifest) ($scenario + ': original inventory was not restored')
            Assert (Test-Path -LiteralPath (Join-Path $install 'obsolete.dll')) ($scenario + ': obsolete file missing after rollback')
            Assert (!(Test-Path -LiteralPath (Join-Path $install 'added.dll'))) ($scenario + ': new file left after rollback')
        } else {
            Assert (!(Test-Path -LiteralPath (Join-Path $install 'obsolete.dll'))) ($scenario + ': obsolete binary left behind')
            Assert (Test-Path -LiteralPath (Join-Path $install 'added.dll')) ($scenario + ': new binary missing')
        }
        StopFixture $install
        Write-Host ($scenario + ': passed')
    }
} finally {
    if ([IO.Path]::GetFullPath($root).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($root) -match '^updater-integration-[a-f0-9]{32}$') {
        Get-ChildItem -LiteralPath $root -Directory | ForEach-Object { StopFixture $_.FullName }
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try { Remove-Item -LiteralPath $root -Recurse -Force; break }
            catch { if ($attempt -eq 19) { throw }; Start-Sleep -Milliseconds 200 }
        }
    }
    foreach ($work in $works) {
        if ((Test-Path -LiteralPath $work) -and $work.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($work) -match '^Fixture-update-[a-f0-9]{32}$') { Remove-Item -LiteralPath $work -Recurse -Force }
    }
}
