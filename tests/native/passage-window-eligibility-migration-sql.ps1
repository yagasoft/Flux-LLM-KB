param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$ReceiptDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
foreach($name in @('incremental-vector-publication-metadata-migration','incremental-source-deletion-pending-queue-index','incremental-passage-window-eligibility-migration')){
 Import-Module (Join-Path $SourceRoot ('scripts/deploy/'+$name+'.psm1')) -Force
}
$connectionText=[Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_WINDOW_ELIGIBILITY_DISPOSABLE_SQL')
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
$contract=Get-PassageWindowEligibilityMigrationContract
$sqlPath=(Resolve-Path -LiteralPath (Join-Path $SourceRoot 'scripts/deploy/passage-window-eligibility-up.sql')).Path
$read={Get-PassageWindowEligibilityDatabaseState $connectionText}
$initial=&$read
$initial|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $ReceiptDirectory 'initial-state.json') -Encoding utf8
Check ((Get-PassageWindowEligibilityStateKind $initial $initial.Identity) -ceq 'Old') 'Fixture must start at exact79.'
Refuse {Invoke-PassageWindowEligibilityMigrationSql $connectionText $sqlPath 'foreign-incarnation'}
Check ((Get-PassageWindowEligibilityStateKind (&$read) $initial.Identity) -ceq 'Old') 'Wrong incarnation changed SQL.'
Test-Sql @'
EXEC(N'CREATE TRIGGER dbo.WindowEligibilityOwnedFailure ON dbo.__EFMigrationsHistory AFTER INSERT AS
BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE MigrationId=N''20261117210606_PersistPassageWindowEligibility'') THROW 51000,''owned failure after table creation'',1; END;');
'@
try{
 $directory=Join-Path $ReceiptDirectory 'rollback';$null=New-Item -ItemType Directory -Path $directory
 $state=New-PassageWindowEligibilityMigrationState $initial $directory 'owned-rollback' ('a'*40) $contract.UpSha256
 Refuse {Invoke-PassageWindowEligibilityMigrationAttempt $state $read {} {Invoke-PassageWindowEligibilityMigrationSql $connectionText $sqlPath $initial.Identity}}
 Check ($state.SettledState -ceq 'Old') 'Failure did not atomically roll back columns, tables and history.'
}finally{Test-Sql 'DROP TRIGGER IF EXISTS dbo.WindowEligibilityOwnedFailure;'}
$directory=Join-Path $ReceiptDirectory 'lost-ack';$null=New-Item -ItemType Directory -Path $directory
$state=New-PassageWindowEligibilityMigrationState (&$read) $directory 'owned-lost-ack' ('a'*40) $contract.UpSha256
Refuse {Invoke-PassageWindowEligibilityMigrationAttempt $state $read {} {
 Invoke-PassageWindowEligibilityMigrationSql $connectionText $sqlPath $initial.Identity
 throw 'owned lost acknowledgement after committed transaction'
}}
Check ($state.SettledState -ceq 'New' -and !$state.Applied) 'Committed lost acknowledgement was not distinguished from rollback.'
$directory=Join-Path $ReceiptDirectory 'already-new';$null=New-Item -ItemType Directory -Path $directory
$state=New-PassageWindowEligibilityMigrationState (&$read) $directory 'owned-already-new' ('a'*40) $contract.UpSha256
Invoke-PassageWindowEligibilityMigrationAttempt $state $read {} {throw 'Complete state must execute zero DDL.'}
Invoke-PassageWindowEligibilityMigrationSql $connectionText $sqlPath $initial.Identity
Check $state.Applied 'Already-new state was not validated.'
Write-Output 'Owned SQL: exact79 to80, atomic rollback, lost acknowledgement and zero-DDL reapply passed.'
