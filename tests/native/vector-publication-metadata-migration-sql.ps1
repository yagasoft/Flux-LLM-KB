param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$ReceiptDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-vector-publication-metadata-migration.psm1') -Force
$connectionText=[Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_VECTOR_METADATA_DISPOSABLE_SQL')
$connectionText=$connectionText -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' `
 -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
$binding=[Data.SqlClient.SqlConnectionStringBuilder]::new($connectionText)
if($binding.InitialCatalog -cnotmatch '^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$'){throw 'Exact generated disposable catalogue required.'}
function Check([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Refuse([scriptblock]$Action){$failed=$false;try{&$Action}catch{$failed=$true};Check $failed 'Expected SQL migration refusal.'}
function Test-Sql([string]$Text) {
 $connection=[Data.SqlClient.SqlConnection]::new($connectionText)
 try{$connection.Open();$command=$connection.CreateCommand();$command.CommandTimeout=15
  $command.CommandText="IF NOT EXISTS(SELECT 1 FROM sys.databases WHERE database_id=DB_ID() AND owner_sid=SUSER_SID()) THROW 51000,'Unowned disposable catalogue.',1;"+$Text
  $null=$command.ExecuteNonQuery()
 }finally{$connection.Dispose()}
}
$contract=Get-VectorPublicationMetadataMigrationContract
$sqlPath=(Resolve-Path -LiteralPath (Join-Path $SourceRoot 'scripts/deploy/vector-publication-metadata-up.sql')).Path
$read={Get-VectorPublicationMetadataDatabaseState $connectionText}
$initial=&$read
Write-Output 'Observed owned schema-backed successor.'
Check ((Get-VectorPublicationMetadataStateKind $initial $initial.Identity) -ceq 'New') 'Fixture must start at the schema-backed successor.'
Test-Sql "DROP INDEX [IX_Vectors_PublicationMetadata] ON dbo.Vectors; DELETE dbo.__EFMigrationsHistory WHERE MigrationId=N'20261101195325_AddVectorPublicationMetadata';"
$old=&$read;Check ((Get-VectorPublicationMetadataStateKind $old $initial.Identity) -ceq 'Old') 'Owned fixture downgrade did not establish exact predecessor.'
Write-Output 'Observed owned predecessor with index absent.'
Refuse {Invoke-VectorPublicationMetadataMigrationSql -ConnectionString $connectionText -SqlPath $sqlPath -ExpectedDatabaseIdentity 'foreign-incarnation'}
Check ((Get-VectorPublicationMetadataStateKind (&$read) $initial.Identity) -ceq 'Old') 'Wrong incarnation changed SQL.'
Test-Sql @'
EXEC(N'CREATE TRIGGER dbo.VectorMetadataOwnedFailure ON dbo.__EFMigrationsHistory AFTER INSERT AS
BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE MigrationId=N''20261101195325_AddVectorPublicationMetadata'') THROW 51000,''owned failure after index creation'',1; END;');
'@
try {
 $directory=Join-Path $ReceiptDirectory 'rollback';$null=New-Item -ItemType Directory -Path $directory
 $state=New-VectorPublicationMetadataMigrationState $old $directory 'owned-rollback' ('a'*40) $contract.UpSha256
 Refuse {Invoke-VectorPublicationMetadataMigrationAttempt $state $read {} {Invoke-VectorPublicationMetadataMigrationSql $connectionText $sqlPath $initial.Identity}}
 Check ($state.SettledState -ceq 'Old') 'History-insert failure did not roll back the already-created index atomically.'
} finally {Test-Sql 'DROP TRIGGER IF EXISTS dbo.VectorMetadataOwnedFailure;'}
$directory=Join-Path $ReceiptDirectory 'lost-ack';$null=New-Item -ItemType Directory -Path $directory
$state=New-VectorPublicationMetadataMigrationState (&$read) $directory 'owned-lost-ack' ('a'*40) $contract.UpSha256
Refuse {Invoke-VectorPublicationMetadataMigrationAttempt $state $read {} {
 Invoke-VectorPublicationMetadataMigrationSql $connectionText $sqlPath $initial.Identity
 throw 'owned lost acknowledgement after committed transaction'
}}
Check ($state.SettledState -ceq 'New' -and -not $state.Applied) 'Committed lost acknowledgement was not distinguished from rollback.'
$directory=Join-Path $ReceiptDirectory 'already-new';$null=New-Item -ItemType Directory -Path $directory
$state=New-VectorPublicationMetadataMigrationState (&$read) $directory 'owned-already-new' ('a'*40) $contract.UpSha256
Invoke-VectorPublicationMetadataMigrationAttempt $state $read {} {throw 'Already-complete state must perform zero DDL.'}
Invoke-VectorPublicationMetadataMigrationSql $connectionText $sqlPath $initial.Identity
Check $state.Applied 'Already-new attempt was not validated.'
Write-Output 'Owned SQL: incarnation refusal, atomic rollback, committed lost acknowledgement and zero-DDL reapply passed.'
