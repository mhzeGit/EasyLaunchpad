param([Parameter(Mandatory=$true)][string]$Directory, [string]$Product, [string]$Version)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
$files = [ordered]@{}
Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
    # RID-specific build output and browser profiles are not owned by the parent build.
    if ($relative -ne 'update-manifest.json' -and $relative -notmatch '^(win-x64|win-x86|win-arm64)/' -and
        $relative -notmatch '(^|/)[^/]*\.WebView2(/|$)' -and $relative -notmatch '^\.easy-') {
        $sha = [Security.Cryptography.SHA256]::Create()
        $stream = [IO.File]::OpenRead($_.FullName)
        try { $files[$relative] = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
    }
}
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $inventory = ($files.GetEnumerator() | ForEach-Object { $_.Key + ':' + $_.Value }) -join "`n"
    $buildId = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($inventory))).Replace('-', '').ToLowerInvariant()
} finally { $sha.Dispose() }
$manifest = @{ product = $Product; version = $Version; builtUtc = [DateTime]::UtcNow.ToString('O'); buildId = $buildId; files = $files }
$json = $manifest | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $root 'update-manifest.json'), $json)
