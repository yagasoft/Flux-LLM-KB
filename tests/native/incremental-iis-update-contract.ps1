[CmdletBinding()]
param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$deploymentScript = Join-Path $SourceRoot "scripts\deploy\update-native-iis-incremental.ps1"

if (-not (Test-Path -LiteralPath $deploymentScript -PathType Leaf)) {
    throw "The incremental IIS deployment script is missing."
}

function Invoke-ExpectedRejection {
    param([string[]]$Arguments)

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & pwsh -NoProfile -File $deploymentScript @Arguments 2>&1 | Out-String
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

$planOutput = & pwsh -NoProfile -File $deploymentScript -SourceRoot $SourceRoot -PlanOnly 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "The incremental IIS deployment plan failed: $planOutput"
}

$plan = $planOutput | ConvertFrom-Json
if ($plan.mode -ne "plan-only" -or
    $plan.site_name -ne "FluxKnowledge" -or
    $plan.site_url -ne "http://127.0.0.1:5137" -or
    $plan.application_root -ne "I:\FluxKnowledge\App" -or
    $plan.recovery_root -ne "I:\FluxKnowledge\Recovery" -or
    $plan.migrations -ne $false -or
    $plan.clean_slate -ne $false -or
    $plan.rollback -ne "automatic-application-payload-restore") {
    throw "The incremental IIS plan is not restricted to the existing application payload and loopback site."
}

$ordinary = Invoke-ExpectedRejection -Arguments @('-SourceRoot', $SourceRoot)
if ($ordinary.ExitCode -eq 0 -or $ordinary.Output -notmatch 'requires -Apply') {
    throw "The incremental IIS updater can execute without an explicit -Apply acknowledgement."
}

$alternateRoot = Invoke-ExpectedRejection -Arguments @(
    '-SourceRoot', $SourceRoot,
    '-PlanOnly',
    '-DeployRoot', 'C:\alternate-flux-app')
if ($alternateRoot.ExitCode -eq 0 -or $alternateRoot.Output -notmatch 'canonical I:\\FluxKnowledge\\App root') {
    throw "The incremental IIS updater accepts a non-canonical application root."
}

Write-Output "Incremental IIS update contract passed."
