#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourcePluginRoot,
    [Parameter(Mandatory)][string]$CachedPluginRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedAdapterSha256,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
function Assert-NoReparsePoint([string]$Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Hook repair refuses reparse points.'
        }
    }
}
function Get-Sha256([byte[]]$Bytes) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
}
$sourceRoot = [IO.Path]::GetFullPath($SourcePluginRoot).TrimEnd('\')
$cacheRoot = [IO.Path]::GetFullPath($CachedPluginRoot).TrimEnd('\')
$adapter = Join-Path $sourceRoot 'hooks\invoke-native-hook.ps1'
$target = Join-Path $cacheRoot 'hooks\invoke-native-hook.ps1'
foreach ($path in @($sourceRoot, $cacheRoot, $adapter, (Join-Path $cacheRoot 'hooks'))) { Assert-NoReparsePoint $path }
foreach ($relative in @('.codex-plugin\plugin.json', '.mcp.json', 'hooks\hooks.json')) {
    Assert-NoReparsePoint (Join-Path $sourceRoot $relative)
    Assert-NoReparsePoint (Join-Path $cacheRoot $relative)
}
$sourceManifest = Get-Content -LiteralPath (Join-Path $sourceRoot '.codex-plugin\plugin.json') -Raw | ConvertFrom-Json
$cacheManifest = Get-Content -LiteralPath (Join-Path $cacheRoot '.codex-plugin\plugin.json') -Raw | ConvertFrom-Json
if ($sourceManifest.name -cne 'fluxknowledge' -or $cacheManifest.name -cne $sourceManifest.name -or
    [string]::IsNullOrWhiteSpace($sourceManifest.version) -or $cacheManifest.version -cne $sourceManifest.version -or
    [IO.Path]::GetFileName($sourceRoot) -cne 'fluxknowledge' -or [IO.Path]::GetFileName($cacheRoot) -cne $sourceManifest.version) {
    throw 'Hook repair requires the exact FluxKnowledge source and cached plugin identity.'
}
foreach ($relative in @('.mcp.json', 'hooks\hooks.json')) {
    if ((Get-FileHash -LiteralPath (Join-Path $sourceRoot $relative)).Hash -cne
        (Get-FileHash -LiteralPath (Join-Path $cacheRoot $relative)).Hash) {
        throw 'Source and cached hook/MCP definitions differ; preserve the cache.'
    }
}
$bytes = [IO.File]::ReadAllBytes($adapter)
$expectedHash = $ExpectedAdapterSha256.ToUpperInvariant()
if ((Get-Sha256 $bytes) -cne $expectedHash) { throw 'Canonical adapter does not match its reviewed SHA-256.' }
$before = @(Get-ChildItem -LiteralPath $cacheRoot -Recurse -File -Force | ForEach-Object {
    Assert-NoReparsePoint $_.FullName
    @{ path = $_.FullName; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
})
$exists = Test-Path -LiteralPath $target
if ($exists) {
    Assert-NoReparsePoint $target
    if ((Get-FileHash -LiteralPath $target).Hash -cne $expectedHash) { throw 'Existing cached adapter conflicts; do not overwrite it.' }
}
$receipt = [ordered]@{
    action = if ($exists) { 'unchanged' } else { 'restore-missing-adapter' }
    applied = $false; target = $target; source_sha256 = $expectedHash; source_bytes = $bytes.Length
    existing_files_preserved = $true; app_actions = 0; configuration_changes = 0; hook_invocations = 0
    rollback = 'Remove only a newly created adapter whose bytes still match this receipt; preserve all other files.'
}
if ($Apply -and !$exists) {
    $temporary = Join-Path (Split-Path -Parent $target) ('.hook-repair-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $stream = [IO.File]::Open($temporary, 'CreateNew', 'Write', 'None')
        try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        Assert-NoReparsePoint (Split-Path -Parent $target)
        [IO.File]::Move($temporary, $target, $false)
        if ((Get-FileHash -LiteralPath $target).Hash -cne $expectedHash) { throw 'Restored adapter hash mismatch; retain evidence.' }
        $receipt.applied = $true
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Assert-NoReparsePoint $temporary
            if ((Get-FileHash -LiteralPath $temporary).Hash -ceq $expectedHash) { Remove-Item -LiteralPath $temporary }
        }
    }
    foreach ($file in $before) {
        if ((Get-FileHash -LiteralPath $file.path).Hash -cne $file.sha256) { throw 'A pre-existing cache file changed; stop.' }
    }
}
$receipt | ConvertTo-Json -Depth 4
