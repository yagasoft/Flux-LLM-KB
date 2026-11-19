param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$ReceiptDirectory,[switch]$CodexWorkspace,[switch]$MediaTranscription,[switch]$ForwardOnly)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
foreach($name in @('incremental-vector-publication-metadata-migration','incremental-source-deletion-pending-queue-index','incremental-passage-window-eligibility-migration','incremental-source-journal-migration')){
 Import-Module (Join-Path $SourceRoot ('scripts/deploy/'+$name+'.psm1')) -Force
}
$connectionText=[Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_OUTLOOK_DISPOSABLE_SQL')
$connectionText=$connectionText -replace '(?i)(^|;)\s*Trust Server Certificate\s*=','$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=','$1ConnectRetryCount='
$binding=[Data.SqlClient.SqlConnectionStringBuilder]::new($connectionText)
if($binding.InitialCatalog -cnotmatch '^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$'){throw 'Exact generated disposable catalogue required.'}
function Check([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Refuse([scriptblock]$Action){$failed=$false;try{&$Action}catch{$failed=$true};Check $failed 'Expected SQL migration refusal.'}
function Test-Sql([string]$Text){
 $connection=[Data.SqlClient.SqlConnection]::new($connectionText)
 try{
  $connection.Open();$command=$connection.CreateCommand();$command.CommandTimeout=15
  $command.CommandText="IF NOT EXISTS(SELECT 1 FROM sys.databases WHERE database_id=DB_ID() AND owner_sid=SUSER_SID()) THROW 51000,'Unowned disposable catalogue.',1;"+$Text
  $null=$command.ExecuteNonQuery()
 }finally{$connection.Dispose()}
}
$mode = if ($MediaTranscription) { @{MediaTranscription=$true} } elseif ($CodexWorkspace) { @{CodexWorkspace = $true} } else { @{Outlook = $true} }
$contract=Get-PassageWindowEligibilityMigrationContract @mode
$sqlPath=(Resolve-Path -LiteralPath (Join-Path $SourceRoot ('scripts/deploy/' + $contract.ArtifactPrefix + '-up.sql'))).Path
$read={Get-PassageWindowEligibilityDatabaseState $connectionText @mode}
$initial=&$read
$initial|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $ReceiptDirectory 'initial-state.json') -Encoding utf8
Check ((Get-PassageWindowEligibilityStateKind @mode $initial $initial.Identity) -ceq 'Old') 'Fixture must start at the exact predecessor.'
if ($ForwardOnly) {
 $state=New-PassageWindowEligibilityMigrationState @mode $initial $ReceiptDirectory 'owned-forward' ('a'*40) $contract.UpSha256
 Invoke-PassageWindowEligibilityMigrationAttempt $state $read {} {
  Invoke-PassageWindowEligibilityMigrationSql @mode $connectionText $sqlPath $initial.Identity
 }
 Check ($state.Applied -and $state.SettledState -ceq 'New') 'The exact successor was not applied.'
 Assert-SourceJournalSchema (Get-SourceJournalDatabaseState $connectionText)
 return
}
if ($MediaTranscription) {
 Test-Sql 'CREATE TABLE dbo.MediaTranscriptionGpuRequests (Incomplete bit);'
 try {
    Refuse { Get-PassageWindowEligibilityStateKind @mode (&$read) $initial.Identity }
    Refuse { Assert-SourceJournalSchema (Get-SourceJournalDatabaseState $connectionText) }
 } finally { Test-Sql 'DROP TABLE dbo.MediaTranscriptionGpuRequests;' }
}
if ($CodexWorkspace) {
 Test-Sql 'ALTER TABLE dbo.KnowledgeItems ADD CodexWorkspaceRootId uniqueidentifier NULL;'
 try {
    Refuse { Get-PassageWindowEligibilityStateKind @mode (&$read) $initial.Identity }
    Refuse { Assert-SourceJournalSchema (Get-SourceJournalDatabaseState $connectionText) }
 } finally { Test-Sql 'ALTER TABLE dbo.KnowledgeItems DROP COLUMN CodexWorkspaceRootId;' }
}
Refuse {Invoke-PassageWindowEligibilityMigrationSql @mode $connectionText $sqlPath 'foreign-incarnation'}
Check ((Get-PassageWindowEligibilityStateKind @mode (&$read) $initial.Identity) -ceq 'Old') 'Wrong incarnation changed SQL.'
Test-Sql (@'
EXEC(N'CREATE TRIGGER dbo.OutlookMigrationOwnedFailure ON dbo.__EFMigrationsHistory AFTER INSERT AS
BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE MigrationId=N''TARGET_MIGRATION'') THROW 51000,''owned failure after table creation'',1; END;');
'@.Replace('TARGET_MIGRATION', $contract.Target))
try{
 $directory=Join-Path $ReceiptDirectory 'rollback';$null=New-Item -ItemType Directory -Path $directory
 $state=New-PassageWindowEligibilityMigrationState @mode $initial $directory 'owned-rollback' ('a'*40) $contract.UpSha256
 Refuse {Invoke-PassageWindowEligibilityMigrationAttempt $state $read {} {Invoke-PassageWindowEligibilityMigrationSql @mode $connectionText $sqlPath $initial.Identity}}
 Check ($state.SettledState -ceq 'Old') 'Failure did not atomically roll back columns, tables and history.'
 $outcome=Get-Content -Raw -LiteralPath $state.OutcomePath | ConvertFrom-Json
 Check ($outcome.Error -like '*owned failure after table creation*') 'The intended post-DDL failure was not reached.'
}finally{Test-Sql 'DROP TRIGGER IF EXISTS dbo.OutlookMigrationOwnedFailure;'}
$directory=Join-Path $ReceiptDirectory 'lost-ack';$null=New-Item -ItemType Directory -Path $directory
$state=New-PassageWindowEligibilityMigrationState @mode (&$read) $directory 'owned-lost-ack' ('a'*40) $contract.UpSha256
Refuse {Invoke-PassageWindowEligibilityMigrationAttempt $state $read {} {
 Invoke-PassageWindowEligibilityMigrationSql @mode $connectionText $sqlPath $initial.Identity
 throw 'owned lost acknowledgement after committed transaction'
}}
Check ($state.SettledState -ceq 'New' -and !$state.Applied) 'Committed lost acknowledgement was not distinguished from rollback.'
$directory=Join-Path $ReceiptDirectory 'already-new';$null=New-Item -ItemType Directory -Path $directory
$state=New-PassageWindowEligibilityMigrationState @mode (&$read) $directory 'owned-already-new' ('a'*40) $contract.UpSha256
Invoke-PassageWindowEligibilityMigrationAttempt $state $read {} {throw 'Complete state must execute zero DDL.'}
Invoke-PassageWindowEligibilityMigrationSql @mode $connectionText $sqlPath $initial.Identity
Check $state.Applied 'Already-new state was not validated.'
Assert-SourceJournalSchema (Get-SourceJournalDatabaseState $connectionText)
$alter = if ($MediaTranscription) {
    'ALTER TABLE dbo.MediaTranscriptionGpuRequests NOCHECK CONSTRAINT CK_MediaTranscriptionGpuRequests_Phase;'
} elseif ($CodexWorkspace) {
    'ALTER INDEX IX_KnowledgeItems_CodexWorkspaceRootId_CreatedAtUtc_Id ON dbo.KnowledgeItems DISABLE;'
} else { 'ALTER TABLE dbo.OutlookCaptureProfiles ALTER COLUMN WatchedRootIdentity varchar(127) NULL;' }
$restore = if ($MediaTranscription) {
    'ALTER TABLE dbo.MediaTranscriptionGpuRequests WITH CHECK CHECK CONSTRAINT CK_MediaTranscriptionGpuRequests_Phase;'
} elseif ($CodexWorkspace) {
    'ALTER INDEX IX_KnowledgeItems_CodexWorkspaceRootId_CreatedAtUtc_Id ON dbo.KnowledgeItems REBUILD;'
} else { 'ALTER TABLE dbo.OutlookCaptureProfiles ALTER COLUMN WatchedRootIdentity varchar(128) NULL;' }
Test-Sql $alter
try {
    Refuse { Get-PassageWindowEligibilityStateKind @mode (&$read) $initial.Identity }
    Refuse { Assert-SourceJournalSchema (Get-SourceJournalDatabaseState $connectionText) }
} finally { Test-Sql $restore }
Assert-SourceJournalSchema (Get-SourceJournalDatabaseState $connectionText)
$journalRelease='20261119T000000Z-aaaaaaaaaaaa'
$journalDirectory=Join-Path $ReceiptDirectory $journalRelease
$null=New-Item -ItemType Directory -Path $journalDirectory
$journalRead={Get-SourceJournalDatabaseState $connectionText}
$journalState=New-SourceJournalMigrationState (&$journalRead) $journalDirectory $journalRelease (Get-SourceJournalMigrationContract).UpSha256
Invoke-SourceJournalMigrationAttempt $journalState $journalRead {} {throw 'An already applied journal migration must execute zero DDL.'}
$hold=Join-Path $ReceiptDirectory 'hold.json'
[IO.File]::WriteAllText($hold, ($journalRelease | ConvertTo-Json -Compress))
$recoveredIntent=Get-SourceJournalHeldMigrationIntent $hold $ReceiptDirectory $initial.Identity
Check ($recoveredIntent -ceq $journalState.IntentPath) 'The successor journal release intent could not be recovered.'
Write-Output 'Owned SQL: exact schema migration, atomic rollback, lost acknowledgement and zero-DDL reapply passed.'

if ($MediaTranscription) {
    $down = Join-Path $SourceRoot 'scripts/deploy/media-transcription-gpu-down.sql'
    Check ((Get-FileHash -LiteralPath $down).Hash -ceq $contract.DownSha256) 'Unreviewed Down script.'
    foreach ($batch in [regex]::Split((Get-Content -Raw -LiteralPath $down),'(?im)^\s*GO\s*$')) {
        if ($batch.Trim()) { Test-Sql $batch }
    }
    Check ((Get-PassageWindowEligibilityStateKind @mode (&$read) $initial.Identity) -ceq 'Old') 'Disposable Down did not restore exact 82.'
    Invoke-PassageWindowEligibilityMigrationSql @mode $connectionText $sqlPath $initial.Identity
}
