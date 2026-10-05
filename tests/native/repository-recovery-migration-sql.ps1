[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot, [Parameter(Mandatory)][string]$SqlPath)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-repository-recovery-migration.psm1') -Force
$text = [Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_RECOVERY_DISPOSABLE_SQL')
$text = [regex]::Replace($text, '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=')
$text = [regex]::Replace($text, '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount=')
$builder = [Data.SqlClient.SqlConnectionStringBuilder]::new($text)
if ($builder.InitialCatalog -cnotmatch '^FluxKnowledge_Phase1Tests_[0-9a-f]{32}$') { throw 'Recovery SQL tests require a generated disposable catalogue.' }
$builder.Pooling = $false
$connectionText = $builder.ConnectionString
function Assert-True([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
function Expect-Failure([scriptblock]$Action, [string]$Reason) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true; if ($_.Exception.Message -notmatch $Reason) { throw } }
    Assert-True $failed "Expected refusal: $Reason"
}
function Invoke-TestSql([string]$Sql) {
    $connection = [Data.SqlClient.SqlConnection]::new($connectionText)
    try {
        $connection.Open(); $command = $connection.CreateCommand()
        try { $command.CommandText = $Sql; [void]$command.ExecuteNonQuery() } finally { $command.Dispose() }
    } finally { $connection.Dispose() }
}
$contract = Get-RepositoryRecoveryMigrationContract
Assert-True ((Get-FileHash -LiteralPath $SqlPath).Hash -ceq $contract.UpSha256) 'Generated SQL differs from the reviewed exact bytes.'
$before = Get-RepositoryRecoveryDatabaseState -ConnectionString $connectionText
Write-Output "Disposable recovery baseline: history=$($before.History.Count); last=$($before.History[-1].MigrationId); columns=$($before.Columns.Count)."
Assert-RepositoryRecoveryMigrationBaseline $before
$retained = Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText
# A reference V1 page implementation verifies identical persisted fingerprints,
# including SQL's binary encoding, rowversion, dates, escaping and composite keys.
foreach ($table in @('Artifacts','Vectors','IndexGenerationVectors','GpuSchedulerOperationReceipts')) {
    $columns=$retained.Projection.Tables.$table
    $select=(@($columns | ForEach-Object { '['+$_.Name.Replace(']',']]')+']' }) -join ',')
    $order=(@($columns | Where-Object KeyOrdinal -GT 0 | Sort-Object KeyOrdinal | ForEach-Object { '['+$_.Name.Replace(']',']]')+']' }) -join ',')
    $reference=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $referenceConnection=[Data.SqlClient.SqlConnection]::new($connectionText)
    try {
        $referenceConnection.Open(); [long]$offset=0
        do {
            $command=$referenceConnection.CreateCommand()
            try {
                $command.CommandText="SELECT (SELECT $select FROM [dbo].[$table] ORDER BY $order OFFSET @offset ROWS FETCH NEXT 128 ROWS ONLY FOR JSON PATH, INCLUDE_NULL_VALUES);"
                [void]$command.Parameters.Add('@offset',[Data.SqlDbType]::BigInt); $command.Parameters['@offset'].Value=$offset
                $document=[Text.Json.JsonDocument]::Parse([string]$command.ExecuteScalar())
                try {
                    $count=$document.RootElement.GetArrayLength()
                    foreach ($entry in $document.RootElement.EnumerateArray()) { $reference.AppendData([Text.Encoding]::UTF8.GetBytes($entry.GetRawText())) }
                    $offset+=$count
                } finally { $document.Dispose() }
            } finally { $command.Dispose() }
        } while ($count -eq 128)
        Assert-True ($offset -eq $retained.Tables.$table.RowCount -and [Convert]::ToHexString($reference.GetHashAndReset()) -ceq $retained.Tables.$table.Fingerprint) "Ordered streaming changed V1 proof in $table."
    } finally { $referenceConnection.Dispose(); $reference.Dispose() }
}
Write-Output 'Streaming V1 equivalence passed: vector bytes, rowversion, composite memberships, Unicode, escaping, large values and time-zone offsets.'
Invoke-TestSql "INSERT INTO __EFMigrationsHistory VALUES (N'20260801000000_Unreviewed',N'10.0.10');"
Expect-Failure { Assert-RepositoryRecoveryMigrationBaseline (Get-RepositoryRecoveryDatabaseState -ConnectionString $connectionText) } 'schema-or-history'
Invoke-TestSql "DELETE FROM __EFMigrationsHistory WHERE MigrationId=N'20260801000000_Unreviewed';"
Assert-True ($retained.Tables.Vectors.RowCount -eq 1 -and $retained.Tables.Jobs.RowCount -eq 1) 'Retained-state proof did not include the saved vector and job.'
Assert-True ($retained.Tables.GpuSchedulerOperationReceipts.RowCount -eq 130 -and $retained.Tables.NativeWorkerInstances.RowCount -eq 1) 'Paged proof omitted prior replay/worker evidence.'
foreach ($needle in @('ALTER TABLE [PipelineRecords]', 'INSERT INTO [__EFMigrationsHistory]')) {
    $fault = { param($command)
        $command.CommandText = $command.CommandText.Replace($needle, "THROW 51000, 'disposable-injected-fault', 1;`n$needle")
        [void]$command.ExecuteNonQuery()
    }.GetNewClosure()
    Expect-Failure { Invoke-RepositoryRecoveryMigrationAttempt -ConnectionString $connectionText -OriginalState $before -SqlPath $SqlPath -ExecuteBatch $fault } 'disposable-injected-fault'
    $afterFault = Get-RepositoryRecoveryDatabaseState -ConnectionString $connectionText
    Assert-RepositoryRecoveryDatabaseMatch -Actual $afterFault -Expected $before
    Assert-RepositoryRecoveryRetainedState -Baseline $retained -Current (Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText -Projection $retained.Projection)
}
$after = Invoke-RepositoryRecoveryMigrationAttempt -ConnectionString $connectionText -OriginalState $before -SqlPath $SqlPath
Assert-RepositoryRecoveryDatabaseMatch -Actual $after -Expected (Get-RepositoryRecoveryTargetState $before)
$neverExecute = { param($command) throw 'SQL must not be repeated' }
[void](Invoke-RepositoryRecoveryMigrationAttempt -ConnectionString $connectionText -OriginalState $before -SqlPath $SqlPath -ExecuteBatch $neverExecute)
Assert-RepositoryRecoveryRetainedState -Baseline $retained -Current (Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText -Projection $retained.Projection)
Assert-RepositoryRecoveryEvidenceEmpty -ConnectionString $connectionText
Invoke-TestSql @'
INSERT INTO GpuSchedulerOperationReceipts (OperationId,OperationKind,Accepted,Committed,WakeReasons,AdmissionDisposition,CreatedAtUtc)
VALUES (NEWID(),N'admission',0,0,1,1,SYSDATETIMEOFFSET());
'@
Assert-RepositoryRecoveryRetainedState -Baseline $retained -Current (Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText -Projection $retained.Projection)

# Only lifecycle evidence for workers verified during this pass may be additional.
# Insert the second worker after the worker page was read to force the real race.
$firstWorker=[Guid]::NewGuid().ToString(); $lateWorker=[Guid]::NewGuid().ToString()
Invoke-TestSql @"
INSERT INTO NativeWorkerInstances (InstanceId,ExecutorKey,ExecutableFingerprint,ProtocolVersion,State,LaunchedAtUtc)
VALUES ('$firstWorker',N'synthetic',REPLICATE(N'd',64),N'synthetic',2,SYSDATETIMEOFFSET());
INSERT INTO NativeWorkerLifecycleEvidence (OperationId,InstanceId,LifecycleClass,RequestFingerprint,ObservedAtUtc,CreatedAtUtc)
VALUES (NEWID(),'$firstWorker',2,REPLICATE(N'd',64),SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET());
"@
Expect-Failure { Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText -Projection $retained.Projection } 'new-worker-ownership-unverified'
$verified=@{Count=0}
$verify={ param($row) $verified.Count++ }.GetNewClosure()
Assert-RepositoryRecoveryRetainedState -Baseline $retained -Current (Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText -Projection $retained.Projection -VerifyNewWorker $verify)
Assert-True ($verified.Count -eq 1) 'Startup lifecycle evidence bypassed worker verification.'
$lateSql=@"
INSERT INTO NativeWorkerInstances (InstanceId,ExecutorKey,ExecutableFingerprint,ProtocolVersion,State,LaunchedAtUtc)
VALUES ('$lateWorker',N'synthetic',REPLICATE(N'd',64),N'synthetic',2,SYSDATETIMEOFFSET());
INSERT INTO NativeWorkerLifecycleEvidence (OperationId,InstanceId,LifecycleClass,RequestFingerprint,ObservedAtUtc,CreatedAtUtc)
VALUES (NEWID(),'$lateWorker',2,REPLICATE(N'd',64),SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET());
"@
$insertLate={ param($row)
    $testConnection=[Data.SqlClient.SqlConnection]::new($connectionText)
    try { $testConnection.Open(); $testCommand=$testConnection.CreateCommand()
        try { $testCommand.CommandText=$lateSql; [void]$testCommand.ExecuteNonQuery() } finally { $testCommand.Dispose() }
    } finally { $testConnection.Dispose() }
}.GetNewClosure()
Expect-Failure { Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText -Projection $retained.Projection -VerifyNewWorker $insertLate } 'unexpected-new-worker-lifecycle-evidence'
Invoke-TestSql "DELETE FROM NativeWorkerLifecycleEvidence WHERE InstanceId IN ('$firstWorker','$lateWorker'); DELETE FROM NativeWorkerInstances WHERE InstanceId IN ('$firstWorker','$lateWorker');"

# Real vector bytes, source identity and job state must change the proof, even if hashes/row counts do not.
foreach ($mutation in @('UPDATE Vectors SET [Values] = 0x02 + SUBSTRING([Values], 2, DATALENGTH([Values]));',
    "UPDATE SourceIdentities SET StableKey = StableKey + N'-changed';", 'UPDATE Jobs SET AttemptCount = AttemptCount + 1;',
    'UPDATE NativeWorkerInstances SET State=12;', 'DELETE FROM NativeWorkerLifecycleEvidence;',
    "DELETE FROM GpuSchedulerOperationReceipts WHERE CreatedAtUtc='1970-01-01';")) {
    $prior = Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText -Projection $retained.Projection
    Invoke-TestSql $mutation
    Expect-Failure { Assert-RepositoryRecoveryRetainedState -Baseline $prior -Current (Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText -Projection $retained.Projection) } 'retained-state-changed'
}
Invoke-TestSql 'ALTER TABLE PipelineRecords ADD UnreviewedColumn int NULL;'
Expect-Failure { Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText -Projection $retained.Projection } 'projection-schema-drift'
Invoke-TestSql 'ALTER TABLE PipelineRecords DROP COLUMN UnreviewedColumn;'
Invoke-TestSql 'UPDATE PipelineRecords SET RepositoryRecoveryBindingJson = N''{}'';'
Expect-Failure { Assert-RepositoryRecoveryEvidenceEmpty -ConnectionString $connectionText } 'recovery-evidence-changed'
Invoke-TestSql 'UPDATE PipelineRecords SET RepositoryRecoveryBindingJson = NULL;'

Invoke-TestSql 'ALTER TABLE SourceRevisions ADD CONSTRAINT DF_DisposableRecovery DEFAULT N''{}'' FOR CurrentDiscoveryEvidenceJson;'
Expect-Failure { Assert-RepositoryRecoveryMigrationBaseline (Get-RepositoryRecoveryDatabaseState -ConnectionString $connectionText) } 'schema-or-history'
Invoke-TestSql 'ALTER TABLE SourceRevisions DROP CONSTRAINT DF_DisposableRecovery;'
Invoke-TestSql 'ALTER TABLE PipelineRecords DROP COLUMN RepositoryRecoveryBindingJson;'
Expect-Failure { Invoke-RepositoryRecoveryMigrationAttempt -ConnectionString $connectionText -OriginalState $before -SqlPath $SqlPath } 'database-state-drift'
Write-Output 'Disposable SQL passed: both atomic faults, exact apply/duplicate, retained vector bytes, source/job changes, evidence and schema drift.'
