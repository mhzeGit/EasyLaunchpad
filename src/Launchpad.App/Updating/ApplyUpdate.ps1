param([Parameter(Mandatory=$true)][string]$ConfigPath, [switch]$Recover)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$work = [IO.Path]::GetFullPath($config.work).TrimEnd('\')
$install = [IO.Path]::GetFullPath($config.installDirectory).TrimEnd('\')
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
# All recursive cleanup is confined to this helper's own unique temporary directory.
if ($work -ne [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -or
    !$work.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($work) -notmatch '-update-[a-f0-9]{32}$') { throw 'Invalid update workspace.' }

function SafePath([string]$root, [string]$relative) {
    if (!$relative -or $relative -match '(^|[\\/])(\.|\.\.|)([\\/]|$)' -or $relative -match '[:*?"<>|]' -or
        $relative -match '(^|[\\/])(CON|PRN|AUX|NUL|COM\d|LPT\d)(\.|[\\/]|$)' -or $relative -match '[. ]([\\/]|$)') { throw 'Unsafe update path.' }
    $prefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath((Join-Path $prefix $relative))
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Update path escapes its folder.' }
    $ancestor = $full
    while ($ancestor) {
        if ([IO.File]::Exists($ancestor) -or [IO.Directory]::Exists($ancestor)) {
            if ([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) { throw 'Update path contains a junction or symbolic link.' }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $full
}
function WriteJson([string]$path, $value) {
    [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 8))
}
function ReplaceFile([string]$source, [string]$target) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    $temporary = $target + '.easy-update-' + $config.token + '.tmp'
    [IO.File]::Copy($source, $temporary, $true)
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary, $target, [System.Management.Automation.Language.NullString]::Value) }
            else { [IO.File]::Move($temporary, $target) }
            return
        } catch [IO.IOException] { if ($attempt -eq 29) { throw }; Start-Sleep -Milliseconds 200 }
    }
}
function ReadManifest([string]$root) {
    $manifest = Get-Content -LiteralPath (SafePath $root 'update-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.product -ne $config.product -or !$manifest.files.($config.executable)) { throw 'Wrong update application.' }
    $seen = @{}
    foreach ($property in $manifest.files.PSObject.Properties) {
        $null = SafePath $root $property.Name
        if ($seen.ContainsKey($property.Name) -or $property.Name -match '^\.easy-' -or
            $property.Name -eq 'update-manifest.json' -or $property.Value -notmatch '^[a-f0-9]{64}$') { throw 'Invalid file inventory.' }
        $seen[$property.Name] = $true
    }
    return $manifest
}
function FileHash([string]$path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}
function StartApp([bool]$acknowledge) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = SafePath $install $config.executable
    $start.WorkingDirectory = $install
    $start.UseShellExecute = $false
    $start.Arguments = '--background'
    if ($acknowledge) { $start.Arguments += ' --update-complete ' + $config.token }
    return [Diagnostics.Process]::Start($start)
}
function RestoreFiles {
    if (!(Test-Path -LiteralPath $journalPath)) { return }
    $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    foreach ($entry in $journal) {
        $target = SafePath $install $entry.path
        $saved = SafePath $backup $entry.path
        if (Test-Path -LiteralPath $saved -PathType Leaf) {
            ReplaceFile $saved $target
        } elseif (!$entry.existed -and (Test-Path -LiteralPath $target -PathType Leaf)) {
            Remove-Item -LiteralPath $target -Force
        }
        $temporary = $target + '.easy-update-' + $config.token + '.tmp'
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

$pendingPath = SafePath $install '.easy-update-pending.json'
$stage = Join-Path $work 'stage'
$backup = Join-Path $work 'backup'
$journalPath = Join-Path $work 'journal.json'
$committed = Join-Path $work 'committed'
$mutex = $null
$ownsMutex = $false
$child = $null
$mayRestore = $false
$cleanup = $false
$ownsPending = $false
try {
    $sha = [Security.Cryptography.SHA256]::Create()
    $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($install.ToLowerInvariant()))).Replace('-', '')
    $sha.Dispose()
    $mutex = New-Object Threading.Mutex($false, ('Local\EasyTools.Update.' + $key))
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (!$ownsMutex) { exit 2 }
    if ($Recover) {
        $pending = Get-Content -LiteralPath $pendingPath -Raw | ConvertFrom-Json
        if ($pending.work -ne $work -or $pending.token -ne $config.token) { throw 'Wrong recovery record.' }
        $ownsPending = $true
        $mayRestore = $true
        if (!(Test-Path -LiteralPath $committed)) {
            $childRecord = Join-Path $work 'child.json'
            if (Test-Path -LiteralPath $childRecord) {
                $record = Get-Content -LiteralPath $childRecord -Raw | ConvertFrom-Json
                $running = Get-Process -Id $record.id -ErrorAction SilentlyContinue
                if ($running -and $running.StartTime.ToUniversalTime().ToString('O') -eq $record.started -and
                    $running.Path -eq (SafePath $install $config.executable)) { $running.Kill(); $running.WaitForExit() }
            }
            RestoreFiles
            WriteJson (Join-Path $install '.easy-update-result.json') @{ error = 'An interrupted update was recovered.' }
        }
        $null = StartApp $true
        Remove-Item -LiteralPath $pendingPath -Force
        $cleanup = $true
    } else {
        $old = ReadManifest $install
        $new = ReadManifest $stage
        foreach ($file in $new.files.PSObject.Properties) {
            if ((FileHash (SafePath $stage $file.Name)) -ne $file.Value) { throw 'Update checksum mismatch.' }
        }
        # Refuse directory/file conflicts before the app exits.
        $paths = @($old.files.PSObject.Properties.Name) + @($new.files.PSObject.Properties.Name) + @('update-manifest.json', '.easy-update-receipt.json')
        $paths = @($paths | Sort-Object -Unique)
        $journal = @($paths | ForEach-Object {
            $target = SafePath $install $_
            if (Test-Path -LiteralPath $target -PathType Container) { throw 'An update file conflicts with an existing directory.' }
            @{ path = $_; existed = (Test-Path -LiteralPath $target -PathType Leaf) }
        })
        WriteJson $journalPath $journal
        $worker = [Diagnostics.Process]::GetCurrentProcess()
        $pending = @{ work = $work; token = $config.token; workerId = $PID; workerStarted = $worker.StartTime.ToUniversalTime().ToString('O') }
        # Publish a complete marker atomically; startups must never read partial JSON.
        $pendingTemporary = $pendingPath + '.' + $config.token + '.tmp'
        $lock = [IO.File]::Open($pendingTemporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes(($pending | ConvertTo-Json))
            $lock.Write($bytes, 0, $bytes.Length)
            $lock.Flush($true)
        } finally { $lock.Dispose() }
        try { [IO.File]::Move($pendingTemporary, $pendingPath) }
        finally { if (Test-Path -LiteralPath $pendingTemporary) { Remove-Item -LiteralPath $pendingTemporary -Force } }
        $ownsPending = $true
        [IO.File]::WriteAllText((Join-Path $work 'ready'), '')
        $parent = Get-Process -Id $config.parentId -ErrorAction SilentlyContinue
        if ($parent -and $parent.StartTime.ToUniversalTime().ToString('O') -eq $config.parentStarted) {
            if (!$parent.WaitForExit(60000)) { throw 'The app did not close in time. No update files were installed.' }
        }
        $mayRestore = $true
        foreach ($entry in $journal) {
            $target = SafePath $install $entry.path
            if (Test-Path -LiteralPath $target -PathType Leaf) {
                $saved = SafePath $backup $entry.path
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved)) | Out-Null
                # Never remove the executable while staging. Only complete backup copies are recoverable.
                [IO.File]::Copy($target, ($saved + '.partial'), $false)
                [IO.File]::Move(($saved + '.partial'), $saved)
            }
        }
        foreach ($file in $new.files.PSObject.Properties) {
            $target = SafePath $install $file.Name
            ReplaceFile (SafePath $stage $file.Name) $target
        }
        foreach ($file in $old.files.PSObject.Properties) {
            if (!$new.files.($file.Name)) {
                $obsolete = SafePath $install $file.Name
                if (Test-Path -LiteralPath $obsolete) { Remove-Item -LiteralPath $obsolete -Force }
            }
        }
        ReplaceFile (Join-Path $stage 'update-manifest.json') (Join-Path $install 'update-manifest.json')
        WriteJson (Join-Path $work 'receipt.json') $config.receipt
        ReplaceFile (Join-Path $work 'receipt.json') (Join-Path $install '.easy-update-receipt.json')
        $child = StartApp $true
        WriteJson (Join-Path $work 'child.json') @{ id = $child.Id; started = $child.StartTime.ToUniversalTime().ToString('O') }
        $healthy = Join-Path $work 'healthy'
        $ok = $false
        for ($i = 0; $i -lt 450; $i++) {
            if ($child.HasExited) { break }
            if ((Test-Path -LiteralPath $healthy) -and (Get-Content -LiteralPath $healthy -Raw).Trim() -eq [string]$child.Id) { $ok = $true; break }
            Start-Sleep -Milliseconds 100
        }
        if (!$ok -or $child.WaitForExit(3000)) { throw 'The updated app did not start correctly.' }
        [IO.File]::WriteAllText($committed, '')
        # Only empty directories belonging to previous build files may be removed.
        foreach ($entry in $journal) {
            $directory = [IO.Path]::GetDirectoryName((SafePath $install $entry.path))
            while ($directory -ne $install -and $directory.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase)) {
                if (!(Test-Path -LiteralPath $directory) -or @(Get-ChildItem -LiteralPath $directory -Force).Count) { break }
                [IO.Directory]::Delete($directory)
                $directory = [IO.Path]::GetDirectoryName($directory)
            }
        }
        $cleanup = $true
        Remove-Item -LiteralPath $pendingPath -Force
    }
} catch {
    $failure = $_.Exception.Message
    try {
        if ($child -and !$child.HasExited) { $child.Kill(); $child.WaitForExit() }
        if ($mayRestore -and !(Test-Path -LiteralPath $committed)) { RestoreFiles }
        if ($mayRestore) {
            WriteJson (Join-Path $install '.easy-update-result.json') @{ error = $failure }
            $null = StartApp $true
        }
        if ($ownsPending -and (Test-Path -LiteralPath $pendingPath)) { Remove-Item -LiteralPath $pendingPath -Force }
        $cleanup = $true
    } catch {
        $failure += "`r`nRecovery will be retried on next launch. Backup retained at: " + $work
    }
    $logDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $config.product
    [IO.Directory]::CreateDirectory($logDir) | Out-Null
    [IO.File]::WriteAllText((Join-Path $logDir 'update-error.txt'), $failure)
} finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
    if ($cleanup) { Remove-Item -LiteralPath $work -Recurse -Force }
}
