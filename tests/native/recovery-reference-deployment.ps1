param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$ExpectedDatabase)
$ErrorActionPreference='Stop'
$normalised=$env:FLUXKNOWLEDGE_RECOVERY_REFERENCE_TEST_SQL -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
$target=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
if($target.InitialCatalog -cne $ExpectedDatabase -or $ExpectedDatabase -cnotmatch '^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$') { throw 'Not an owned disposable database.' }
$script:connection=$target.ConnectionString
function Sql([string]$text) {
    $sql=[Data.SqlClient.SqlConnection]::new($script:connection)
    try { $sql.Open();$command=$sql.CreateCommand();$command.CommandTimeout=3
        try {$command.CommandText=$text;return $command.ExecuteScalar()}finally{$command.Dispose()}
    }finally{$sql.Dispose()}
}
function Parse([string]$file) {
    $tokens=$null;$errors=$null
    $result=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot $file),[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'Deployment source did not parse.'}; return $result
}
function Require([bool]$condition,[string]$reason){if(!$condition){throw $reason}}
$updater=Parse 'scripts/deploy/update-native-iis-incremental.ps1'
$restorer=Parse 'scripts/deploy/restore-outlook-scheduled-host.ps1'
$entry=@($updater.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst] -and $_.Body.Extent.Text.Contains('if ($ApplyRepositoryRecoveryAuthorityMigration -and $ReconcileRepositoryRecoveryRelease)')})
Require ($entry.Count -eq 1) 'Actual updater operation boundary missing.'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/recovery-reference-writer.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper-task.psm1') -Force
$script:scratch=Join-Path ([IO.Path]::GetTempPath()) ('flux-recovery-deployment-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($script:scratch) | Out-Null
$script:mutations=0;$script:failWrite=$false
function Get-DeploymentSqlConnectionString {$script:connection}
function Get-OutlookRecoveryReferenceConnectionString {$script:connection}
function Assert-IncrementalIisPreflight {}
function Ensure-CpuSearchOwnerFile {}
function Assert-RepositoryRecoveryReceiptPath {}
function Test-IncrementalRollbackHoldRelease {$false}
function Mutation {
    $script:mutations++
    Require ([int](Sql 'SELECT COUNT(*) FROM dbo.IndexRecoveryWriters;') -eq 1) 'Recovery mutation was not preceded by durable writer registration.'
    Require (![bool](Sql 'SELECT IsValid FROM dbo.IndexRecoveryAudit WHERE Id=1;')) 'Recovery mutation retained a current audit.'
    Require (![bool](Sql 'SELECT OwnerExitSufficient FROM dbo.IndexRecoveryWriters;')) 'External deployment writer must be conservative.'
    if($script:failWrite){throw 'injected-recovery-write-failure'}
    [IO.File]::WriteAllText((Join-Path $script:scratch 'mutation'),'written')
}
function Invoke-RepositoryRecoveryIisUpdate {Mutation}
function Invoke-HybridPassageIisPatch {Mutation}
function Invoke-HybridPassageIisUpdate {Mutation}
function Invoke-Continuation([string]$mode) {
    $CanonicalRecoveryRoot=$script:scratch
    $SourceRoot='disposable';$commit='test';$SiteName='test'
    $ApplyRepositoryRecoveryAuthorityMigration=$mode -in @('reconcile','prepared','activation','repository')
    $ReconcileRepositoryRecoveryRelease=if($mode -ceq 'reconcile'){'test'}else{''}
    $ResumeRepositoryRecoveryPreparedRelease=if($mode -ceq 'prepared'){'test'}else{''}
    $ResumeRepositoryRecoveryActivationRelease=if($mode -ceq 'activation'){'test'}else{''}
    $ApplyHybridPassageRebuild=$mode -in @('hybrid','patch')
    $PatchHybridRebuildRelease=if($mode -ceq 'patch'){'test'}else{''}
    $ResumeHybridPatchRelease='';$ResumeHybridRebuildRelease='';$ReplaceHybridRebuildRelease=''
    $ExpectedPatchCandidateHash='';$ExpectedPatchOperatorHash='';$AcknowledgeCancelledQueryTaskId='';$ExpectedCancelledQueryTaskSha256=''
    $sourceAccessChange=$null;$EnableUnattendedDiscovery=$false
    $leaseAcquired=$false;$deploymentValidation=$null;$holdReleaseAttempted=$false;$holdReleased=$false
    $ApplyResumableSourceDeletionCleanupMigration=$false;$sourceDeletionCleanupState=$null
    $interactiveHostMutationStarted=$false;$interactiveHostTaskWasEnabled=$false
    $recoveryWriter=$null
    $sourceJournalHelperState=$null;$sourceJournalNativePorts=$null
    $mutex=[Threading.Mutex]::new($false,'Local\FluxKnowledge.RecoveryFixture.'+[guid]::NewGuid().ToString('N'))
    Invoke-Expression $entry[0].Extent.Text
}
try {
    foreach($mode in @('reconcile','prepared','activation','repository','hybrid','patch')) {
        [void](Sql 'UPDATE dbo.IndexRecoveryAudit SET IsValid=1 WHERE Id=1;')
        Invoke-Continuation $mode
        Require ([int](Sql 'SELECT COUNT(*) FROM dbo.IndexRecoveryWriters;') -eq 0) 'Successful updater continuation retained writer ownership.'
    }
    $script:failWrite=$true
    $refused=$false
    try{Invoke-Continuation 'reconcile'}catch{if($_.Exception.Message -notlike '*injected-recovery-write-failure*'){throw};$refused=$true}
    Require $refused 'Injected updater failure was swallowed.'
    Require ([int](Sql 'SELECT COUNT(*) FROM dbo.IndexRecoveryWriters;') -eq 1) 'Failed updater lost conservative ownership.'
    [void](Sql 'DELETE dbo.IndexRecoveryWriters;') # Disposable fixture reconciliation only.

    $save=@($restorer.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Save-OutlookHostRestorationReceipt'},$true))
    Require ($save.Count -eq 1) 'Actual Outlook receipt writer missing.'
    Invoke-Expression $save[0].Extent.Text
    $CanonicalRecoveryRoot=$script:scratch
    $packet=Join-Path $script:scratch 'IncrementalUpdates/test'
    [IO.Directory]::CreateDirectory($packet) | Out-Null
    $receipt=Join-Path $packet 'outlook-host-restoration.json'
    Save-OutlookHostRestorationReceipt $receipt @{Version=1;State='test'}
    Require ([IO.File]::Exists($receipt)) 'Receipt was not written.'
    Require ([int](Sql 'SELECT COUNT(*) FROM dbo.IndexRecoveryWriters;') -eq 0) 'Completed receipt retained ownership.'
    [IO.Directory]::CreateDirectory($receipt+'.directory') | Out-Null
    $refused=$false
    try{Save-OutlookHostRestorationReceipt ($receipt+'.directory') @{Version=1;State='test'}}catch{$refused=$true}
    Require $refused 'Invalid receipt replacement unexpectedly succeeded.'
    Require ([int](Sql 'SELECT COUNT(*) FROM dbo.IndexRecoveryWriters WHERE OwnerExitSufficient=1;') -eq 1) 'Failed in-process receipt lost its recoverable writer record.'
    'PASS: actual updater continuations and Outlook receipt writer lifetimes.'
} finally {
    [void](Sql 'DELETE dbo.IndexRecoveryWriters;')
    $resolved=[IO.Path]::GetFullPath($script:scratch)
    Require ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolved).StartsWith('flux-recovery-deployment-')) 'Unsafe fixture cleanup.'
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
