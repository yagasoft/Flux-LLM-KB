[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$ReceiptDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$connectionText=$env:FLUXKNOWLEDGE_JOURNAL_DISPOSABLE_SQL -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' `
 -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
$binding=[Data.SqlClient.SqlConnectionStringBuilder]::new($connectionText)
if(!$binding.DataSource -or $binding.InitialCatalog -cnotmatch '^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$'){
 throw 'Only the generated disposable migration-test catalogue is allowed.'
}
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-migration.psm1') -Force
$before=Get-SourceJournalDatabaseState -ConnectionString $binding.ConnectionString
Assert-SourceJournalSchema $before
if(@($before.History).Count -ne 79){throw 'The actual metadata-successor schema is required.'}
$contract=Get-SourceJournalMigrationContract
$id='20261102T000000Z-aaaaaaaaaaaa';$release=Join-Path $ReceiptDirectory $id
$null=New-Item -ItemType Directory -Path $release
$hold=Join-Path $ReceiptDirectory 'hold.json'
[IO.File]::WriteAllText($hold,($id|ConvertTo-Json -Compress))
$state=New-SourceJournalMigrationState -DatabaseState $before -ReceiptDirectory $release -ReleaseId $id -SqlSha256 $contract.UpSha256
Invoke-SourceJournalMigrationAttempt -State $state -ReadState {Get-SourceJournalDatabaseState -ConnectionString $binding.ConnectionString} -AssertStopped {} -RunUp {throw 'Already-applied migration was replayed.'}
$path=Get-SourceJournalHeldMigrationIntent -HoldPath $hold -RecoveryRoot $ReceiptDirectory -ExpectedDatabaseIdentity $before.Identity
if($path -cne $state.IntentPath){throw 'Already-applied SQL successor intent did not round trip.'}
$intent=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
if($intent.OriginalVectorPublicationIndexSha256 -cne $before.VectorPublicationIndexSha256 -or
 ($intent.ExpectedHistory -join '|') -cne ($before.History -join '|')){throw 'Persisted successor evidence changed.'}
$after=Get-SourceJournalDatabaseState -ConnectionString $binding.ConnectionString
if($after.Identity -cne $before.Identity -or $after.SchemaSha256 -cne $before.SchemaSha256 -or
 $after.VectorPublicationIndexSha256 -cne $before.VectorPublicationIndexSha256 -or
 ($after.History -join '|') -cne ($before.History -join '|')){throw 'SQL schema changed during the receipt round trip.'}
Write-Output 'Actual 79-entry SQL journal intent round trip passed; no replay or production calls.'
