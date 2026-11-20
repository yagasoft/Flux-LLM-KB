$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$installer = Join-Path $root 'scripts/deploy/install-source-journal-allowlist-operator.ps1'
$binding = @{
    StageName = 'JournalFixture'
    DashboardPort = 56137
    OperatorPort = 56138
    OperatorSid = @('S-1-5-21-1001-1002-1003-1004')
    DatabaseIdentity = 'localhost|FluxKnowledge_Staging_JournalFixture|10000000-0000-0000-0000-000000000001'
    WebSha256 = 'A' * 64
    OutlookSha256 = 'B' * 64
    HelperSha256 = 'C' * 64
    TaskDefinitionSha256 = 'D' * 64
    TaskSecuritySha256 = 'E' * 64
    JournalPipeName = 'FluxKnowledge.SourceJournal.Staging-JournalFixture'
    StageVolumeIds = @('\\?\VOLUME{10000000-0000-0000-0000-000000000001}\', '\\?\VOLUME{10000000-0000-0000-0000-000000000002}\')
}
$plan = & $installer @binding -PlanOnly | ConvertFrom-Json
if ($plan.Mode -cne 'plan-only' -or $plan.TaskName -cne 'FluxKnowledge.SourceJournalOperator-Staging-JournalFixture' -or
    $plan.Binding.ReceiptDirectory -cne 'I:\FluxKnowledgeSourceJournalOperator-Staging-JournalFixture\Receipts' -or
    $plan.PlanSha256 -cnotmatch '^[A-F0-9]{64}$' -or $plan.Binding.OperatorOrigin -cne 'http://localhost:56138') {
    throw 'Staging installation plan is not bound to the exact distinct operator targets'
}
foreach ($schema in 'outlook-watched-source-schema.sql', 'codex-workspace-notes-schema.sql',
    'media-transcription-gpu-schema.sql', 'gpu-model-residency-schema.sql') {
    $path = Join-Path (Split-Path -Parent $installer) $schema
    if ($plan.Package.$schema -cne (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) {
        throw "Operator package does not pin the schema check for $schema"
    }
}
foreach ($override in @(@{DashboardPort=5137}, @{OperatorPort=56137}, @{OperatorSid=@('S-1-5-18')},
    @{DatabaseIdentity='localhost|FluxKnowledge|10000000-0000-0000-0000-000000000001'}, @{JournalPipeName='FluxKnowledge.SourceJournal.Main'})) {
    $invalid = $binding.Clone()
    foreach ($key in $override.Keys) { $invalid[$key] = $override[$key] }
    $rejected = $false
    try { $null = & $installer @invalid -PlanOnly } catch { $rejected = $true }
    if (!$rejected) { throw "Unscoped operator installation plan was accepted for $($override.Keys -join ',')" }
}
'isolated operator installer plan passed'
