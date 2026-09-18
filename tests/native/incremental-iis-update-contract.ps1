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
    $plan.payload_acl -ne "inherit-from-live-root" -or
    $plan.rollback -ne "automatic-application-payload-restore" -or
    $plan.deployment_validation_hold -ne $true -or
    $plan.candidate_validation -ne "held-loopback-probes-and-unchanged-retained-pipeline-state") {
    throw "The incremental IIS plan is not restricted to the existing application payload and loopback site."
}

$remediationPlanOutput = & pwsh -NoProfile -File $deploymentScript -SourceRoot $SourceRoot -PlanOnly -DeferReadinessForScopedRemediation 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "The scoped readiness-remediation deployment plan failed: $remediationPlanOutput"
}
$remediationPlan = $remediationPlanOutput | ConvertFrom-Json
if ($remediationPlan.migrations -ne $false -or
    $remediationPlan.readiness_remediation -ne "requires exact readiness HTTP 503; post-activation readiness remains pending") {
    throw "The scoped readiness-remediation plan is not explicitly restricted to the unready, no-migration repair path."
}

$deploymentScriptText = Get-Content -LiteralPath $deploymentScript -Raw
$requiredDeploymentValidationSteps = @(
    'New-DeploymentValidationHold',
    'Get-RetainedPipelineStateBaseline',
    'Invoke-RequiredLoopbackProbes',
    'Assert-RetainedPipelineStateUnchanged',
    'Remove-DeploymentValidationHold'
)
foreach ($step in $requiredDeploymentValidationSteps) {
    if ($deploymentScriptText -notmatch [regex]::Escape($step)) {
        throw "The incremental IIS updater is missing deployment-validation step $step."
    }
}
$requiredSqlClientCompatibilitySteps = @(
    'ConvertTo-DeploymentValidationConnectionString',
    'TrustServerCertificate=',
    'ConnectRetryCount='
)
foreach ($step in $requiredSqlClientCompatibilitySteps) {
    if ($deploymentScriptText -notmatch [regex]::Escape($step)) {
        throw "The incremental IIS updater is missing SQL-client compatibility step $step."
    }
}
$legacySqlClientConnectionString = 'Data Source=localhost;Initial Catalog=FluxKnowledge;Integrated Security=True;Trust Server Certificate=True;Connect Retry Count=0'
$normalisedSqlClientConnectionString = [regex]::Replace(
    $legacySqlClientConnectionString,
    '(?i)(^|;)\s*Trust Server Certificate\s*=',
    '$1TrustServerCertificate=')
$normalisedSqlClientConnectionString = [regex]::Replace(
    $normalisedSqlClientConnectionString,
    '(?i)(^|;)\s*Connect Retry Count\s*=',
    '$1ConnectRetryCount=')
try {
    $normalisedSqlClientBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($normalisedSqlClientConnectionString)
    $normalisedSqlClientConnection = [System.Data.SqlClient.SqlConnection]::new($normalisedSqlClientBuilder.ConnectionString)
}
catch {
    throw "The incremental IIS updater cannot read the existing production SQL connection-string spellings."
}
if (-not $normalisedSqlClientBuilder.TrustServerCertificate -or $normalisedSqlClientBuilder.ConnectRetryCount -ne 0) {
    throw "The incremental IIS updater did not preserve the existing production SQL connection-string values."
}
if ($deploymentScriptText -match [regex]::Escape('.ApplicationName =')) {
    throw "The incremental IIS updater uses the unsupported legacy SQL-client ApplicationName property."
}
$normalisedSqlClientConnection.Dispose()
$generatedSqlSplitter = [regex]::Match(
    $deploymentScriptText,
    '\[regex\]::Split\(\$script,\s*"(?<pattern>[^"]+)"\)')
if (-not $generatedSqlSplitter.Success) {
    throw "The incremental IIS updater no longer exposes its generated-SQL batch splitter for contract validation."
}
$generatedSqlBatches = @(
    [regex]::Split("SELECT 1;`r`nGO`r`nSELECT 2;", $generatedSqlSplitter.Groups['pattern'].Value) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($generatedSqlBatches.Count -ne 2 -or
    $generatedSqlBatches[0].Trim() -ne 'SELECT 1;' -or
    $generatedSqlBatches[1].Trim() -ne 'SELECT 2;') {
    throw "The incremental IIS updater does not split generated SQL batches on GO."
}
$triggerBatches = @(
    [regex]::Split(@"
BEGIN TRANSACTION;
ALTER TRIGGER [dbo].[One] ON [dbo].[One] AFTER UPDATE AS BEGIN SELECT 1; END;
ALTER TRIGGER [dbo].[Two] ON [dbo].[Two] AFTER UPDATE AS BEGIN SELECT 2; END;
COMMIT;
GO
"@, $generatedSqlSplitter.Groups['pattern'].Value) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($triggerBatches.Count -ne 4 -or
    -not $triggerBatches[1].TrimStart().StartsWith('ALTER TRIGGER [dbo].[One]', [StringComparison]::Ordinal) -or
    -not $triggerBatches[2].TrimStart().StartsWith('ALTER TRIGGER [dbo].[Two]', [StringComparison]::Ordinal) -or
    -not $triggerBatches[3].TrimStart().StartsWith('COMMIT;', [StringComparison]::Ordinal)) {
    throw "The incremental IIS updater does not isolate ALTER TRIGGER statements and post-trigger migration work into their required SQL batches."
}
$holdCreatedAt = $deploymentScriptText.IndexOf('New-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $releaseId')
$candidateStartedAt = $deploymentScriptText.IndexOf('Start-WebAppPool -Name $SiteName', $holdCreatedAt)
$probedAt = $deploymentScriptText.IndexOf('Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds', $candidateStartedAt)
$stateComparedAt = $deploymentScriptText.IndexOf('Assert-RetainedPipelineStateUnchanged', $probedAt)
$holdReleasedAt = $deploymentScriptText.IndexOf('Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $releaseId', $stateComparedAt)
if ($holdCreatedAt -lt 0 -or $candidateStartedAt -lt 0 -or $probedAt -lt $candidateStartedAt -or
    $stateComparedAt -lt $probedAt -or $holdReleasedAt -lt $stateComparedAt) {
    throw 'The incremental IIS updater can release the deployment-validation hold before candidate probes and unchanged-state validation.'
}
$preflightStart = $deploymentScriptText.IndexOf('function Assert-IncrementalIisPreflight')
$preflightEnd = $deploymentScriptText.IndexOf('Assert-CanonicalPath', $preflightStart)
if ($preflightStart -lt 0 -or $preflightEnd -lt $preflightStart) {
    throw 'The incremental IIS updater preflight contract is not readable.'
}
if ($deploymentScriptText.Substring($preflightStart, $preflightEnd - $preflightStart) -match [regex]::Escape('Invoke-RequiredLoopbackProbes')) {
    throw 'The incremental IIS updater requires a failing payload to pass health probes before its held recovery candidate can start.'
}
foreach ($requiredScopedRemediationStep in @(
    '[switch]$DeferReadinessForScopedRemediation',
    'Invoke-ScopedReadinessRemediationProbes',
    'exact HTTP 503',
    'readiness remediation pending')) {
    if ($deploymentScriptText -notmatch [regex]::Escape($requiredScopedRemediationStep)) {
        throw "The scoped readiness-remediation deployment path is missing $requiredScopedRemediationStep."
    }
}
$scopedRemediationWithMigrations = Invoke-ExpectedRejection -Arguments @(
    '-SourceRoot', $SourceRoot,
    '-PlanOnly',
    '-ApplyMigrations',
    '-DeferReadinessForScopedRemediation')
if ($scopedRemediationWithMigrations.ExitCode -eq 0 -or $scopedRemediationWithMigrations.Output -notmatch 'cannot be combined with -ApplyMigrations') {
    throw "The scoped readiness-remediation path permits schema migration."
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
