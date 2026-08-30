[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string]$SiteName = "FluxKnowledge",
    [string]$SiteUrl = "http://127.0.0.1:5137",
    [string]$DeployRoot = "I:\FluxKnowledge\App",
    [switch]$ApplyMigrations,
    [switch]$ConfirmApplyMigrations,
    [switch]$KeepOutlookHostDisabled = $true,
    [int]$ReadinessTimeoutSeconds = 120,
    [switch]$PlanOnly,
    [switch]$PreflightOnly,
    [switch]$GoLive,
    [switch]$ConfirmCleanSlate,
    [switch]$ConfirmConfigureVss,
    [switch]$ConfirmDestroySql,
    [switch]$ConfirmRegisterCodex
)

$ErrorActionPreference = "Stop"
$CanonicalLiveRoot = "I:\FluxKnowledge"
$CanonicalDeployRoot = "$CanonicalLiveRoot\App"
$CanonicalConfigRoot = "$CanonicalLiveRoot\Config"
$CanonicalSqlDataFile = "$CanonicalLiveRoot\Data\Sql\Data\FluxKnowledge.mdf"
$CanonicalSqlLogFile = "$CanonicalLiveRoot\Data\Sql\Log\FluxKnowledge_log.ldf"
$CanonicalIndexRoot = "$CanonicalLiveRoot\Data\Index"
$CanonicalRetainedRoot = "$CanonicalLiveRoot\Data\Retained"
$CanonicalSpoolRoot = "$CanonicalLiveRoot\Runtime\Spool"
$CanonicalTempRoot = "$CanonicalLiveRoot\Runtime\Temp"
$CanonicalLogsRoot = "$CanonicalLiveRoot\Runtime\Logs"
$CanonicalCodexPluginRoot = "$CanonicalLiveRoot\CodexPlugin"
$CanonicalRecoveryRoot = "$CanonicalLiveRoot\Recovery"

$deploySegments = @($DeployRoot -split '[\\/]')
if ($deploySegments -contains '.' -or $deploySegments -contains '..') {
    throw "Native deployment requires the canonical I:\FluxKnowledge\App root without traversal."
}
try {
    $canonicalRequestedDeployRoot = [IO.Path]::GetFullPath($DeployRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}
catch {
    throw "Native deployment requires the canonical I:\FluxKnowledge\App root."
}
if (-not [string]::Equals(
        $canonicalRequestedDeployRoot,
        $CanonicalDeployRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Native deployment requires the canonical I:\FluxKnowledge\App root."
}

. (Join-Path $PSScriptRoot 'loopback-deployment-safety.ps1')
[void](Get-FixedLoopbackOrigin -SiteUrl $SiteUrl)
if (-not $KeepOutlookHostDisabled) {
    throw "Outlook host activation is not authorised; KeepOutlookHostDisabled must remain true."
}
if ($PreflightOnly -and $PlanOnly) {
    throw "-PreflightOnly cannot be combined with -PlanOnly."
}
if ($GoLive -and ($PlanOnly -or $PreflightOnly)) {
    throw "-GoLive cannot be combined with preparation-only modes."
}
if ($ConfirmApplyMigrations -and -not $ApplyMigrations) {
    throw "-ConfirmApplyMigrations requires -ApplyMigrations."
}
if ($ApplyMigrations -and -not $ConfirmApplyMigrations) {
    throw "-ApplyMigrations requires -ConfirmApplyMigrations."
}
if ($ApplyMigrations) {
    throw "Native SQL migration is unavailable until the guarded VSS go-live workflow is authorised."
}
if ($ReadinessTimeoutSeconds -lt 10) {
    throw "Readiness timeout must be at least 10 seconds."
}
if ($SiteName -cne 'FluxKnowledge') {
    throw "Native deployment is restricted to the fixed FluxKnowledge IIS site."
}

if ($GoLive) {
    throw "Direct -GoLive execution is refused because it has no claimed in-process authority; use the authorised closeout process."
}

$SchedulerMigrationTargetId = '20260805112341_AddGpuExecutorDispatchAndReceipts'
$SchedulerMigrationIds = @(
    '20260729080641_AddGpuSchedulerDurability',
    '20260729094809_AddGpuSchedulerOperationReceipts',
    '20260729103104_CompleteGpuSchedulerOperationReceipts',
    '20260729120305_AddGpuSchedulerOperationReceiptRequestFingerprint',
    '20260802182703_AddGpuSchedulerBinaryFenceCollation',
    '20260802191240_AddGpuSchedulerOpaqueKeyCanonicality',
    $SchedulerMigrationTargetId
)
$Phase3AMigrationTargetId = '20260808191700_AddRetainedTextPipelineLink'
$Phase3AMigrationIds = @('20260806120000_AddPhase3ALocalSources', $Phase3AMigrationTargetId)
$Phase3BMigrationIds = @('20260809110000_AddPhase3BWatcherCorpusEvents')
$NativeWorkerSupervisionMigrationIds = @('20260810185641_AddNativeWorkerSupervision')
$NativeOutlookIngressBaselineMigrationId = '20260811093501_AddNativeOutlookIngress'
$NativeOutlookIngressMigrationIds = @(
    $NativeOutlookIngressBaselineMigrationId,
    '20260811094729_HardenNativeOutlookIngress',
    '20260811100247_FixOutlookPrivateIdentityColumns',
    '20260811101550_EnforceOutlookCaptureIdentityFences',
    '20260811105928_HardenOutlookCaptureReplay',
    '20260811112742_BindOutlookExportClaimIdentity',
    '20260811132655_BindOutlookProfileSourceRoot',
    '20260811133300_AlignDeferredCapabilityFingerprintCollation',
    '20260811143122_RecordOutlookExportBlockedReason',
    '20260811152249_AllowIdentitylessBlockedOutlookExports',
    '20260812102333_AddOutlookBrowseTargetPath'
)
$Phase5MigrationTargetId = '20260820101021_CloseRetainedCsharpMixedOutcomes'
$Phase5MigrationIds = @(
    '20260813103233_AddRetainedZipProcessorBranches',
    '20260813125157_AddRetainedProcessorBranchMemberChildForeignKeys',
    '20260814144818_AddSourceProcessorForceRequests',
    '20260814161559_AddOperatorActionCapabilityFoundation',
    '20260814162746_EnforceOperatorActionCapabilityInvariants',
    '20260814170852_EnforceOperatorActionRequestPolicies',
    '20260820062157_AddRetainedCsharpCodeFacts',
    '20260820070404_HardenRetainedCsharpLifecycle',
    $Phase5MigrationTargetId
)
$RequiredBaselineMigrationIds = @(
    '20260726215521_InitialPhase1',
    '20260726221653_EnforceCanonicalSqlSafety',
    '20260726235718_AddIndexGenerationMembership',
    '20260727055755_DistinguishVectorIdentityAndPayloadChecksum'
)

if ($PlanOnly) {
    [ordered]@{
        mode = 'plan-only'
        executionAvailable = $false
        root = $CanonicalLiveRoot
        siteName = 'FluxKnowledge'
        loopbackPort = 5137
        vss = [ordered]@{ volume = 'I:'; maximumStorageFraction = [decimal]0.10 }
        validation = [ordered]@{
            baseUri = 'http://127.0.0.1:5137'
            mcpTools = @(
                'knowledge.search', 'knowledge.write', 'knowledge.graph', 'code.query', 'code.write',
                'corpus.query', 'corpus.write', 'operations.status', 'operations.audit')
        }
        required_site = 'FluxKnowledge'
        loopback_only = $true
        requires_explicit_migration_confirmation = $true
        execution_available = $false
        migration_update_available = $false
        live_root = $CanonicalLiveRoot
        application_root = $CanonicalDeployRoot
        config_root = $CanonicalConfigRoot
        sql_data_file = $CanonicalSqlDataFile
        sql_log_file = $CanonicalSqlLogFile
        index_root = $CanonicalIndexRoot
        retained_root = $CanonicalRetainedRoot
        spool_root = $CanonicalSpoolRoot
        temp_root = $CanonicalTempRoot
        logs_root = $CanonicalLogsRoot
        codex_plugin_root = $CanonicalCodexPluginRoot
        recovery_root = $CanonicalRecoveryRoot
        required_baseline_migration_ids = $RequiredBaselineMigrationIds
        scheduler_migration_ids = $SchedulerMigrationIds
        scheduler_migration_target = $SchedulerMigrationTargetId
        phase3a_migration_ids = $Phase3AMigrationIds
        phase3b_migration_ids = $Phase3BMigrationIds
        native_worker_supervision_migration_ids = $NativeWorkerSupervisionMigrationIds
        native_outlook_ingress_migration_ids = $NativeOutlookIngressMigrationIds
        native_outlook_ingress_baseline_migration = $NativeOutlookIngressBaselineMigrationId
        native_outlook_ingress_migration_target = $NativeOutlookIngressMigrationIds[-1]
        native_outlook_ingress_post_deploy_validator = 'validate-native-outlook-ingress.ps1'
        phase5_migration_ids = $Phase5MigrationIds
        phase5_migration_target = $Phase5MigrationTargetId
        deployment_migration_target = $Phase5MigrationTargetId
        post_deploy_validator = 'validate-phase-5-deployment.ps1'
        keep_outlook_host_disabled = $true
        outlook_host_activation = $false
        outlook_host_payload = [ordered]@{ published = $true; relative_directory = 'outlook-host' }
        outlook_host_scheduler = [ordered]@{
            task_name = 'FluxKnowledge.OutlookHost'
            interactive_only = $true
            hidden = $true
            interval_minutes = 15
            multiple_instances = 'IgnoreNew'
            execution_limit_minutes = 14
            verbose_diagnostics = $false
        }
        windows_service_registration = $false
        iis_anonymous_authentication_required = $true
        iis_windows_authentication_prohibited = $true
        source_artifact_store_requires_app_pool_modify_access = $true
        source_artifact_store_acl_rejects_protected_root_overlap = $true
        required_endpoints = @(
            '/health/live', '/health/ready', '/api/index-health', '/api/gpu-status',
            'POST /api/v1/knowledge/search', 'POST /mcp initialize', 'POST /mcp tools/list')
        prohibited_components = @('python', 'docker', 'rabbitmq', 'vespa', 'model', 'gpu-runtime')
    } | ConvertTo-Json -Depth 5
    exit 0
}

throw "Native deployment execution is unavailable until guarded go-live authority is implemented."
