param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'scripts/deploy/incremental-ocr-lifecycle-recovery.ps1')
$connection=$env:FLUXKNOWLEDGE_OCR_DISPOSABLE_SQL -replace '(?i)Trust Server Certificate=','TrustServerCertificate=' -replace '(?i)Connect Retry Count=','ConnectRetryCount='
$builder=[Data.SqlClient.SqlConnectionStringBuilder]::new($connection)
if($builder.InitialCatalog -cnotmatch '^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$'){throw 'Disposable SQL binding required.'}
function Assert-Refused([scriptblock]$Action,[string]$Reason){
    try{& $Action|Out-Null}catch{if($_.Exception.Message.Contains($Reason)){return};throw}
    throw "Expected refusal: $Reason"
}
$state=Get-OcrLifecycleDatabaseState $connection
$plan=@{Version=2;Mode='CleanupOnly';DatabaseIdentity=$state.Identity;OriginalHistory=@(Get-OcrReviewedMigrationPrefix $SourceRoot -All)}
if(!(Assert-OcrLifecycleSchema $state $plan -RequireOriginal)){throw 'Schema80 not accepted.'}
$before=$state|ConvertTo-Json -Depth 20 -Compress
# The normal migration entry point must return without executing any DDL.
$stops=0
$after=Invoke-OcrLifecycleMigration $connection $plan {$script:stops++} {throw 'DDL must not execute.'}
if($stops -ne 1 -or ($after|ConvertTo-Json -Depth 20 -Compress) -cne $before){throw 'Cleanup-only changed schema.'}
$drift=@{};foreach($key in $state.Keys){$drift[$key]=$state[$key]};$drift.History=@($state.History)[0..78]
Assert-Refused {Assert-OcrLifecycleSchema $drift $plan} 'schema-or-history-changed'
$drift=@{};foreach($key in $state.Keys){$drift[$key]=$state[$key]};$drift.SchemaSha256='0'*64
Assert-Refused {Assert-OcrLifecycleSchema $drift $plan} 'schema-or-history-changed'
$plan.Mode='CompleteAndRequeue'
Assert-Refused {Assert-OcrLifecycleSchema $state $plan} 'schema-or-history-changed'
$plan.Mode='CleanupOnly'
$sql=[Data.SqlClient.SqlConnection]::new($connection)
try{$sql.Open();$batch=Invoke-OcrScalar $sql 'SELECT TOP(1) Id FROM GpuBatches WHERE State=3 ORDER BY Id;'}finally{$sql.Dispose()}
if($batch -is [Guid]){
    $binding=Get-OcrTerminalCleanupBinding $connection $batch ([Guid]::NewGuid())
    if(!$binding.SourceRootId -or $binding.Contains('ResultDigest') -or [Convert]::FromBase64String($binding.RequestRowVersion).Length -ne 8){throw 'Terminal binding contract invalid.'}
    Assert-Refused {Get-OcrTerminalCleanupBinding $connection ([Guid]::NewGuid()) ([Guid]::NewGuid())} 'terminal-binding-not-exact'
}
'Cleanup-only contracts passed: exact current history/schema, no DDL and terminal binding without a result digest.'
