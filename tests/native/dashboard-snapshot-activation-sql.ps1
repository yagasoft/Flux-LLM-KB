param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$ExpectedDatabase)
$ErrorActionPreference='Stop'
$text=$env:FLUXKNOWLEDGE_DASHBOARD_DISPOSABLE_SQL -replace '(?i)(^|;)\s*Trust Server Certificate\s*=','$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=','$1ConnectRetryCount='
$target=[Data.SqlClient.SqlConnectionStringBuilder]::new($text)
if($target.InitialCatalog -cne $ExpectedDatabase -or $ExpectedDatabase -cnotmatch '^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$'){throw 'Owned disposable database required.'}
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-schema.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-dashboard-snapshots.psm1') -Force
$before=Get-SegmentedIndexDatabaseState -ConnectionString $target.ConnectionString
$option=Get-DashboardSnapshotDatabaseState -ConnectionString $target.ConnectionString
if($option.Identity -cne $before.Identity -or $option.SnapshotState -ne 0){throw 'Fresh disposable OFF baseline required.'}
$binding=@{DatabaseIdentity=$option.Identity;InitialSnapshotState=0;IntendedSnapshotState=1;RcsiEnabled=$option.RcsiEnabled;AdrEnabled=$option.AdrEnabled}
$state=@{};$journal=[Collections.Generic.List[string]]::new()
Invoke-DashboardSnapshotActivation -State $state -Binding $binding -ReadState {Get-DashboardSnapshotDatabaseState $target.ConnectionString} `
    -Enable {Enable-DashboardSnapshotDatabaseOption -ConnectionString $target.ConnectionString -Binding $binding} `
    -WriteState {$journal.Add(($state|ConvertTo-Json -Depth 10 -Compress))}
Assert-DashboardSnapshotActivation $state $binding -ReadState {Get-DashboardSnapshotDatabaseState $target.ConnectionString}
if($journal.Count -lt 2 -or $state.DashboardSnapshotActivation.LastObserved.SnapshotState -ne 1){throw 'Actual activation was not observed.'}
$already=@{};$binding.InitialSnapshotState=1
Invoke-DashboardSnapshotActivation -State $already -Binding $binding -ReadState {Get-DashboardSnapshotDatabaseState $target.ConnectionString} `
    -Enable {throw 'Already ON must not issue ALTER.'} -WriteState {}
$bad=$binding.Clone();$bad.DatabaseIdentity='wrong-incarnation'
$refused=$false;try{Enable-DashboardSnapshotDatabaseOption -ConnectionString $target.ConnectionString -Binding $bad}catch{if($_.Exception.Message -notmatch 'binding-changed'){throw};$refused=$true}
if(!$refused){throw 'Actual ALTER accepted another database binding.'}
$after=Get-SegmentedIndexDatabaseState -ConnectionString $target.ConnectionString
foreach($key in @('Identity','SchemaSha256','RequiredFormat')){if($before.$key -cne $after.$key){throw ('Unexpected schema change: '+$key)}}
if(($before.History -join '|') -cne ($after.History -join '|')){throw 'Migration history changed.'}
Assert-DashboardSnapshotDatabaseBinding $binding (Get-DashboardSnapshotDatabaseState $target.ConnectionString) -RequireOn
# The predecessor's ordinary read-committed transaction remains usable with ON.
$connection=[Data.SqlClient.SqlConnection]::new($target.ConnectionString)
try {
    $connection.Open();$transaction=$connection.BeginTransaction([Data.IsolationLevel]::ReadCommitted)
    try{$command=$connection.CreateCommand();$command.Transaction=$transaction;$command.CommandText='SELECT COUNT(*) FROM dbo.Jobs';[void]$command.ExecuteScalar();$command.Dispose();$transaction.Commit()}finally{$transaction.Dispose()}
}finally{$connection.Dispose()}
@{Status='PASS';ProductionAccess=$false;ExpectedDatabase=$ExpectedDatabase;SnapshotState=1;SchemaAndHistoryUnchanged=$true;RcsiAndAdrUnchanged=$true}|ConvertTo-Json
