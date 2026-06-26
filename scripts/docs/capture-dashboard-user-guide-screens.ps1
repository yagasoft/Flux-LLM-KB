[CmdletBinding()]
param(
    [string]$Node = "",
    [string]$Npm = "",
    [switch]$SkipBrowserInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$bundledNode = Join-Path $env:USERPROFILE ".cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe"
if ([string]::IsNullOrWhiteSpace($Node)) {
    if (Test-Path -LiteralPath $bundledNode) {
        $Node = $bundledNode
    } elseif (-not [string]::IsNullOrWhiteSpace($env:NODE)) {
        $Node = $env:NODE
    } else {
        $Node = "node"
    }
}
if ([string]::IsNullOrWhiteSpace($Npm)) {
    if (-not [string]::IsNullOrWhiteSpace($env:NPM)) {
        $Npm = $env:NPM
    } else {
        $Npm = "npm"
    }
}

Push-Location $repoRoot
try {
    if (-not $SkipBrowserInstall) {
        & $Npm --prefix "dashboard" exec playwright install chromium
        if ($LASTEXITCODE -ne 0) {
            throw "Playwright Chromium install failed with exit code $LASTEXITCODE"
        }
    }
    & $Node "scripts\docs\capture_dashboard_user_guide_screens.mjs"
    if ($LASTEXITCODE -ne 0) {
        throw "Real dashboard screenshot capture failed with exit code $LASTEXITCODE"
    }
} finally {
    Pop-Location
}
