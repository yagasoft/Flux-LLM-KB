[CmdletBinding()]
param(
    [string]$FeatureWorktree = (Get-Location).Path,
    [string]$MainRoot = "",
    [switch]$DryRun,
    [switch]$GoLive,
    [switch]$ConfirmCleanSlate,
    [switch]$ConfirmConfigureVss,
    [switch]$ConfirmDestroySql,
    [switch]$ConfirmRegisterCodex,
    [switch]$ConfirmRemoveLegacyPlugin
)

$ErrorActionPreference = 'Stop'
$closeout = Join-Path $PSScriptRoot 'complete-feature.ps1'
if (-not (Test-Path -LiteralPath $closeout -PathType Leaf)) {
    throw 'The guarded native closeout entrypoint is unavailable.'
}

$acknowledgements = @(
    $ConfirmCleanSlate,
    $ConfirmConfigureVss,
    $ConfirmDestroySql,
    $ConfirmRegisterCodex,
    $ConfirmRemoveLegacyPlugin)
if ($GoLive -and -not ($acknowledgements -notcontains $false)) {
    throw '-GoLive requires -ConfirmCleanSlate, -ConfirmConfigureVss, -ConfirmDestroySql, -ConfirmRegisterCodex and -ConfirmRemoveLegacyPlugin.'
}
if (-not $GoLive -and ($acknowledgements -contains $true)) {
    throw 'Clean-slate acknowledgement switches require -GoLive.'
}

$closeoutArguments = @{ FeatureWorktree = $FeatureWorktree }
if (-not [string]::IsNullOrWhiteSpace($MainRoot)) { $closeoutArguments.MainRoot = $MainRoot }
if ($DryRun) { $closeoutArguments.DryRun = $true }
if ($GoLive) { $closeoutArguments.GoLive = $true }
if ($ConfirmCleanSlate) { $closeoutArguments.ConfirmCleanSlate = $true }
if ($ConfirmConfigureVss) { $closeoutArguments.ConfirmConfigureVss = $true }
if ($ConfirmDestroySql) { $closeoutArguments.ConfirmDestroySql = $true }
if ($ConfirmRegisterCodex) { $closeoutArguments.ConfirmRegisterCodex = $true }
if ($ConfirmRemoveLegacyPlugin) { $closeoutArguments.ConfirmRemoveLegacyPlugin = $true }

& $closeout @closeoutArguments
exit $LASTEXITCODE
