[CmdletBinding()]
param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$deploymentScript = Join-Path $SourceRoot "scripts\deploy\update-native-windows.ps1"
if (-not (Test-Path -LiteralPath $deploymentScript)) {
    throw "The native deployment script is missing."
}

$output = & powershell -NoProfile -ExecutionPolicy Bypass -File $deploymentScript -PlanOnly 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "The native deployment plan failed: $output"
}

$plan = $output | ConvertFrom-Json
if ($plan.mode -ne "plan-only" -or -not $plan.loopback_only -or -not $plan.requires_explicit_migration_confirmation) {
    throw "The native deployment plan has lost its loopback or migration-confirmation gate."
}
if ($plan.required_site -ne "FluxKnowledge") {
    throw "The native deployment plan is not fixed to the FluxKnowledge IIS site."
}

$previousErrorActionPreference = $ErrorActionPreference
try {
    # This deliberately exercises a rejected native process.  Do not let the
    # test host's Stop preference turn that expected non-zero exit code into a
    # test-host failure before we can assert the deployment-script fence.
    $ErrorActionPreference = "Continue"
    $wrongSiteOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $deploymentScript -PreflightOnly -SiteName "OtherSite" 2>&1 | Out-String
    $wrongSiteExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}
if ($wrongSiteExitCode -eq 0 -or $wrongSiteOutput -notmatch "fixed FluxKnowledge IIS site") {
    throw "The native deployment executable does not reject a non-FluxKnowledge IIS target before preflight."
}

$expectedBaselineMigrations = @(
    "20260726215521_InitialPhase1",
    "20260726221653_EnforceCanonicalSqlSafety",
    "20260726235718_AddIndexGenerationMembership",
    "20260727055755_DistinguishVectorIdentityAndPayloadChecksum"
)
foreach ($migration in $expectedBaselineMigrations) {
    if ($migration -notin @($plan.required_baseline_migration_ids)) {
        throw "The native deployment plan is missing baseline migration $migration."
    }
}

$expectedMigrations = @(
    "20260729080641_AddGpuSchedulerDurability",
    "20260729094809_AddGpuSchedulerOperationReceipts",
    "20260729103104_CompleteGpuSchedulerOperationReceipts",
    "20260729120305_AddGpuSchedulerOperationReceiptRequestFingerprint",
    "20260802182703_AddGpuSchedulerBinaryFenceCollation",
    "20260802191240_AddGpuSchedulerOpaqueKeyCanonicality"
)
foreach ($migration in $expectedMigrations) {
    if ($migration -notin @($plan.scheduler_migration_ids)) {
        throw "The native deployment plan is missing scheduler migration $migration."
    }
}

$requiredEndpoints = @("/health/live", "/health/ready", "/api/index-health", "/api/gpu-status")
foreach ($endpoint in $requiredEndpoints) {
    if ($endpoint -notin @($plan.required_endpoints)) {
        throw "The native deployment plan is missing required endpoint $endpoint."
    }
}

$deploymentScriptText = Get-Content -LiteralPath $deploymentScript -Raw
if ($deploymentScriptText -notmatch 'Invoke-WebRequest\s+-UseBasicParsing') {
    throw "The native deployment probe is not compatible with Windows PowerShell's basic parsing mode."
}

Write-Output "Native deployment plan contract passed."
