[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$SqlPath)
$ErrorActionPreference='Stop'
# Native control-flow fixture; actual format admission has separate SQL integration coverage.
function Invoke-DeploymentIndexAdmission { param($Roles,$Action) & $Action }
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-deletion-cleanup-migration.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
$contract=Get-SourceDeletionCleanupMigrationContract
if ((Get-FileHash -LiteralPath $SqlPath -Algorithm SHA256).Hash -cne $contract.UpSha256) { throw 'Updater fault tests require the reviewed SQL artifact.' }
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count -ne 0) { throw 'The updater does not parse.' }
foreach ($name in @('New-DeploymentValidationHold','Assert-SourceDeletionCleanupReleaseBinding','Get-SourceDeletionCleanupCandidateDatabase','Start-IndexCompatibleWebAppPool')) {
    $function=$ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name },$true)
    Invoke-Expression $function.Extent.Text
}
$createHold=(Get-Command New-DeploymentValidationHold).ScriptBlock
function New-DeploymentValidationHold {
    param($Path,$ReleaseId)
    $result=& $createHold -Path $Path -ReleaseId $ReleaseId
    if ($failurePoint -ceq 'hold-owner') { [IO.File]::WriteAllText($Path,'"other-release"') }
    $result
}
$command=@($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Invoke-IncrementalApplicationPayloadSwap' },$true))[-1]
$outerTry=$command.Parent
while ($null -ne $outerTry -and $outerTry -isnot [Management.Automation.Language.TryStatementAst]) { $outerTry=$outerTry.Parent }
if ($null -eq $outerTry) { throw 'The ordinary updater payload swap has no containing recovery handler.' }
$catchText=$outerTry.CatchClauses[0].Body.Extent.Text
$catchBlock=[scriptblock]::Create($catchText.Substring(1,$catchText.Length-2))
function Assert-True([bool]$Value,[string]$Message) { if (!$Value) { throw $Message } }
function Assert-NotReparsePoint { param($Path,$Message); if ((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw $Message } }
function Stop-HybridIisAfterGpuDrain { $calls.Pool='Stopped'; $calls.Stops++ }
function Get-DeploymentSqlConnectionString { 'disposable-mock' }
function Get-SourceDeletionCleanupDatabaseState {
    param($ConnectionString)
    $calls.Reads++
    if (($failurePoint -ceq 'queued-before-start' -and $calls.Reads -eq 3) -or
        ($failurePoint -ceq 'queued-before-release' -and $calls.Reads -eq 4)) { $db.RunnableCount=1 }
    [pscustomobject]$db
}
function Get-RepositoryRecoveryTaskObservation { $task }
function Disable-ScheduledTask { param($TaskName); $task.Enabled=$false; $task.State='Disabled' }
function Wait-InteractiveHostStopped { param($TaskName,$TimeoutSeconds); Assert-True (!$task.Enabled -and $task.State -cne 'Running') 'Recovery restored companion bytes before task exit.' }
function Get-RetainedPipelineStateBaseline { 'unchanged' }
function Assert-RetainedPipelineStateUnchanged { param($Baseline,$Current); Assert-True ($Baseline -ceq $Current) 'Pipeline changed.' }
function Invoke-CandidatePayloadActivation { param($CandidateRoot,$ApplicationRoot); Move-Item -LiteralPath $CandidateRoot -Destination $ApplicationRoot }
function Invoke-GeneratedSqlScript {
    param($Path)
    Assert-True (Test-Path -LiteralPath (Join-Path $releaseRoot 'source-deletion-migration-attempt.json')) 'SQL preceded its durable receipt.'
    Assert-True (Test-Path -LiteralPath $ValidationHoldPath) 'SQL ran without its owned hold.'
    Assert-True (!$task.Enabled -and $calls.Pool -ceq 'Stopped') 'SQL ran with a running task or IIS pool.'
    $calls.Up++; $db.ColumnCount=5; $db.CheckCount=2; $db.SchemaValid=$true; $db.History += $contract.Target
}
function Start-WebAppPool {
    param($Name)
    $version=[IO.File]::ReadAllText((Join-Path $CanonicalDeployRoot 'version'))
    if ($version -ceq 'old') { $calls.OldStarts++ } else { $calls.CandidateStarts++ }
    $calls.Pool='Started'
}
function Wait-IisAppPoolState { param($Name,$ExpectedState,$TimeoutSeconds); Assert-True ($calls.Pool -ceq $ExpectedState) 'Pool state differs.' }
function Invoke-RequiredLoopbackProbes {
    param($Origin,$TimeoutSeconds)
    Assert-True ($calls.Pool -ceq 'Started') 'Stopped predecessor was probed.'
    if ($failurePoint -ceq 'config-after-start') { [IO.File]::WriteAllText($appConfig,'changed-after-start') }
    if ($failurePoint -ceq 'hold-after-start') { [IO.File]::WriteAllText($ValidationHoldPath,'"other-release"') }
    if ($failurePoint -ceq 'prior-payload-drift') {
        [IO.File]::WriteAllText((Join-Path $previousRoot 'version'),'damaged')
        throw 'injected candidate probe failure and corrupted predecessor bytes'
    }
}
function Assert-StoppedPoolRecoveryBaseline { Assert-True ($calls.Pool -ceq 'Stopped') 'Recovery left a running pool.' }
function Get-SourceDeletionAcceptanceIisHash { if ($failurePoint -ceq 'maintenance-iis-drift') { 'changed' } else { 'iis' } }
function Assert-SourceDeletionAcceptanceCpuOwnerFree { if ($failurePoint -ceq 'maintenance-cpu-owner') { throw 'Native owner remains.' } }
function Test-InteractiveHostPayload { param($Path); Assert-True (Test-Path -LiteralPath (Join-Path $Path 'version')) 'Missing companion payload.' }
function Restore-InteractiveHostPayload {
    param($PreviousRoot,$LiveRoot)
    Copy-Item -LiteralPath (Join-Path $PreviousRoot 'version') -Destination (Join-Path $LiveRoot 'version') -Force
    $calls.CompanionRestores++
}
function Remove-DeploymentValidationHold {
    param($Path,$ReleaseId)
    if ($failurePoint -ceq 'remove-before') { throw 'injected hold removal failure before delete' }
    [IO.File]::Delete($Path)
    if ($failurePoint -ceq 'remove-after') { throw 'injected lost hold removal acknowledgement' }
}
$testRoot=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('flux-source-updater-'+[guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    foreach ($failurePoint in @('hold-owner','task-enabled','config-drift','sql-drift','queued-before-start','queued-before-release','config-after-start','hold-after-start','prior-payload-drift','intent-write','remove-before','remove-after','ack-write','none',
        'maintenance-restarted','maintenance-iis-drift','maintenance-cpu-owner','maintenance-foreign-hold','maintenance-none')) {
        $releaseId=$failurePoint; $releaseRoot=Join-Path $testRoot $releaseId
        $CanonicalLiveRoot=Join-Path $releaseRoot 'install'; $CanonicalDeployRoot=Join-Path $CanonicalLiveRoot 'App'
        $candidateRoot=Join-Path $releaseRoot 'candidate'; $previousRoot=Join-Path $releaseRoot 'previous'; $failedRoot=Join-Path $releaseRoot 'failed'
        $InteractiveHostRoot=Join-Path $releaseRoot 'companion/live'; $interactiveHostPreviousRoot=Join-Path $releaseRoot 'companion/previous'
        $ValidationHoldPath=Join-Path $CanonicalLiveRoot 'Runtime/hold.json'
        New-Item -ItemType Directory -Path $CanonicalDeployRoot,$candidateRoot,$InteractiveHostRoot,$interactiveHostPreviousRoot,(Split-Path -Parent $ValidationHoldPath),(Join-Path $CanonicalLiveRoot 'Config') | Out-Null
        foreach ($directory in @($CanonicalDeployRoot,$interactiveHostPreviousRoot)) { [IO.File]::WriteAllText((Join-Path $directory 'version'),'old') }
        foreach ($directory in @($candidateRoot,$InteractiveHostRoot)) { [IO.File]::WriteAllText((Join-Path $directory 'version'),'new') }
        $appConfig=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
        $companionConfig=Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json'
        [IO.File]::WriteAllText($appConfig,'{}'); [IO.File]::WriteAllText($companionConfig,'{}')
        $db=@{ Identity='owned'; History=@('earlier',$contract.Baseline); ColumnCount=0; CheckCount=0; CanAlter=$true;
            SchemaValid=$false; RunnableCount=0; LegacySha256=('A'*64); ProgressIsInitial=$true }
        $task=@{ Enabled=$false; State='Disabled'; IdentityHash=('B'*64); LauncherSha256=('C'*64) }
        $sourceDeletionCleanupState=New-SourceDeletionCleanupMigrationState -DatabaseState ([pscustomobject]$db) -ReceiptDirectory $releaseRoot `
            -Binding @{ ReleaseId=$releaseId; Commit=('d'*40); SqlSha256=$contract.UpSha256;
                WebPayloadSha256=(Get-HybridPayloadFingerprint $candidateRoot); CompanionPayloadSha256=(Get-HybridPayloadFingerprint $InteractiveHostRoot);
                OriginalWebSha256=(Get-HybridPayloadFingerprint $CanonicalDeployRoot); OriginalCompanionSha256=(Get-HybridPayloadFingerprint $interactiveHostPreviousRoot);
                ConfigurationSha256=(Get-FileHash -LiteralPath $appConfig).Hash; CompanionConfigurationSha256=(Get-FileHash -LiteralPath $companionConfig).Hash; Task=$task.Clone() }
        $caseSqlPath=Join-Path $releaseRoot 'up.sql'
        Copy-Item -LiteralPath $SqlPath -Destination $caseSqlPath
        if ($failurePoint -ceq 'task-enabled') { $task.Enabled=$true }
        if ($failurePoint -ceq 'config-drift') { [IO.File]::WriteAllText($appConfig,'changed') }
        if ($failurePoint -ceq 'sql-drift') { [IO.File]::WriteAllText($caseSqlPath,'changed') }
        if ($failurePoint -ceq 'intent-write') { [IO.File]::WriteAllText((Join-Path $releaseRoot 'source-deletion-hold-release-intent.json'),'existing') }
        if ($failurePoint -ceq 'ack-write') { [IO.File]::WriteAllText((Join-Path $releaseRoot 'source-deletion-hold-released.json'),'existing') }
        $calls=@{ Pool='Started'; Up=0; OldStarts=0; CandidateStarts=0; Stops=0; CompanionRestores=0; Reads=0 }
        $SourceDeletionAcceptanceMaintenanceReceipt=''
        $sourceDeletionMaintenance=$null
        if ($failurePoint.StartsWith('maintenance-',[StringComparison]::Ordinal)) {
            $SourceDeletionAcceptanceMaintenanceReceipt='exact-receipt'
            $sourceDeletionMaintenance=@{ Original=@{ IisSha256='iis' } }
            if ($failurePoint -cne 'maintenance-restarted') { $calls.Pool='Stopped' }
            if ($failurePoint -ceq 'maintenance-foreign-hold') { [IO.File]::WriteAllText($ValidationHoldPath,'"foreign"') }
        }
        $ApplyResumableSourceDeletionCleanupMigration=$true
        $ApplyMigrations=$false; $ApplyCodeDisclosureProofMigration=$false; $ApplyCorpusChunkFullTextMigration=$false
        $EnableUnattendedDiscovery=$false; $RecoverStoppedPool=$false; $DeferReadinessForScopedRemediation=$false
        $hostingChange=@{ Attempted=$false; RollbackVerified=$true }
        $deploymentValidation=@{ HoldCreated=$false; Baseline=$null; PayloadRollbackVerified=$false; RollbackVerified=$true }
        $migrationPlan=@{ Up=@{ Path=$caseSqlPath } }; $SiteName='owned-test'; $InteractiveHostTaskName='owned-test'; $ReadinessTimeoutSeconds=1
        $loopbackOrigin=@{ Origin='http://127.0.0.1:1' }
        $interactiveHostMutationStarted=$true; $interactiveHostTaskWasEnabled=$false; $holdReleased=$false
        $failed=$false
        try { Invoke-Expression $command.Extent.Text | Out-Null }
        catch { try { . $catchBlock } catch { $failed=$true } }
        Assert-True ($failed -eq ($failurePoint -notin @('none','maintenance-none'))) 'Unexpected updater failure outcome.'
        $maintenanceRefusal=$failurePoint.StartsWith('maintenance-',[StringComparison]::Ordinal) -and $failurePoint -cne 'maintenance-none'
        $beforeSql=$maintenanceRefusal -or $failurePoint -in @('hold-owner','task-enabled','config-drift','sql-drift')
        $beforeStart=$beforeSql -or $failurePoint -ceq 'queued-before-start'
        Assert-True ($calls.Up -eq $(if ($beforeSql) { 0 } else { 1 }) -and $calls.OldStarts -eq 0 -and
            $calls.CandidateStarts -eq $(if ($beforeStart) { 0 } else { 1 })) "SQL replay, unguarded candidate or predecessor startup occurred at $failurePoint."
        $web=[IO.File]::ReadAllText((Join-Path $CanonicalDeployRoot 'version'))
        $companion=[IO.File]::ReadAllText((Join-Path $InteractiveHostRoot 'version'))
        if ($maintenanceRefusal) {
            Assert-True ($web -ceq 'old' -and $companion -ceq 'old' -and !$task.Enabled -and $calls.Stops -eq 0 -and
                $calls.Pool -ceq $(if ($failurePoint -ceq 'maintenance-restarted') { 'Started' } else { 'Stopped' }) -and
                (Test-Path -LiteralPath $ValidationHoldPath) -eq ($failurePoint -ceq 'maintenance-foreign-hold')) 'Maintenance drift was silently adopted or execution/hold state changed.'
            if ($failurePoint -ceq 'maintenance-foreign-hold') { Assert-True ([IO.File]::ReadAllText($ValidationHoldPath) -ceq '"foreign"') 'Foreign hold changed.' }
        } elseif ($failurePoint -in @('hold-owner','task-enabled','config-drift','sql-drift','queued-before-start','queued-before-release','config-after-start','intent-write')) {
            Assert-True ($web -ceq 'old' -and $companion -ceq 'old' -and $calls.Pool -ceq 'Stopped' -and
                (Test-Path -LiteralPath $ValidationHoldPath) -and !$task.Enabled -and !$sourceDeletionCleanupState.ForwardRecoveryRequired) 'Pre-intent failure did not restore both payloads stopped and held with task disabled.'
        } elseif ($failurePoint -in @('hold-after-start','prior-payload-drift')) {
            Assert-True ($web -ceq $(if ($failurePoint -ceq 'hold-after-start') { 'new' } else { 'damaged' }) -and
                $companion -ceq 'new' -and $calls.Pool -ceq 'Stopped' -and !$task.Enabled -and
                (Test-Path -LiteralPath $ValidationHoldPath) -and $calls.CompanionRestores -eq 0 -and
                !$sourceDeletionCleanupState.ForwardRecoveryRequired) 'Unverified predecessor restoration changed the companion or released execution/hold.'
        } else {
            Assert-True ($web -ceq 'new' -and $companion -ceq 'new' -and $sourceDeletionCleanupState.ForwardRecoveryRequired -and
                $calls.CompanionRestores -eq 0) 'Release intent allowed a mixed pair or predecessor rollback.'
            Assert-True ((Test-Path -LiteralPath $ValidationHoldPath) -eq ($failurePoint -ceq 'remove-before')) 'Hold outcome differs from injected removal boundary.'
        }
    }
} finally {
    $resolved=(Resolve-Path -LiteralPath $testRoot).Path
    if (![string]::Equals($resolved,$testRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected updater test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output 'Actual updater callbacks passed: guarded admission, held rollback, retained restoration uncertainty, ambiguous release and success; no SQL replay or predecessor restart.'
