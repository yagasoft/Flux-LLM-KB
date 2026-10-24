param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'scripts/deploy/incremental-ocr-lifecycle-recovery.ps1')
$connectionString=$env:FLUXKNOWLEDGE_OCR_DISPOSABLE_SQL
$connectionString=$connectionString -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
$binding=[Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
if ($binding.InitialCatalog -notmatch '^FluxKnowledge_Phase1Tests_[0-9a-f]{32}$') { throw 'Disposable OCR database required.' }
$before=Get-OcrLifecycleDatabaseState $connectionString
$expected=@(Get-OcrReviewedMigrationPrefix $SourceRoot)
if (($expected -join '|') -cne ($before.History -join '|') -or $expected[0] -cne '20260726215521_InitialPhase1') {
    throw 'Reviewed migration metadata does not match the actual EF baseline.'
}
if ($env:FLUXKNOWLEDGE_OCR_SCHEMA_CAPTURE) { Write-OcrRecoveryJson ($env:FLUXKNOWLEDGE_OCR_SCHEMA_CAPTURE+'.baseline') $before }
$plan=@{DatabaseIdentity=$before.Identity;OriginalHistory=$before.History;MigrationSha256=(Get-OcrLifecycleMigrationContract).ScriptSha256}
function Refused([scriptblock]$Action) { $failed=$false;try { & $Action | Out-Null }catch{$failed=$true};if(!$failed){throw 'Unsafe migration accepted.'} }
$bad=$plan.Clone();$bad.DatabaseIdentity+='changed'
Refused { Invoke-OcrLifecycleMigration $connectionString $bad {} }
$bad=$plan.Clone();$bad.MigrationSha256='0'*64
Refused { Invoke-OcrLifecycleMigration $connectionString $bad {} }
$connection=[Data.SqlClient.SqlConnection]::new($connectionString)
try {
    $connection.Open()
    $active=Invoke-OcrScalar $connection 'SELECT ActiveBatchId FROM dbo.GpuCapacitySlots WHERE State=2;'
    $recoveryId=[Guid]::NewGuid()
    $originalBinding=if ($active -is [Guid]) { Get-OcrLegacyBinding $connectionString $active $recoveryId } else { $null }
    $retainedSql="SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),(SELECT MiniTaskId,ResultJson,CONVERT(varchar(64),ResultDigest,2) AS Digest FROM dbo.DocumentOcrRequests ORDER BY MiniTaskId FOR JSON PATH))),2);"
    $retainedBefore=Invoke-OcrScalar $connection $retainedSql
    Refused { Invoke-OcrLifecycleMigration $connectionString $plan {} { throw 'synthetic mid-DDL failure' } }
    if ((Get-OcrLifecycleDatabaseState $connectionString).SchemaSha256 -cne $before.SchemaSha256) { throw 'Partial migration escaped rollback.' }
    $after=Invoke-OcrLifecycleMigration $connectionString $plan {} -AfterCommit { throw 'synthetic lost migration commit response' }
    if (@($after.Schema.Columns).Count -ne 19 -or @($after.Schema.Constraints).Count -ne 5 -or
        (Invoke-OcrScalar $connection $retainedSql) -cne $retainedBefore) { throw 'Migration changed retained data or schema was incomplete.' }
    if ($null -ne $originalBinding) {
        $readBack=Get-OcrLegacyBinding $connectionString $active $recoveryId
        if (($readBack | ConvertTo-Json -Depth 10 -Compress) -cne ($originalBinding | ConvertTo-Json -Depth 10 -Compress)) {
            throw 'Actual planner reservation/result binding changed across migration.'
        }
    }
    [void](Invoke-OcrLifecycleMigration $connectionString $plan {})
    if ($env:FLUXKNOWLEDGE_OCR_SCHEMA_CAPTURE) { Write-OcrRecoveryJson $env:FLUXKNOWLEDGE_OCR_SCHEMA_CAPTURE $after }
    [void](Invoke-OcrScalar $connection 'ALTER TABLE dbo.DocumentOcrRequests ALTER COLUMN RecoveryReason nvarchar(129) COLLATE Latin1_General_100_BIN2 NULL;')
    Refused { Assert-OcrLifecycleSchema (Get-OcrLifecycleDatabaseState $connectionString) $plan }
    'Pinned OCR migration rehearsal passed; schema='+$after.SchemaSha256
} finally {
    [void](Invoke-OcrScalar $connection "IF COL_LENGTH('dbo.DocumentOcrRequests','RecoveryReason') IS NOT NULL ALTER TABLE dbo.DocumentOcrRequests ALTER COLUMN RecoveryReason nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL;")
    $connection.Dispose()
}
