[CmdletBinding()]
param([string]$SourceRoot='', [string]$MigrationScript='')
$ErrorActionPreference='Stop'
if (-not $SourceRoot) { $SourceRoot=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) }
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Updater parse failed.' }
foreach ($name in @('Invoke-HybridPassageIisUpdate','Assert-NotReparsePoint','New-DeploymentValidationHold','Remove-DeploymentValidationHold')) {
    $definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if ($definition.Count -ne 1) { throw 'Required updater function missing.' }
    . ([scriptblock]::Create($definition[0].Extent.Text))
}
function Get-DeploymentSqlConnectionString { 'Data Source=localhost;Initial Catalog=FluxKnowledge_DisposableResumeContract;Integrated Security=True;TrustServerCertificate=True;ConnectRetryCount=0' }
function Get-AppliedMigrationIds { if ($evidence.Mode -eq 'foreign') { @($contract.Baseline) } else { @($contract.Baseline)+@($contract.Suffix) } }
function Invoke-HybridRebuildOperator {
    param($OperatorRoot,$ConnectionString,$Arguments)
    switch ($Arguments[0]) {
        'verify-models' { @{ Verified=$true; Offline=$true } }
        'status' { @{ Committed=$true; Completed=$evidence.Finished; ManifestHash=('a'*64); PendingItems=0; RunningItems=0; FailedEmbeddingJobs=0; FailedPublishJobs=0 } }
        'prepare' { $evidence.Events.Add('prepare'); @{ Prepared=$true } }
        'finish' { $evidence.Finished=$true; $evidence.Events.Add('finish'); @{} }
        default { throw 'Unexpected operator action.' }
    }
}
function Disable-ScheduledTask { param($TaskName) $evidence.Disables++ }
function Enable-ScheduledTask { param($TaskName) $evidence.Enables++ }
function Wait-InteractiveHostStopped { param($TaskName,$TimeoutSeconds) }
function Get-WebAppPoolState { param($Name) @{ Value=$evidence.PoolState } }
function Start-WebAppPool {
    param($Name)
    $evidence.Starts++
    $hold=Get-Content -LiteralPath $ValidationHoldPath -Raw | ConvertFrom-Json
    if ($hold -is [string]) { $evidence.Events.Add('start-denied-recovery'); $evidence.StaleReservation=$false }
    else { $evidence.Events.Add('start-permitted') }
    $evidence.PoolState='Started'
}
function Wait-IisAppPoolState { param($Name,$ExpectedState,$TimeoutSeconds) if ($evidence.PoolState -cne $ExpectedState) { throw 'Fake pool state mismatch.' } }
function Stop-HybridIisAfterGpuDrain {
    $evidence.Drains++
    if ($evidence.StaleReservation) { throw 'Drain attempted before compatible held recovery.' }
    $evidence.Events.Add('drain'); $evidence.PoolState='Stopped'
}
function Activate-HybridCompatiblePayload { param($CandidateRoot,$PreviousRoot,$ReleaseRoot,$ConfigurationCandidate,$ConfigurationPath,$State) $evidence.Activations++; $evidence.Events.Add('activate') }
function Invoke-FixedLoopbackProbe { param($Uri,$TimeoutSeconds) [IO.MemoryStream]::new() }
function Invoke-RequiredLoopbackProbes { param($Origin,$TimeoutSeconds) $evidence.Events.Add('validate') }
function Get-HybridPreservedInputFingerprint { 'synthetic-preserved-input-fingerprint' }
function Invoke-GeneratedSqlScript { param($Path) $evidence.Schemas++; $evidence.Events.Add('schema') }

$temporaryParent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$temporaryRoot=[IO.Path]::GetFullPath((Join-Path $temporaryParent ('HybridResumeContract-'+[Guid]::NewGuid().ToString('N'))))
if (-not $temporaryRoot.StartsWith($temporaryParent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root.' }
try {
    if (-not $MigrationScript) {
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        $MigrationScript=Join-Path $temporaryRoot 'reviewed-migration.sql'
        $contract=Get-HybridPassageMigrationContract
        & dotnet ef migrations script $contract.Baseline $contract.Target --idempotent --configuration Release `
            --project (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer') `
            --startup-project (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer') --no-build --output $MigrationScript | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Test migration generation failed.' }
    }
    foreach ($mode in @('foreign','released-foreign','unjournalled-schema','recovery')) {
        $CanonicalLiveRoot=Join-Path $temporaryRoot $mode
        $CanonicalDeployRoot=Join-Path $CanonicalLiveRoot 'App'
        $IncrementalRecoveryRoot=Join-Path $CanonicalLiveRoot 'Recovery/IncrementalUpdates'
        $ValidationHoldPath=Join-Path $CanonicalLiveRoot 'Runtime/deployment-validation-hold.json'
        $InteractiveHostTaskName='SyntheticTask'; $SiteName='SyntheticSite'; $ReadinessTimeoutSeconds=30; $RebuildTimeoutSeconds=30
        $loopbackOrigin=@{ Origin='http://127.0.0.1:5137' }
        $contract=Get-HybridPassageMigrationContract
        $releaseId='20260927T000000Z-aaaaaaaaaaaa-hybrid'; $releaseRoot=Join-Path $IncrementalRecoveryRoot $releaseId
        foreach ($path in @($CanonicalDeployRoot,(Join-Path $CanonicalLiveRoot 'Config'),(Split-Path $ValidationHoldPath -Parent),$releaseRoot,(Join-Path $releaseRoot 'candidate'),(Join-Path $releaseRoot 'operator'))) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
        foreach ($payload in @('candidate','operator')) { [IO.File]::WriteAllText((Join-Path $releaseRoot "$payload/marker.txt"),'synthetic') }
        $configPath=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
        Write-HybridRebuildJson -Path $configPath -Value @{ Runtime=@{} }
        Copy-Item -LiteralPath $configPath -Destination (Join-Path $releaseRoot 'candidate-config.json')
        Copy-Item -LiteralPath $MigrationScript -Destination (Join-Path $releaseRoot 'hybrid-idempotent-up.sql')
        $state=@{
            Version=1; ReleaseId=$releaseId; Commit=('a'*40); OperationId=[Guid]::NewGuid().ToString('D')
            DatabaseServer='localhost'; DatabaseName='FluxKnowledge_DisposableResumeContract'; OriginalHistory=@($contract.Baseline)
            SchemaAttempted=($mode -in @('recovery','released-foreign')); ResetCommitted=($mode -in @('recovery','released-foreign'))
            HoldReleased=($mode -eq 'released-foreign'); HoldReleaseAttempted=($mode -eq 'released-foreign')
            CandidateHash=(Get-HybridPayloadFingerprint (Join-Path $releaseRoot 'candidate')); OperatorHash=(Get-HybridPayloadFingerprint (Join-Path $releaseRoot 'operator'))
            OriginalConfigHash=(Get-FileHash $configPath).Hash; ActivatedConfigHash=(Get-FileHash $configPath).Hash
            PreservedInputFingerprint='synthetic-preserved-input-fingerprint'; InteractiveHostWasEnabled=$true
        }
        Write-HybridRebuildJson -Path (Join-Path $releaseRoot 'hybrid-state.json') -Value $state
        Write-HybridRebuildJson -Path $ValidationHoldPath -Value $(if ($mode -in @('foreign','released-foreign')) { 'another-release' } else { $releaseId })
        $evidence=@{ Mode=$mode; Starts=0; Activations=0; Schemas=0; Drains=0; Disables=0; Enables=0; PoolState='Stopped'; StaleReservation=($mode -eq 'recovery'); Finished=$false; Events=[Collections.Generic.List[string]]::new() }
        try {
            Invoke-HybridPassageIisUpdate -SourceRoot $SourceRoot -Commit ('a'*40) -ResumeRelease $releaseId | Out-Null
            if ($mode -ne 'recovery') { throw 'Ambiguous resume was accepted.' }
        }
        catch {
            $expected=switch ($mode) {
                'foreign' { 'hybrid-hold-not-owned-by-release-and-operation' }
                'released-foreign' { 'hybrid-release-outcome-ambiguous' }
                'unjournalled-schema' { 'hybrid-resume-schema-boundary-ambiguous' }
                default { '' }
            }
            if (-not $expected -or $_.Exception.Message -cne $expected) { throw }
        }
        if ($mode -ne 'recovery') {
            if ($evidence.Starts -or $evidence.Activations -or $evidence.Schemas -or $evidence.Drains -or $evidence.Disables -or $evidence.Enables -or
                (Get-Content $ValidationHoldPath -Raw | ConvertFrom-Json) -cne $(if ($mode -eq 'unjournalled-schema') { $releaseId } else { 'another-release' })) { throw 'Ambiguous resume refusal mutated an operation.' }
        }
        else {
            $events=@($evidence.Events)
            if ($events.IndexOf('start-denied-recovery') -lt 0 -or $events.IndexOf('start-denied-recovery') -ge $events.IndexOf('drain') -or
                -not $evidence.Finished -or (Test-Path $ValidationHoldPath)) { throw 'Stopped-candidate resume did not recover under deny-all before draining.' }
        }
    }
}
finally { if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force } }
Write-Output 'Hybrid updater resume contract passed: foreign holds and unjournalled schema cause zero mutations; stopped candidate recovery precedes drain under deny-all admission.'
