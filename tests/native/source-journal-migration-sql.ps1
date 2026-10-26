[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$SqlPath,
 [Parameter(Mandatory)][string]$ReceiptDirectory,[switch]$FailAfterFirstMigration,[switch]$UseDeploymentExecutor,[string]$WebEntryPath='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-migration.psm1') -Force
$text=[Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_JOURNAL_DISPOSABLE_SQL')
$text=$text -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
$builder=[Data.SqlClient.SqlConnectionStringBuilder]::new($text)
if($builder.InitialCatalog -cnotmatch '^FluxKnowledge_Phase1Tests_[0-9a-f]{32}$'){throw 'Journal migration tests require a generated disposable catalogue.'}
$builder['Pooling']=$false;$connectionText=$builder.ConnectionString
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Expect-Refusal([scriptblock]$Action){$refused=$false;try{&$Action}catch{$refused=$true};Assert-True $refused ('Expected refusal: '+$Action.ToString())}
function Read-LegacyFingerprint {
 $connection=[Data.SqlClient.SqlConnection]::new($connectionText)
 try{
  $connection.Open();$command=$connection.CreateCommand();$command.CommandTimeout=10
  $command.CommandText=@'
DECLARE @snapshot nvarchar(max)=CONCAT(
 (SELECT * FROM SourceRootConfigurations ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
 (SELECT * FROM SourceRevisions ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
 (SELECT * FROM SourceArtifacts ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES));
SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),@snapshot)),2);
'@
  [string]$command.ExecuteScalar()
 }finally{$connection.Dispose()}
}
function Invoke-TestSql([string]$Sql,[bool]$Fail) {
 $connection=[Data.SqlClient.SqlConnection]::new($connectionText)
 try{
  $connection.Open();$firstHistory=$false
  foreach($batch in [regex]::Split($Sql,'(?im)^\s*GO\s*(?:--.*)?$')|Where-Object{![string]::IsNullOrWhiteSpace($_)}){
   if(!$Fail -and !$seedControl.Seeded -and $batch.Contains('ADD [ReadCountsIncomplete]')){
    $seed=$connection.CreateCommand()
    try{
     $seed.CommandText=@'
INSERT SourceVerificationRuns (Id,SourceRootId,CreatedAtUtc,CompletedAtUtc,State,BytesRead,
 Active,AuthorityJson,OriginalPolicyJson,Reason,SourceBytesRead,RetainedBytesRead)
SELECT @id,Id,'2026-10-01','2026-10-02',N'Completed',42,0,N'{}',N'{}',N'owned-counter-migration-fixture',40,2
FROM (SELECT TOP(1) Id FROM SourceRootConfigurations ORDER BY Id) r;
'@
     $null=$seed.Parameters.AddWithValue('@id',$seedControl.RunId)
     Assert-True ($seed.ExecuteNonQuery() -eq 1) 'The pre-counter legacy verification row was not seeded.'
     $seedControl.Seeded=$true
    }finally{$seed.Dispose()}
   }
   $command=$connection.CreateCommand();$command.CommandTimeout=30
   try{$command.CommandText=$batch;$null=$command.ExecuteNonQuery()}finally{$command.Dispose()}
   if($batch.Contains('INSERT INTO [__EFMigrationsHistory]') -and $batch.Contains('20261024095915_AddIncrementalSourceScanning')){$firstHistory=$true}
   if($Fail -and $firstHistory -and $batch -match '(?m)^\s*COMMIT;') {throw 'owned interruption after the first committed additive migration'}
  }
 }finally{$connection.Dispose()}
}
$contract=Get-SourceJournalMigrationContract
$bytes=Read-SourceJournalMigrationSql -Path $SqlPath
Assert-True ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ceq $contract.UpSha256) 'Generated SQL differs from the reviewed bytes.'
$sql=[Text.UTF8Encoding]::new($false,$true).GetString($bytes).TrimStart([char]0xfeff)
$before=Get-SourceJournalDatabaseState $connectionText
Write-Output ('Disposable baseline history='+$before.History.Count+'; schema='+$before.SchemaSha256)
Assert-SourceJournalSchema $before -AllowBaseline
Assert-True ($before.History.Count -eq $contract.BaselineHistoryCount) 'Fixture did not start at the exact old schema.'
$legacy=Read-LegacyFingerprint
$state=New-SourceJournalMigrationState -DatabaseState $before -ReceiptDirectory $ReceiptDirectory -ReleaseId 'disposable-sql-qualification' -SqlSha256 $contract.UpSha256
$read={Get-SourceJournalDatabaseState $connectionText}
$case=@{Calls=0}
$seedControl=@{Seeded=$false;RunId=[guid]::NewGuid()}
$up={Assert-True (Test-Path -LiteralPath $state.IntentPath) 'Intent was not durable before SQL.';$case.Calls++;
 if($UseDeploymentExecutor){Invoke-SourceJournalDeploymentSql -SqlBytes $bytes -ConnectionString $connectionText -ExpectedDatabaseIdentity $before.Identity}
 else{Invoke-SourceJournalVerifiedSql -SqlBytes $bytes -RunSql {param($verifiedSql);Invoke-TestSql $verifiedSql ([bool]$FailAfterFirstMigration)}}}
Expect-Refusal {Invoke-SourceJournalDeploymentSql -SqlBytes $bytes -ConnectionString $connectionText -ExpectedDatabaseIdentity 'different-incarnation'}
Assert-True ((Read-LegacyFingerprint) -ceq $legacy) 'A mismatched executor target mutated the catalogue.'
if($FailAfterFirstMigration){
 Expect-Refusal {Invoke-SourceJournalMigrationAttempt -State $state -ReadState $read -AssertStopped {} -RunUp $up}
 $partial=&$read
 Assert-True ($state.Attempted -and !$state.Applied -and $partial.History.Count -eq ($before.History.Count+1)) 'Fault did not preserve exactly one committed migration and attempted intent.'
 Expect-Refusal {Assert-SourceJournalSchema $partial -AllowBaseline}
 Expect-Refusal {Assert-SourceJournalPredecessorStart -DatabaseState $partial -MigrationState $state}
 Expect-Refusal {New-SourceJournalMigrationState -DatabaseState $partial -ReceiptDirectory $ReceiptDirectory -ReleaseId 'unsafe-retry' -SqlSha256 $contract.UpSha256}
}else{
 Invoke-SourceJournalMigrationAttempt -State $state -ReadState $read -AssertStopped {} -RunUp $up
 $after=&$read;Assert-SourceJournalSchema $after
 Assert-True ($state.Applied -and $after.Identity -ceq $before.Identity) 'Complete migration lost its binding.'
 Expect-Refusal {Assert-SourceJournalPredecessorStart -DatabaseState $after}
 # Explicit disposable proof of the actual executor's idempotent SQL, not an operator retry.
 Invoke-SourceJournalDeploymentSql -SqlBytes $bytes -ConnectionString $connectionText -ExpectedDatabaseIdentity $after.Identity
 $repeat=&$read;Assert-SourceJournalSchema $repeat
 Assert-True (($repeat.History -join '|') -ceq ($after.History -join '|')) 'Idempotent script changed completed history.'
 if($UseDeploymentExecutor){
  & (Join-Path $SourceRoot 'tests/native/source-journal-composed-sql-admission.ps1') -SourceRoot $SourceRoot -ReceiptDirectory $ReceiptDirectory -WebEntryPath $WebEntryPath
 }
 $check=[Data.SqlClient.SqlConnection]::new($connectionText)
 try{
  $check.Open();$command=$check.CreateCommand()
  $command.CommandText="SELECT COUNT(*) FROM SourceVerificationRuns WHERE Id=@id AND ReadCountsIncomplete=1 AND State=N'Completed' AND BytesRead=42 AND SourceBytesRead=40 AND RetainedBytesRead=2 AND CompletedAtUtc='2026-10-02';"
  $null=$command.Parameters.AddWithValue('@id',$seedControl.RunId)
   if(!$UseDeploymentExecutor){Assert-True ($seedControl.Seeded -and [int]$command.ExecuteScalar() -eq 1) 'The additive counter backfill changed totals/status or failed to mark prior measurements incomplete.'}
 }finally{$check.Dispose()}
}
Expect-Refusal {Invoke-SourceJournalMigrationAttempt -State $state -ReadState $read -AssertStopped {} -RunUp $up}
Assert-True ($case.Calls -eq 1) 'The operator replayed its SQL attempt.'
Assert-True ((Read-LegacyFingerprint) -ceq $legacy) 'Legacy root/revision/artifact metadata changed.'
Write-Output ('Disposable journal migration passed; injected_committed_failure='+[bool]$FailAfterFirstMigration+'; no predecessor start or Down.')
