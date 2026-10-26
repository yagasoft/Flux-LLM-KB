# Shared native no-follow namespace checks for first installation and stopped upgrades.
if (-not ('FluxSourceAccessInstallFile' -as [type])) {
    $sourceBytes=[IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'source-access-install-file.cs'))
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes)) -cne 'D159B53261B0CADC936DAF38221B77DBE833A99CEDDA1CEF23055152B04C9F21') {
        throw 'Source-access native installation support differs from the reviewed bytes.'
    }
    Add-Type -TypeDefinition ([Text.UTF8Encoding]::new($false,$true).GetString($sourceBytes)) -ErrorAction Stop
}