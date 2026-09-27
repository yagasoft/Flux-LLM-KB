[CmdletBinding()]
param([string]$SourceRoot='', [string]$Mode='replacement')
$ErrorActionPreference='Stop'
if (-not $SourceRoot) { $SourceRoot=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) }
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Updater parse failed.' }
foreach ($name in @('Invoke-HybridPassageIisUpdate','Assert-NotReparsePoint','New-DeploymentValidationHold','Remove-DeploymentValidationHold')) {
    $definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if ($definition.Count -ne 1) { throw 'Required updater function missing.' }
    . ([scriptblock]::Create($definition[0].Extent.Text))
}
function Get-DeploymentSqlConnectionString { 'Data Source=localhost;Initial Catalog=FluxKnowledge_DisposableReplacement;Integrated Security=True;TrustServerCertificate=True' }
function Get-AppliedMigrationIds { @($contract.Baseline)+@($contract.Suffix) }
function Assert-HybridReplacementPacket {
    param($RecoveryRoot,$Binding)
    if ((Get-FileHash (Join-Path $RecoveryRoot "$($Binding.ReleaseId)/hybrid-state.json")).Hash -cne $Binding.JournalHash) { throw 'Changed predecessor.' }
}
function Get-HybridReplacementSqlReceipt {
    param($ConnectionString,[Guid]$OperationId)
    if (-not $evidence.Committed) { return @{ Committed=$false } }
    return @{ Committed=$true; OperationId=$OperationId.ToString('D'); ManifestHash=('b'*64); TargetEpoch=$nextEpoch; CorpusEpoch=$nextEpoch
        ActiveOperationId=$OperationId.ToString('D'); SupersedesOperationId=$(if ($evidence.Mode -eq 'wrong-receipt') {[Guid]::NewGuid().ToString('D')} else {$oldOperation}) }
}
function Invoke-HybridRebuildOperator {
    param($OperatorRoot,$ConnectionString,$Arguments)
    switch ($Arguments[0]) {
        'verify-models' { @{ Verified=$true; Offline=$true } }
        'status' { @{ Committed=$evidence.Committed; Completed=$evidence.Finished; ManifestHash=('b'*64); TargetEpoch=$nextEpoch;
            PendingItems=0; RunningItems=0; FailedEmbeddingJobs=0; FailedPublishJobs=0 } }
        'plan' { @{ OperationId=$nextOperation; TargetEpoch=$nextEpoch; ManifestHash=('b'*64);
            Supersession=@{ OperationId=$oldOperation; TargetEpoch=$oldEpoch; ManifestHash=('a'*64) } } }
        'commit' {
            if ($evidence.Mode -eq 'before-commit') { throw 'injected-before-commit' }
            $evidence.Committed=$true
            if ($evidence.Mode -eq 'response-loss') { throw 'injected-commit-response-loss' }
            @{ OperationId=$nextOperation; ManifestHash=('b'*64); TargetEpoch=$nextEpoch }
        }
        'prepare' { @{ Prepared=$true } }
        'finish' { $evidence.Finished=$true; @{ Completed=$true } }
        default { throw 'Unexpected operator command.' }
    }
}
function Get-WebAppPoolState { param($Name) @{ Value=$evidence.PoolState } }
function Start-WebAppPool { param($Name) $evidence.PoolState='Started'; $evidence.Starts++ }
function Wait-IisAppPoolState { param($Name,$ExpectedState,$TimeoutSeconds) }
function Disable-ScheduledTask { param($TaskName) }
function Enable-ScheduledTask { param($TaskName) $evidence.Enables++ }
function Wait-InteractiveHostStopped { param($TaskName,$TimeoutSeconds) }
function Stop-HybridIisAfterGpuDrain { param($OnStopRequested) & $OnStopRequested; $evidence.PoolState='Stopped' }
function Activate-HybridCompatiblePayload { param($CandidateRoot,$PreviousRoot,$ReleaseRoot,$ConfigurationCandidate,$ConfigurationPath,$State) }
function Invoke-FixedLoopbackProbe {
    param($Uri,$TimeoutSeconds)
    if ($evidence.Mode -eq 'after-activation') { throw 'injected-after-activation' }
    [IO.MemoryStream]::new()
}
function Invoke-RequiredLoopbackProbes { param($Origin,$TimeoutSeconds) }
function Get-HybridPreservedInputFingerprint { 'preserved-inputs' }
function Invoke-GeneratedSqlScript { param($Path) }

$parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$temporary=[IO.Path]::GetFullPath((Join-Path $parent ('HybridReplacement-'+[Guid]::NewGuid().ToString('N'))))
if (-not $temporary.StartsWith($parent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary root.' }
try {
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $contract=Get-HybridPassageMigrationContract
    $schema=Join-Path $temporary 'schema.sql'
    & dotnet ef migrations script $contract.Baseline $contract.Target --idempotent --configuration Release `
        --project (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer') `
        --startup-project (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer') --no-build --output $schema | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Schema generation failed.' }
    foreach ($case in @('before-commit','after-activation','response-loss','wrong-receipt','success-enabled','success-disabled')) {
        $CanonicalLiveRoot=Join-Path $temporary $case; $CanonicalDeployRoot=Join-Path $CanonicalLiveRoot 'App'
        $IncrementalRecoveryRoot=Join-Path $CanonicalLiveRoot 'Recovery/IncrementalUpdates'
        $ValidationHoldPath=Join-Path $CanonicalLiveRoot 'Runtime/deployment-validation-hold.json'
        $InteractiveHostTaskName='Synthetic'; $SiteName='Synthetic'; $ReadinessTimeoutSeconds=30; $RebuildTimeoutSeconds=30
        $loopbackOrigin=@{ Origin='http://127.0.0.1:5137' }
        $oldRelease='20260927T000000Z-aaaaaaaaaaaa-hybrid'; $nextRelease='20260927T000001Z-bbbbbbbbbbbb-hybrid'
        $oldOperation=[Guid]::NewGuid().ToString('D'); $oldEpoch=[Guid]::NewGuid().ToString('D')
        $nextOperation=[Guid]::NewGuid().ToString('D'); $nextEpoch=[Guid]::NewGuid().ToString('D')
        $releaseRoot=Join-Path $IncrementalRecoveryRoot $nextRelease
        foreach ($path in @($CanonicalDeployRoot,(Join-Path $CanonicalLiveRoot 'Config'),(Split-Path $ValidationHoldPath -Parent),
            $releaseRoot,(Join-Path $releaseRoot 'candidate'),(Join-Path $releaseRoot 'operator'),(Join-Path $IncrementalRecoveryRoot $oldRelease))) {
            New-Item -ItemType Directory -Path $path -Force | Out-Null
        }
        foreach ($payload in @('candidate','operator')) { [IO.File]::WriteAllText((Join-Path $releaseRoot "$payload/marker"),'synthetic') }
        $oldJournal=Join-Path $IncrementalRecoveryRoot "$oldRelease/hybrid-state.json"
        Write-HybridRebuildJson -Path $oldJournal -Value @{ Immutable='predecessor' }
        $oldHash=(Get-FileHash $oldJournal).Hash
        $config=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
        Write-HybridRebuildJson -Path $config -Value @{ Runtime=@{} }
        Copy-Item -LiteralPath $config -Destination (Join-Path $releaseRoot 'candidate-config.json')
        Copy-Item -LiteralPath $schema -Destination (Join-Path $releaseRoot 'hybrid-idempotent-up.sql')
        $state=@{
            Version=1; ReleaseId=$nextRelease; Commit=('b'*40); OperationId=$nextOperation; DatabaseServer='localhost'; DatabaseName='FluxKnowledge_DisposableReplacement'
            OriginalHistory=@($contract.Baseline); SchemaAttempted=$true; ResetCommitted=$false; HoldReleased=$false; HoldReleaseAttempted=$false
            CandidateHash=(Get-HybridPayloadFingerprint (Join-Path $releaseRoot 'candidate')); OperatorHash=(Get-HybridPayloadFingerprint (Join-Path $releaseRoot 'operator'))
            OriginalConfigHash=(Get-FileHash $config).Hash; ActivatedConfigHash=(Get-FileHash $config).Hash; PreservedInputFingerprint='preserved-inputs'
            InteractiveHostWasEnabled=($case -ne 'success-disabled')
            Replacement=@{ ReleaseId=$oldRelease; OperationId=$oldOperation; TargetEpoch=$oldEpoch; ManifestHash=('a'*64); JournalHash=$oldHash }
        }
        Write-HybridRebuildJson -Path (Join-Path $releaseRoot 'hybrid-state.json') -Value $state
        Write-HybridRebuildJson -Path $ValidationHoldPath -Value $oldRelease
        $evidence=@{ Mode=$case; Committed=$false; Finished=$false; PoolState='Started'; Starts=0; Enables=0 }
        $failure=$null
        try { Invoke-HybridPassageIisUpdate -SourceRoot $SourceRoot -Commit ('b'*40) -ResumeRelease $nextRelease | Out-Null }
        catch { $failure=$_.Exception.Message }
        if ($case -in @('before-commit','after-activation','wrong-receipt')) {
            if (-not $failure) { throw "Expected failure: $case" }
            $expectedHold=if ($case -eq 'after-activation') { $nextRelease } else { $oldRelease }
            $hold=Get-Content $ValidationHoldPath -Raw | ConvertFrom-Json
            if ($hold -isnot [string] -or $hold -cne $expectedHold) { throw "Failure left wrong/permissive hold: $case ($failure)" }
            if ($evidence.Enables) { throw 'Failed recovery enabled intake.' }
        }
        else {
            if ($failure) { throw $failure }
            if (Test-Path $ValidationHoldPath) { throw 'Verified successor retained hold.' }
            $expectedEnables=if ($case -eq 'success-disabled') { 0 } else { 1 }
            if ($evidence.Enables -ne $expectedEnables) { throw 'Predecessor task preference was lost.' }
        }
        if ((Get-FileHash $oldJournal).Hash -cne $oldHash) { throw 'Recovery changed predecessor journal.' }
        if ($case -eq 'after-activation') {
            $evidence.Mode='response-loss'
            Invoke-HybridPassageIisUpdate -SourceRoot $SourceRoot -Commit ('b'*40) -ResumeRelease $nextRelease | Out-Null
            if (Test-Path $ValidationHoldPath) { throw 'Committed successor did not resume safely.' }
        }
    }
    Write-Output 'Replacement updater contract passed: pre-commit/post-activation failure, response loss, wrong receipt, replay, immutable predecessor and both intake preferences.'
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force } }
