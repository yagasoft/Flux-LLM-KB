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
    "20260802191240_AddGpuSchedulerOpaqueKeyCanonicality",
    "20260805112341_AddGpuExecutorDispatchAndReceipts"
)
if ((@($plan.scheduler_migration_ids) -join "|") -ne ($expectedMigrations -join "|")) {
    throw "The native deployment plan does not expose exactly the approved scheduler migration sequence."
}
if ($plan.scheduler_migration_target -ne $expectedMigrations[-1]) {
    throw "The native deployment plan does not pin the approved scheduler migration target."
}

$expectedPhase3AMigrations = @(
    "20260806120000_AddPhase3ALocalSources",
    "20260808191700_AddRetainedTextPipelineLink"
)
if ((@($plan.phase3a_migration_ids) -join "|") -ne ($expectedPhase3AMigrations -join "|")) {
    throw "The native deployment plan does not expose exactly the approved Phase 3A migration sequence."
}
if ($plan.deployment_migration_target -ne $expectedPhase3AMigrations[-1]) {
    throw "The native deployment plan does not pin the approved Phase 3A migration target."
}
if (-not $plan.source_artifact_store_requires_app_pool_modify_access) {
    throw "The native deployment plan does not require writable retained source storage for the IIS application pool."
}
if (-not $plan.source_artifact_store_acl_rejects_protected_root_overlap) {
    throw "The native deployment plan does not fence retained-source storage permissions from protected roots."
}

$requiredEndpoints = @("/health/live", "/health/ready", "/api/index-health", "/api/gpu-status")
foreach ($endpoint in $requiredEndpoints) {
    if ($endpoint -notin @($plan.required_endpoints)) {
        throw "The native deployment plan is missing required endpoint $endpoint."
    }
}

$hashHelper = Join-Path $SourceRoot "scripts\deploy\get-sha256.ps1"
$hashProbePath = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllBytes($hashProbePath, [System.Text.Encoding]::ASCII.GetBytes("abc"))
    $escapedHashHelper = $hashHelper.Replace("'", "''")
    $escapedHashProbePath = $hashProbePath.Replace("'", "''")
    $hashProbeScript = @"
`$ErrorActionPreference = "Stop"
Import-Module Microsoft.PowerShell.Management -ErrorAction Stop
Remove-Module Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue
`$PSModuleAutoloadingPreference = "None"
if (Get-Command Get-FileHash -ErrorAction SilentlyContinue) {
    throw "Get-FileHash remained available after module autoload was disabled."
}
& '$escapedHashHelper' -LiteralPath '$escapedHashProbePath'
"@
    $encodedHashProbeScript = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($hashProbeScript))
    $hashOutput = & powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encodedHashProbeScript 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "The native deployment SHA-256 helper failed without Get-FileHash in a child Windows PowerShell host: $hashOutput"
    }
    if ($hashOutput.Trim() -ne "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD") {
        throw "The native deployment SHA-256 helper produced an unexpected digest: $hashOutput"
    }
} finally {
    if (Test-Path -LiteralPath $hashProbePath) {
        Remove-Item -LiteralPath $hashProbePath -Force
    }
}

$deploymentScriptText = Get-Content -LiteralPath $deploymentScript -Raw
if ($deploymentScriptText -notmatch 'Invoke-WebRequest\s+-UseBasicParsing') {
    throw "The native deployment probe is not compatible with Windows PowerShell's basic parsing mode."
}
if ($deploymentScriptText -match '\bGet-FileHash\b' -or $deploymentScriptText -notmatch 'get-sha256\.ps1') {
    throw "The native deployment executable is not wired to the compatible SHA-256 helper."
}

Write-Output "Native deployment plan contract passed."
