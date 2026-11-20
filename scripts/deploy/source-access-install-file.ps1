# Shared native no-follow namespace checks for first installation and stopped upgrades.
if (-not ('FluxSourceAccessInstallFile' -as [type])) {
    $sourceBytes=[IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'source-access-install-file.cs'))
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes)) -cne 'B4F6DC9684E731BD2A453BC0C807A20F394E60A2879FF2226EDAB7D457EC2FDA') {
        throw 'Source-access native installation support differs from the reviewed bytes.'
    }
    Add-Type -TypeDefinition ([Text.UTF8Encoding]::new($false,$true).GetString($sourceBytes)) -ErrorAction Stop
}