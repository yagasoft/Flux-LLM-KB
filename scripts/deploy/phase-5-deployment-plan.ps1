[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string]$SiteUrl = "http://127.0.0.1:5137",
    [switch]$PlanOnly
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "loopback-deployment-safety.ps1")
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$siteOrigin = (Get-FixedLoopbackOrigin -SiteUrl $SiteUrl).Origin
if (-not $PlanOnly) {
    throw "phase-5-deployment-plan.ps1 is a planning-only command; specify -PlanOnly."
}

$deploymentScript = Join-Path $SourceRoot "scripts\deploy\update-native-windows.ps1"
if (-not (Test-Path -LiteralPath $deploymentScript -PathType Leaf)) {
    throw "The authoritative native deployment script is missing."
}
$deploymentOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $deploymentScript `
    -SourceRoot $SourceRoot `
    -SiteUrl $siteOrigin `
    -PlanOnly 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "The authoritative native deployment plan could not be read."
}
try {
    $deploymentPlan = $deploymentOutput | ConvertFrom-Json
    $phase5MigrationIds = @($deploymentPlan.phase5_migration_ids)
}
catch {
    throw "The authoritative native deployment plan returned invalid JSON."
}
$expectedPhase5MigrationIds = @(
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
if (($phase5MigrationIds -join "|") -cne ($expectedPhase5MigrationIds -join "|") -or
    [string]$deploymentPlan.phase5_migration_target -cne $expectedPhase5MigrationIds[-1] -or
    [string]$deploymentPlan.deployment_migration_target -cne $expectedPhase5MigrationIds[-1] -or
    -not [bool]$deploymentPlan.keep_outlook_host_disabled -or
    [bool]$deploymentPlan.outlook_host_activation) {
    throw "The authoritative native deployment plan does not match the approved Phase 5 contract."
}

[ordered]@{
    mode = "plan-only"
    loopback_only = $true
    read_only_validation = $true
    site_origin = $siteOrigin
    phase5_migration_ids = $expectedPhase5MigrationIds
    phase5_migration_target = $expectedPhase5MigrationIds[-1]
    required_schema_objects = @(
        "SourceActivityRelations",
        "SourceProcessorBranches",
        "SourceProcessorAttempts",
        "SourceProcessorBranchMembers",
        "SourceProcessorForceRequests",
        "OperatorActionCapabilityPolicies",
        "OperatorActionHardDenials",
        "SourceProcessorCodeDocuments",
        "SourceProcessorCodeCompletionReceipts",
        "SourceProcessorCodeSymbols",
        "SourceProcessorCodeReferences",
        "SourceProcessorCodeDiagnostics",
        "SourceProcessorCodeBlockedDiagnostics"
    )
    required_schema_triggers = @(
        "TR_OperatorActionCapabilityPolicies_Immutable",
        "TR_OperatorActionHardDenials_Immutable",
        "TR_SourceProcessorCodeCompletionReceipts_Closure",
        "TR_SourceProcessorCodeCompletionReceipts_OutcomeFence",
        "TR_SourceProcessorCodeDocuments_Immutable",
        "TR_SourceProcessorCodeDocuments_InsertFence",
        "TR_SourceProcessorCodeBlockedDiagnostics_InsertFence"
    )
    required_schema_trigger_bindings = @(
        [ordered]@{ name = "TR_OperatorActionCapabilityPolicies_Immutable"; parent_schema = "dbo"; parent_table = "OperatorActionCapabilityPolicies" },
        [ordered]@{ name = "TR_OperatorActionHardDenials_Immutable"; parent_schema = "dbo"; parent_table = "OperatorActionHardDenials" },
        [ordered]@{ name = "TR_SourceProcessorCodeCompletionReceipts_Closure"; parent_schema = "dbo"; parent_table = "SourceProcessorCodeCompletionReceipts" },
        [ordered]@{ name = "TR_SourceProcessorCodeCompletionReceipts_OutcomeFence"; parent_schema = "dbo"; parent_table = "SourceProcessorCodeCompletionReceipts" },
        [ordered]@{ name = "TR_SourceProcessorCodeDocuments_Immutable"; parent_schema = "dbo"; parent_table = "SourceProcessorCodeDocuments" },
        [ordered]@{ name = "TR_SourceProcessorCodeDocuments_InsertFence"; parent_schema = "dbo"; parent_table = "SourceProcessorCodeDocuments" },
        [ordered]@{ name = "TR_SourceProcessorCodeBlockedDiagnostics_InsertFence"; parent_schema = "dbo"; parent_table = "SourceProcessorCodeBlockedDiagnostics" }
    )
    direct_get_endpoints = @(
        "/operator-actions",
        "/api/operator-actions",
        "/search/csharp-code",
        "/api/local/retained-csharp-code?query={no-match-token}"
    )
    retained_csharp_search_requires_no_match = $true
    forwarded_proxy_headers = @(
        "Forwarded",
        "Forwarded-For",
        "X-Forwarded-For",
        "X-Original-URL",
        "Proxy-Connection",
        "X-ProxyUser-IP",
        "X-Real-IP",
        "Via",
        "True-Client-IP",
        "CF-Connecting-IP"
    )
    prohibited_operations = @(
        "POST",
        "PUT",
        "PATCH",
        "DELETE",
        "source-original-read",
        "outlook",
        "model",
        "runtime-activation"
    )
    outlook_host_activation = $false
    validation_record_fields = @(
        "validated_at_utc",
        "migration_ids",
        "schema_contract",
        "direct_get_status_codes",
        "forwarded_proxy_status_codes",
        "outlook_host_activation"
    )
} | ConvertTo-Json -Depth 7
