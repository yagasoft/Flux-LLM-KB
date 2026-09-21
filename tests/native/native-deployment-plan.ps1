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
$goLiveModule = Join-Path $SourceRoot "scripts\deploy\native-go-live.psm1"
$cliProgram = Join-Path $SourceRoot "src\FluxKnowledge.Cli\Program.cs"
$outlookValidator = Join-Path $SourceRoot "scripts\deploy\validate-native-outlook-ingress.ps1"
$workerValidator = Join-Path $SourceRoot "scripts\deploy\validate-native-worker-supervision.ps1"
$phase5Validator = Join-Path $SourceRoot "scripts\deploy\validate-phase-5-deployment.ps1"
$loopbackProbeHelper = Join-Path $SourceRoot "scripts\deploy\loopback-deployment-safety.ps1"
$bootstrapManifestTest = Join-Path $SourceRoot "tests\native\native-go-live-bootstrap-manifest.ps1"
if (-not (Test-Path -LiteralPath $deploymentScript) -or -not (Test-Path -LiteralPath $goLiveModule -PathType Leaf)) {
    throw "The native deployment script is missing."
}
if (-not (Test-Path -LiteralPath $cliProgram -PathType Leaf)) {
    throw "The normal CLI dispatch source is missing."
}

function Assert-False {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { throw $Message }
}

function Test-CliContainsCommand {
    param([Parameter(Mandatory)][string]$Name)

    $cliText = Get-Content -LiteralPath $cliProgram -Raw
    return $cliText -match ('(?m)^\s*"{0}"\s*=>' -f [regex]::Escape($Name))
}

Assert-False (Test-CliContainsCommand -Name 'provision-sql') 'Normal CLI cannot initialise SQL.'
foreach ($requiredScript in @($outlookValidator, $workerValidator, $phase5Validator, $loopbackProbeHelper, $bootstrapManifestTest)) {
    if (-not (Test-Path -LiteralPath $requiredScript -PathType Leaf)) {
        throw "A required native deployment validator or safety helper is missing: $requiredScript"
    }
}

$output = & powershell -NoProfile -ExecutionPolicy Bypass -File $deploymentScript `
    -PlanOnly 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "The native deployment plan failed: $output"
}

$plan = $output | ConvertFrom-Json
if ($plan.mode -ne "plan-only" -or -not $plan.loopback_only -or -not $plan.requires_explicit_migration_confirmation) {
    throw "The native deployment plan has lost its loopback or migration-confirmation gate."
}
if ($plan.migration_update_available -ne $false) {
    throw "The native deployment plan must keep non-disposable migration unavailable until guarded VSS go-live authority exists."
}
if ($plan.execution_available -ne $false -or
    $plan.executionAvailable -ne $false -or
    $plan.root -ne 'I:\FluxKnowledge' -or
    $plan.siteName -ne 'FluxKnowledge' -or
    $plan.loopbackPort -ne 5137 -or
    $plan.live_root -ne 'I:\FluxKnowledge' -or
    $plan.application_root -ne 'I:\FluxKnowledge\App' -or
    $plan.config_root -ne 'I:\FluxKnowledge\Config' -or
    $plan.sql_data_file -ne 'I:\FluxKnowledge\Data\Sql\Data\FluxKnowledge.mdf' -or
    $plan.sql_log_file -ne 'I:\FluxKnowledge\Data\Sql\Log\FluxKnowledge_log.ldf' -or
    $plan.index_root -ne 'I:\FluxKnowledge\Data\Index' -or
    $plan.retained_root -ne 'I:\FluxKnowledge\Data\Retained' -or
    $plan.spool_root -ne 'I:\FluxKnowledge\Runtime\Spool' -or
    $plan.temp_root -ne 'I:\FluxKnowledge\Runtime\Temp' -or
    $plan.logs_root -ne 'I:\FluxKnowledge\Runtime\Logs' -or
    $plan.codex_plugin_root -ne 'I:\FluxKnowledge\CodexPlugin' -or
    $plan.recovery_root -ne 'I:\FluxKnowledge\Recovery') {
    throw "The native deployment plan is not bound to the complete canonical live-root layout."
}
if ($plan.required_site -ne "FluxKnowledge") {
    throw "The native deployment plan is not fixed to the FluxKnowledge IIS site."
}
if (-not $plan.keep_outlook_host_disabled -or
    $plan.outlook_host_activation -ne $false -or
    $plan.windows_service_registration -ne $false) {
    throw "The native deployment plan may not activate the Outlook host or register a Windows Service."
}

$explicitFalseCommand = "& '$($deploymentScript.Replace("'", "''"))' -PlanOnly -KeepOutlookHostDisabled:`$false"
$explicitFalseEncoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($explicitFalseCommand))
$previousErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $explicitFalseOutput = & powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand $explicitFalseEncoded 2>&1 | Out-String
    $explicitFalseExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}
if ($explicitFalseExitCode -eq 0 -or $explicitFalseOutput -notmatch "Outlook host activation is not authorised") {
    throw "The native deployment executable accepts an explicit request to activate the Outlook host."
}
if ($plan.outlook_host_scheduler.task_name -ne 'FluxKnowledge.OutlookHost' -or
    -not $plan.outlook_host_scheduler.interactive_only -or
    -not $plan.outlook_host_scheduler.hidden -or
    $plan.outlook_host_scheduler.interval_minutes -ne 15 -or
    $plan.outlook_host_scheduler.multiple_instances -ne 'IgnoreNew' -or
    $plan.outlook_host_scheduler.verbose_diagnostics -ne $false) {
    throw 'The native deployment plan has lost the approved Outlook scheduler boundary.'
}
if (-not $plan.outlook_host_payload.published -or
    $plan.outlook_host_payload.relative_directory -ne 'outlook-host') {
    throw 'The native deployment plan does not publish the Outlook host payload.'
}
if (-not $plan.iis_anonymous_authentication_required -or -not $plan.iis_windows_authentication_prohibited) {
    throw "The native deployment plan must retain anonymous IIS access and prohibit Windows authentication."
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

$deploymentScriptText = Get-Content -LiteralPath $deploymentScript -Raw
$publicHostEffectPattern = '\b(?:Get-Website|Get-WebBinding|Stop-WebAppPool|Start-WebAppPool|Invoke-Sqlcmd|SqlConnection|codex\.exe|dotnet\s+(?:publish|run))\b'
if ($deploymentScriptText -match $publicHostEffectPattern) {
    throw "The public native deployment façade contains a production host operation."
}

function Invoke-ExpectedNativeRejection {
    param(
        [string]$Script,
        [string[]]$Arguments = @()
    )

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $commandArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Script) + $Arguments
        $rejectedOutput = & powershell @commandArguments 2>&1 | Out-String
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $rejectedOutput }
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

$ordinary = Invoke-ExpectedNativeRejection -Script $deploymentScript
if ($ordinary.ExitCode -eq 0 -or
    $ordinary.Output -notmatch 'Native deployment execution is unavailable until guarded go-live authority is implemented') {
    throw "An ordinary native deployment invocation can pass the preparation-only execution fence."
}

$directGoLive = Invoke-ExpectedNativeRejection `
    -Script $deploymentScript `
    -Arguments @('-GoLive', '-ConfirmCleanSlate', '-ConfirmConfigureVss', '-ConfirmDestroySql', '-ConfirmRegisterCodex')
if ($directGoLive.ExitCode -eq 0 -or $directGoLive.Output -notmatch 'claimed in-process authority') {
    throw 'Direct native go-live execution is not fenced behind the in-process authority.'
}

$alternateRoot = Invoke-ExpectedNativeRejection `
    -Script $deploymentScript `
    -Arguments @('-PlanOnly', '-DeployRoot', 'C:\alternate-flux-app')
if ($alternateRoot.ExitCode -eq 0 -or $alternateRoot.Output -notmatch 'canonical I:\\FluxKnowledge\\App root') {
    throw "The native deployment plan accepts a non-canonical application root."
}

foreach ($validatorCase in @(
    @{ Script = $outlookValidator; Arguments = @('-PlanOnly') },
    @{ Script = $phase5Validator; Arguments = @('-PlanOnly') },
    @{ Script = $workerValidator; Arguments = @('-ExpectedMigrationId', '20260101000000_AddNativeWorkerSupervision') }
)) {
    $validatorArguments = @('-SourceRoot', $SourceRoot, '-DeployRoot', 'C:\alternate-flux-app') + @($validatorCase.Arguments)
    $validator = Invoke-ExpectedNativeRejection -Script $validatorCase.Script -Arguments $validatorArguments
    if ($validator.ExitCode -eq 0 -or $validator.Output -notmatch 'canonical I:\\FluxKnowledge\\App root') {
        throw "A native deployment validator accepts or inspects a non-canonical application root: $($validatorCase.Script)"
    }
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
$expectedPhase3BMigration = "20260809110000_AddPhase3BWatcherCorpusEvents"
if ((@($plan.phase3b_migration_ids) -join "|") -ne $expectedPhase3BMigration) {
    throw "The native deployment plan does not expose the approved Phase 3B migration."
}

$nativeWorkerMigrationDirectory = Join-Path $SourceRoot "src\FluxKnowledge.Infrastructure.SqlServer\Persistence\Migrations"
$nativeWorkerMigration = @(
    Get-ChildItem -LiteralPath $nativeWorkerMigrationDirectory -File -Filter "*_AddNativeWorkerSupervision.cs" |
        Where-Object { $_.BaseName -match '^\d{14}_AddNativeWorkerSupervision$' }
)
if ($nativeWorkerMigration.Count -ne 1) {
    throw "The generated native-worker supervision migration could not be identified uniquely."
}
$nativeWorkerMigrationId = $nativeWorkerMigration[0].BaseName
if ((@($plan.native_worker_supervision_migration_ids) -join "|") -ne $nativeWorkerMigrationId) {
    throw "The native deployment plan does not require the generated native-worker supervision migration."
}
$expectedOutlookMigrations = @(
    "20260811093501_AddNativeOutlookIngress",
    "20260811094729_HardenNativeOutlookIngress",
    "20260811100247_FixOutlookPrivateIdentityColumns",
    "20260811101550_EnforceOutlookCaptureIdentityFences",
    "20260811105928_HardenOutlookCaptureReplay",
    "20260811112742_BindOutlookExportClaimIdentity",
    "20260811132655_BindOutlookProfileSourceRoot",
    "20260811133300_AlignDeferredCapabilityFingerprintCollation",
    "20260811143122_RecordOutlookExportBlockedReason",
    "20260811152249_AllowIdentitylessBlockedOutlookExports",
    "20260812102333_AddOutlookBrowseTargetPath"
)
if ($expectedOutlookMigrations.Count -eq 0 -or $expectedOutlookMigrations[0] -ne "20260811093501_AddNativeOutlookIngress") {
    throw "The generated Outlook migration sequence does not start with AddNativeOutlookIngress."
}
if ((@($plan.native_outlook_ingress_migration_ids) -join "|") -ne ($expectedOutlookMigrations -join "|")) {
    throw "The native deployment plan does not expose the complete generated Outlook migration sequence."
}
$targetedBrowseMigration = "20260812102333_AddOutlookBrowseTargetPath"
if ($targetedBrowseMigration -notin $expectedOutlookMigrations) {
    throw "The generated targeted Outlook browse migration is missing."
}
if ($targetedBrowseMigration -notin @($plan.native_outlook_ingress_migration_ids)) {
    throw "The native deployment plan does not require the targeted Outlook browse migration."
}
if ($plan.native_outlook_ingress_baseline_migration -ne $expectedOutlookMigrations[0]) {
    throw "The native deployment plan does not identify AddNativeOutlookIngress as the Outlook baseline."
}
if ($plan.native_outlook_ingress_migration_target -ne $expectedOutlookMigrations[-1]) {
    throw "The native deployment plan does not keep the Outlook migration target within the Outlook family."
}
if ($plan.native_outlook_ingress_post_deploy_validator -ne "validate-native-outlook-ingress.ps1") {
    throw "The native deployment plan does not retain the Outlook post-deploy validator."
}

$expectedPhase5Migrations = @(
    "20260813103233_AddRetainedZipProcessorBranches",
    "20260813125157_AddRetainedProcessorBranchMemberChildForeignKeys",
    "20260814144818_AddSourceProcessorForceRequests",
    "20260814161559_AddOperatorActionCapabilityFoundation",
    "20260814162746_EnforceOperatorActionCapabilityInvariants",
    "20260814170852_EnforceOperatorActionRequestPolicies",
    "20260820062157_AddRetainedCsharpCodeFacts",
    "20260820070404_HardenRetainedCsharpLifecycle",
    "20260820101021_CloseRetainedCsharpMixedOutcomes"
)
if ((@($plan.phase5_migration_ids) -join "|") -ne ($expectedPhase5Migrations -join "|")) {
    throw "The native deployment plan does not expose exactly the nine generated Phase 5 migrations."
}
foreach ($migrationId in $expectedPhase5Migrations) {
    if (-not (Test-Path -LiteralPath (Join-Path $nativeWorkerMigrationDirectory "$migrationId.cs") -PathType Leaf)) {
        throw "The Phase 5 deployment migration source is missing: $migrationId."
    }
}
if ($plan.phase5_migration_target -ne $expectedPhase5Migrations[-1] -or
    $plan.deployment_migration_target -ne $expectedPhase5Migrations[-1]) {
    throw "The native deployment target is not pinned to CloseRetainedCsharpMixedOutcomes."
}
if ($plan.post_deploy_validator -ne "validate-phase-5-deployment.ps1") {
    throw "The native deployment plan does not require the Phase 5 post-deploy validator."
}

$validatorOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $outlookValidator -PlanOnly 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "The native Outlook validation plan failed: $validatorOutput"
}
$validatorPlan = $validatorOutput | ConvertFrom-Json
if ($validatorPlan.mode -ne "plan-only" -or -not $validatorPlan.loopback_only -or
    $validatorPlan.outlook_enabled -ne $false -or $validatorPlan.outlook_host_activation -ne $false -or
    -not $validatorPlan.effective_configuration_projection -or $validatorPlan.configuration_projection_starts_host -ne $false) {
    throw "The native Outlook validator lost its disabled loopback-only boundary."
}
if ($validatorPlan.native_outlook_ingress_baseline_migration -ne $plan.native_outlook_ingress_baseline_migration -or
    $validatorPlan.native_outlook_ingress_migration_target -ne $plan.native_outlook_ingress_migration_target) {
    throw "The native Outlook validator does not derive the authoritative deployed Outlook migration contract."
}

function Assert-StaleOutlookMigrationOverrideIsRejected {
    param(
        [string]$ParameterName,
        [string]$MigrationId
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $overrideOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $outlookValidator `
            -SourceRoot $SourceRoot `
            -PlanOnly `
            $ParameterName $MigrationId 2>&1 | Out-String
        $overrideExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($overrideExitCode -eq 0 -or $overrideOutput -notmatch "does not match the authoritative deployment plan") {
        throw "The native Outlook validator accepted a stale $ParameterName override or returned an unsafe failure."
    }
}

Assert-StaleOutlookMigrationOverrideIsRejected `
    -ParameterName "-ExpectedMigrationId" `
    -MigrationId "20260811152249_AllowIdentitylessBlockedOutlookExports"
Assert-StaleOutlookMigrationOverrideIsRejected `
    -ParameterName "-BaselineMigrationId" `
    -MigrationId "20260811094729_HardenNativeOutlookIngress"

$allowedRecordFields = @(
    "started_at_utc", "completed_at_utc", "loopback_status_codes", "migration_ids",
    "outlook_enabled", "aggregate_counts", "private_schema_policy"
)
if ((@($validatorPlan.validation_record_fields) -join "|") -ne ($allowedRecordFields -join "|")) {
    throw "The native Outlook validator record is not restricted to the approved aggregate fields."
}
$privateTerms = @("folder_name", "spool", "store_id", "folder_entry_id", "entry_id", "content", "credential", "diagnostic")
foreach ($term in $privateTerms) {
    if ($term -in @($validatorPlan.validation_record_fields)) {
        throw "The native Outlook validation plan exposes prohibited private field $term."
    }
}
if (-not $plan.source_artifact_store_requires_app_pool_modify_access) {
    throw "The native deployment plan does not require writable and lease-safe retained source storage for the IIS application pool."
}
if (-not $plan.source_artifact_store_acl_rejects_protected_root_overlap) {
    throw "The native deployment plan does not fence retained-source storage permissions from protected roots."
}

$requiredEndpoints = @(
    "/health/live", "/health/ready", "/api/index-health", "/api/gpu-status",
    "POST /api/v1/knowledge/search", "POST /mcp initialize", "POST /mcp tools/list")
foreach ($endpoint in $requiredEndpoints) {
    if ($endpoint -notin @($plan.required_endpoints)) {
        throw "The native deployment plan is missing required endpoint $endpoint."
    }
}

$goLiveModuleInstance = Import-Module $goLiveModule -Force -PassThru
try {
    if ('Invoke-NativeGoLive' -in @($goLiveModuleInstance.ExportedCommands.Keys) -or
        @($goLiveModuleInstance.ExportedCommands.Keys) -notcontains 'Get-NativeGoLivePlan') {
        throw 'The native go-live executor is exported or the read-only plan surface is unavailable.'
    }
}
finally {
    Remove-Module $goLiveModuleInstance -Force -ErrorAction SilentlyContinue
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

if ($deploymentScriptText -match '(?i)\bBackupRoot\b|BACKUP\s+DATABASE|RESTORE\s+(?:VERIFYONLY|DATABASE)|backup_path') {
    throw "The native deployment executable retains a file-copy backup or restore path instead of the VSS-only recovery policy."
}
if ($deploymentScriptText -notmatch 'Native SQL migration is unavailable until the guarded VSS go-live workflow is authorised') {
    throw "The native deployment executable does not refuse non-disposable migration while go-live execution is unavailable."
}
foreach ($probeScript in @($workerValidator, $outlookValidator, $phase5Validator)) {
    $probeScriptText = Get-Content -LiteralPath $probeScript -Raw
    if ($probeScriptText -notmatch '\bInvoke-FixedLoopbackProbe\b' -or
        $probeScriptText -match '\bInvoke-WebRequest\b') {
        throw "A native deployment probe bypasses the shared no-proxy/no-redirect fixed-loopback helper: $probeScript"
    }
}
$loopbackProbeHelperText = Get-Content -LiteralPath $loopbackProbeHelper -Raw
if ($loopbackProbeHelperText -notmatch '\bInvoke-FixedLoopbackProbe\b' -or
    $loopbackProbeHelperText -notmatch 'UseProxy\s*=\s*\$false' -or
    $loopbackProbeHelperText -notmatch 'AllowAutoRedirect\s*=\s*\$false') {
    throw "The shared native health-probe helper does not prohibit proxy use and redirects."
}
foreach ($activationCommand in @(
    'Register-ScheduledTask', 'Enable-ScheduledTask', 'Start-ScheduledTask',
    'Register-OutlookHostTask', 'Install-OutlookHostTask')) {
    if ($deploymentScriptText -match ("\b{0}\b" -f [regex]::Escape($activationCommand))) {
        throw "The native deployment executable retains an Outlook activation command: $activationCommand"
    }
}
$goLiveModuleText = Get-Content -LiteralPath $goLiveModule -Raw
$guardedHostText = Get-Content -LiteralPath (Join-Path $sourceRoot 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\GuardedNativeGoLiveHost.cs') -Raw
if ($guardedHostText -notmatch 'NativeGoLiveIisObservation' -or
    $guardedHostText -notmatch 'AnonymousAuthentication' -or
    $guardedHostText -notmatch 'WindowsAuthentication' -or
    $guardedHostText -notmatch '127\.0\.0\.1') {
    throw "The private native go-live module does not enforce the fixed IIS authentication contract."
}
if ($goLiveModuleText -match '\bGet-FileHash\b') {
    throw "The private native go-live module uses the unavailable Windows PowerShell hash cmdlet."
}

$bootstrapManifestOutput = & pwsh -NoProfile -File $bootstrapManifestTest -SourceRoot $SourceRoot 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "The reviewed native go-live SQL bootstrap manifest is not reproducible: $bootstrapManifestOutput"
}

Write-Output "Native deployment plan contract passed."
