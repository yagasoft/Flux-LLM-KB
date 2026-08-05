[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$LiteralPath
)

$ErrorActionPreference = "Stop"

$resolvedPath = (Resolve-Path -LiteralPath $LiteralPath -ErrorAction Stop).Path
if (-not [System.IO.File]::Exists($resolvedPath)) {
    throw "The SHA-256 input must be an existing file."
}

$stream = $null
$sha256 = $null
try {
    $stream = [System.IO.File]::OpenRead($resolvedPath)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    [System.BitConverter]::ToString($sha256.ComputeHash($stream)).Replace("-", "")
} finally {
    if ($null -ne $sha256) {
        $sha256.Dispose()
    }
    if ($null -ne $stream) {
        $stream.Dispose()
    }
}
