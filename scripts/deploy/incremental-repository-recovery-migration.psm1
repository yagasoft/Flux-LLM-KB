Set-StrictMode -Version Latest
if ($null -eq ('FluxKnowledge.Deployment.RetainedRowStream' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'repository-recovery-row-stream.cs')
}

function Get-RepositoryRecoveryMigrationContract {
    [pscustomobject]@{
        Baseline = '20261003220411_AddCanonicalCodeDisclosureProof'
        Target = '20261004175522_AddRepositoryRecoveryAuthority'
        ProductVersion = '10.0.10'
        BaselineHistorySha256 = 'E91CCAC2123532CD5A8B53A5536AD48C38F594BF71480309613B155CE8DBCF9D'
        UpSha256 = 'B4241C3083A57E258C648082A55470D221A4F60787AE3DE31DA941E03FA90F9C'
    }
}

function ConvertTo-RepositoryRecoveryCanonicalValue($Value) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [Collections.IDictionary] -or $Value -is [pscustomobject]) {
        $result = [ordered]@{}
        $names = if ($Value -is [Collections.IDictionary]) { @($Value.Keys) } else { @($Value.PSObject.Properties.Name) }
        foreach ($name in @($names | Sort-Object -CaseSensitive)) { $result[$name] = ConvertTo-RepositoryRecoveryCanonicalValue $Value.$name }
        return $result
    }
    $items = @($Value | ForEach-Object { ConvertTo-RepositoryRecoveryCanonicalValue $_ })
    return ,$items
}

function Get-RepositoryRecoveryValueHash($Value) {
    $json = ConvertTo-RepositoryRecoveryCanonicalValue $Value | ConvertTo-Json -Depth 60 -Compress
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json)))
}

function Invoke-RepositoryRecoveryScalar($Connection, $Transaction, [string]$Sql) {
    $command = $Connection.CreateCommand()
    try {
        if ($null -ne $Transaction) { $command.Transaction = $Transaction }
        $command.CommandTimeout = 120; $command.CommandText = $Sql
        return $command.ExecuteScalar()
    } finally { $command.Dispose() }
}

function ConvertFrom-RepositoryRecoveryJsonArray([string]$Json) {
    $value=$Json | ConvertFrom-Json -AsHashtable
    if ($null -eq $value) { return ,@() }
    return ,@($value)
}

function Read-RepositoryRecoveryDatabaseState($Connection, $Transaction) {
    $identityJson = Invoke-RepositoryRecoveryScalar $Connection $Transaction @'
SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')) AS ServerName,
    CONVERT(nvarchar(128), SERVERPROPERTY('MachineName')) AS MachineName,
    CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName')) AS InstanceName,
    DB_NAME() AS DatabaseName, CONVERT(nvarchar(36), database_guid) AS DatabaseGuid
FROM sys.database_recovery_status WHERE database_id = DB_ID()
FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER;
'@
    $identity = $identityJson | ConvertFrom-Json -AsHashtable
    if (!$identity -or !$identity.DatabaseGuid -or !$identity.ServerName -or !$identity.DatabaseName) {
        throw 'repository-recovery-database-incarnation-unavailable; require visibility of the actual database GUID.'
    }
    $historyJson = Invoke-RepositoryRecoveryScalar $Connection $Transaction @'
SELECT (SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory WITH (HOLDLOCK)
ORDER BY MigrationId FOR JSON PATH, INCLUDE_NULL_VALUES);
'@
    $schemaJson = Invoke-RepositoryRecoveryScalar $Connection $Transaction @'
SELECT (SELECT t.name AS TableName, c.name AS ColumnName, TYPE_NAME(c.user_type_id) AS TypeName,
    SCHEMA_NAME(ty.schema_id) AS TypeSchema,
    c.max_length AS MaxLength, c.is_nullable AS IsNullable, c.is_computed AS IsComputed,
    c.is_identity AS IsIdentity, dc.definition AS DefaultDefinition
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.columns c ON c.object_id=t.object_id
JOIN sys.types ty ON ty.user_type_id=c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
WHERE s.name=N'dbo' AND ((t.name=N'SourceRevisions' AND c.name=N'CurrentDiscoveryEvidenceJson')
    OR (t.name=N'PipelineRecords' AND c.name=N'RepositoryRecoveryBindingJson'))
ORDER BY t.name, c.name FOR JSON PATH, INCLUDE_NULL_VALUES);
'@
    return [ordered]@{
        Identity = $identity; History = ConvertFrom-RepositoryRecoveryJsonArray $historyJson
        Columns = ConvertFrom-RepositoryRecoveryJsonArray $schemaJson
        CanAlter = [int](Invoke-RepositoryRecoveryScalar $Connection $Transaction "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER');") -eq 1
        CanInsertHistory = [int](Invoke-RepositoryRecoveryScalar $Connection $Transaction "SELECT HAS_PERMS_BY_NAME('dbo.__EFMigrationsHistory', 'OBJECT', 'INSERT');") -eq 1
    }
}

function Get-RepositoryRecoveryDatabaseState {
    param([Parameter(Mandatory)][string]$ConnectionString)
    $connection = [Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open(); $transaction = $connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        try { $state = Read-RepositoryRecoveryDatabaseState $connection $transaction; $transaction.Commit(); return $state }
        finally { $transaction.Dispose() }
    } finally { $connection.Dispose() }
}

function Get-RepositoryRecoveryExpectedColumns {
    @('PipelineRecords|RepositoryRecoveryBindingJson', 'SourceRevisions|CurrentDiscoveryEvidenceJson') | ForEach-Object {
        $parts = $_.Split('|')
        [ordered]@{ TableName=$parts[0]; ColumnName=$parts[1]; TypeName='nvarchar'; TypeSchema='sys'; MaxLength=-1
            IsNullable=$true; IsComputed=$false; IsIdentity=$false; DefaultDefinition=$null }
    }
}

function Assert-RepositoryRecoveryMigrationBaseline {
    param([Parameter(Mandatory)]$DatabaseState, [switch]$RequireOriginal)
    $contract = Get-RepositoryRecoveryMigrationContract
    $history = @($DatabaseState.History)
    $priorIds=@($history | Where-Object MigrationId -CNE $contract.Target | ForEach-Object MigrationId)
    $historyHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($priorIds -join "`n"))))
    if ($historyHash -cne $contract.BaselineHistorySha256) { throw 'repository-recovery-schema-or-history-not-reviewed: migration prefix' }
    $old = $history.Count -gt 0 -and $history[-1].MigrationId -ceq $contract.Baseline -and
        @($DatabaseState.Columns).Count -eq 0 -and @($history | Where-Object MigrationId -CEQ $contract.Target).Count -eq 0
    $new = $history.Count -gt 1 -and $history[-2].MigrationId -ceq $contract.Baseline -and
        $history[-1].MigrationId -ceq $contract.Target -and $history[-1].ProductVersion -ceq $contract.ProductVersion -and
        (Get-RepositoryRecoveryValueHash @($DatabaseState.Columns)) -ceq (Get-RepositoryRecoveryValueHash @(Get-RepositoryRecoveryExpectedColumns))
    if (!$old -and (!$new -or $RequireOriginal)) { throw 'repository-recovery-schema-or-history-not-reviewed' }
    if ($old -and (!$DatabaseState.CanAlter -or !$DatabaseState.CanInsertHistory)) { throw 'repository-recovery-requires-ALTER-and-history-INSERT' }
}

function Get-RepositoryRecoveryTargetState {
    param([Parameter(Mandatory)]$OriginalState)
    Assert-RepositoryRecoveryMigrationBaseline $OriginalState -RequireOriginal
    $contract = Get-RepositoryRecoveryMigrationContract
    return [ordered]@{
        Identity=$OriginalState.Identity
        History=@($OriginalState.History) + @([ordered]@{ MigrationId=$contract.Target; ProductVersion=$contract.ProductVersion })
        Columns=@(Get-RepositoryRecoveryExpectedColumns)
        CanAlter=$OriginalState.CanAlter; CanInsertHistory=$OriginalState.CanInsertHistory
    }
}

function Assert-RepositoryRecoveryDatabaseMatch {
    param([Parameter(Mandatory)]$Actual, [Parameter(Mandatory)]$Expected)
    foreach ($field in @('Identity', 'History', 'Columns')) {
        if ((Get-RepositoryRecoveryValueHash $Actual.$field) -cne (Get-RepositoryRecoveryValueHash $Expected.$field)) {
            throw "repository-recovery-database-state-drift: $field"
        }
    }
}

function Invoke-RepositoryRecoveryMigrationAttempt {
    param([Parameter(Mandatory)][string]$ConnectionString, [Parameter(Mandatory)]$OriginalState,
        [Parameter(Mandatory)][string]$SqlPath, [scriptblock]$ExecuteBatch = { param($command) [void]$command.ExecuteNonQuery() })
    $contract = Get-RepositoryRecoveryMigrationContract
    $scriptBytes=[IO.File]::ReadAllBytes($SqlPath)
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($scriptBytes)) -cne $contract.UpSha256) { throw 'repository-recovery-up-script-hash-mismatch' }
    $sql=[Text.Encoding]::UTF8.GetString($scriptBytes).TrimStart([char]0xFEFF)
    $target = Get-RepositoryRecoveryTargetState $OriginalState
    $builder = [Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    $builder.Pooling=$false; $builder.ConnectRetryCount=0
    $connection = [Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    try {
        $connection.Open(); $transaction = $connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        try {
            [void](Invoke-RepositoryRecoveryScalar $connection $transaction @'
SET XACT_ABORT ON;
DECLARE @result int;
EXEC @result=sp_getapplock @Resource=N'FluxKnowledge.RepositoryRecovery.Migration.v1',
    @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
IF @result < 0 THROW 51000, 'repository-recovery-migration-lock-unavailable', 1;
SELECT 1;
'@)
            $current = Read-RepositoryRecoveryDatabaseState $connection $transaction
            if ((Get-RepositoryRecoveryValueHash $current.History) -ceq (Get-RepositoryRecoveryValueHash $target.History)) {
                Assert-RepositoryRecoveryDatabaseMatch $current $target
                $transaction.Commit(); return $current
            }
            Assert-RepositoryRecoveryDatabaseMatch $current $OriginalState
            Assert-RepositoryRecoveryMigrationBaseline $current -RequireOriginal
            $command = $connection.CreateCommand()
            try {
                $command.Transaction=$transaction; $command.CommandTimeout=120
                # The reviewed script has a nested BEGIN/COMMIT. The outer transaction
                # retains schema/history locks through exact post-state validation.
                $command.CommandText = [regex]::Replace($sql, '(?im)^\s*GO\s*$', '')
                & $ExecuteBatch $command | Out-Null
            } finally { $command.Dispose() }
            $after = Read-RepositoryRecoveryDatabaseState $connection $transaction
            Assert-RepositoryRecoveryDatabaseMatch $after $target
            $transaction.Commit(); return $after
        } catch {
            try { $transaction.Rollback() } catch { # XACT_ABORT may already have rolled it back.
            }
            throw
        } finally { $transaction.Dispose() }
    } finally { $connection.Dispose() }
}

function Get-RepositoryRecoveryRetainedTables {
    @('SourceIdentities','SourceRootConfigurations','SourceRootWatchStates','SourceScanRequests','SourceScanJobs','SourceScanOutbox',
        'SourceRevisions','SourceArtifacts','SourceActivities','SourceCapabilities','SourceProcessorBranches','SourceProcessorAttempts',
        'SourceProcessorForceRequests','SourceProcessorActionIgnoreHeads','SourceProcessorBranchMembers','SourceActivityRelations',
        'SourceProcessorCodeDocuments','SourceProcessorCodeSymbols','SourceProcessorCodeReferences','SourceProcessorCodeDiagnostics',
        'SourceProcessorCodeCompletionReceipts','SourceProcessorCodeBlockedDiagnostics','SourceDeletionOperations','SourceDeletionCleanupItems',
        'PipelineRecords','Jobs','JobAttempts','OutboxMessages','Artifacts','CanonicalCodeDisclosureProofs','CanonicalCodeDisclosureSpans',
        'DocumentPublications','TextChunks','Vectors','IndexGenerations','IndexGenerationVectors','IndexState',
        'CorpusRebuildOperations','CorpusRebuildWorkItems','CorpusRebuildSupersededJobs',
        'GpuMiniTasks','GpuBatches','GpuCapacitySlots','DocumentOcrRequests','EmbeddingGpuRequests',
        'GpuExecutorDispatches','GpuExecutorResultReceipts','GpuExecutorEvidence',
        'GpuSchedulerOperationReceipts','NativeWorkerInstances','NativeWorkerLifecycleEvidence')
}

function Read-RepositoryRecoveryProjection($Connection, [string]$Table) {
    # Table names come only from the fixed versioned list above. SQL identifiers
    # are quoted after metadata capture; callers cannot select arbitrary tables.
    $json = Invoke-RepositoryRecoveryScalar $Connection $null @"
SELECT (SELECT c.name AS Name, TYPE_NAME(c.user_type_id) AS TypeName, c.max_length AS MaxLength,
    c.precision AS [Precision], c.scale AS Scale, c.is_nullable AS IsNullable,
    c.is_computed AS IsComputed, c.is_identity AS IsIdentity, c.collation_name AS CollationName,
    dc.definition AS DefaultDefinition, cc.definition AS ComputedDefinition,
    ISNULL(pk.key_ordinal, 0) AS KeyOrdinal
FROM sys.columns c
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
LEFT JOIN (SELECT ic.object_id, ic.column_id, ic.key_ordinal FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id WHERE i.is_primary_key=1) pk
    ON pk.object_id=c.object_id AND pk.column_id=c.column_id
WHERE c.object_id=OBJECT_ID(N'dbo.$Table')
    AND NOT (N'$Table'=N'PipelineRecords' AND c.name=N'RepositoryRecoveryBindingJson')
    AND NOT (N'$Table'=N'SourceRevisions' AND c.name=N'CurrentDiscoveryEvidenceJson')
ORDER BY c.column_id FOR JSON PATH, INCLUDE_NULL_VALUES);
"@
    $columns = ConvertFrom-RepositoryRecoveryJsonArray $json
    if ($columns.Count -eq 0 -or @($columns | Where-Object KeyOrdinal -GT 0).Count -eq 0) { throw "repository-recovery-projection-missing-table-or-key: $Table" }
    return ,$columns
}

function Get-RepositoryRecoveryRetainedState {
    param([Parameter(Mandatory)][string]$ConnectionString, $Projection = $null,
        [scriptblock]$VerifyNewWorker = { param($row) throw 'repository-recovery-new-worker-ownership-unverified' })
    $tableNames = @(Get-RepositoryRecoveryRetainedTables)
    if ($null -ne $Projection -and ($Projection.Version -ne 1 -or
        (Get-RepositoryRecoveryValueHash @($Projection.Tables.Keys | Sort-Object)) -cne (Get-RepositoryRecoveryValueHash @($tableNames | Sort-Object)))) {
        throw 'repository-recovery-projection-schema-drift: version or table contract'
    }
    $connection = [Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open(); $columnsByTable=[ordered]@{}; $tables=[ordered]@{}; $preservedKeys=[ordered]@{}
        $appendTables=@('GpuSchedulerOperationReceipts','NativeWorkerInstances','NativeWorkerLifecycleEvidence')
        $verifiedNewWorkerIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($table in $tableNames) {
            $columns = Read-RepositoryRecoveryProjection $connection $table
            if ($null -ne $Projection -and (Get-RepositoryRecoveryValueHash $columns) -cne (Get-RepositoryRecoveryValueHash $Projection.Tables.$table)) {
                throw "repository-recovery-projection-schema-drift: $table"
            }
            $columnsByTable[$table]=$columns
            $keys=[Collections.Generic.List[string]]::new()
            $baselineKeys=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            if ($null -ne $Projection -and $table -cin $appendTables) {
                foreach ($key in @($Projection.PreservedKeys.$table)) { [void]$baselineKeys.Add($key); $keys.Add($key) }
            }
            $select = (@($columns | ForEach-Object { '[retained].[' + $_.Name.Replace(']', ']]') + ']' }) -join ',')
            $order = (@($columns | Where-Object KeyOrdinal -GT 0 | Sort-Object KeyOrdinal | ForEach-Object { '[retained].[' + $_.Name.Replace(']', ']]') + ']' }) -join ',')
            $hasher=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            $command=$connection.CreateCommand()
            try {
                $command.CommandTimeout=120
                $command.CommandText="SELECT (SELECT $select FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER) FROM [dbo].[$table] AS [retained] ORDER BY $order;"
                $reader=$command.ExecuteReader([Data.CommandBehavior]::SequentialAccess)
                try {
                    if ($table -cnotin $appendTables) {
                        $fingerprint=[FluxKnowledge.Deployment.RetainedRowStream]::Read($reader)
                        $tables[$table]=[ordered]@{ RowCount=$fingerprint.RowCount; Fingerprint=$fingerprint.Fingerprint }
                        continue
                    }
                    [long]$retainedCount=0
                    while ($reader.Read()) {
                        $rawRow=$reader.GetString(0)
                        $row=$rawRow | ConvertFrom-Json -AsHashtable
                        $keyName=if ($table -ceq 'NativeWorkerInstances') { 'InstanceId' } else { 'OperationId' }
                        $key=[string]$row.$keyName
                        if ($null -eq $Projection) { $keys.Add($key) }
                        elseif (!$baselineKeys.Contains($key)) {
                            switch ($table) {
                                'GpuSchedulerOperationReceipts' {
                                    $wake=$row.OperationKind -cin @('wake-consumption','wake-acknowledgement') -and
                                        $null -eq $row.BatchId -and $null -eq $row.AdmissionGeneration
                                    $noWork=$row.OperationKind -ceq 'admission' -and !$row.Accepted -and !$row.Committed -and
                                        $row.AdmissionDisposition -eq 1 -and $null -eq $row.DeferredUntilUtc -and $null -eq $row.AdmissionGeneration
                                    if ((!$wake -and !$noWork) -or $null -ne $row.CapacitySlotKey -or $null -ne $row.OwnerKey) {
                                        throw 'repository-recovery-unexpected-new-recovery-receipt'
                                    }
                                }
                                'NativeWorkerInstances' {
                                    if ($null -ne $row.ActiveDispatchId -or $null -ne $row.ExitedAtUtc -or $row.State -notin @(2,3,4)) {
                                        throw 'repository-recovery-new-worker-has-work-or-uncertain-outcome'
                                    }
                                    & $VerifyNewWorker $row | Out-Null
                                    [void]$verifiedNewWorkerIds.Add([string]$row.InstanceId)
                                }
                                'NativeWorkerLifecycleEvidence' {
                                    if (!$verifiedNewWorkerIds.Contains([string]$row.InstanceId) -or
                                        $row.LifecycleClass -notin @(0,2,3,4) -or $null -ne $row.OutcomeCode) {
                                        throw 'repository-recovery-unexpected-new-worker-lifecycle-evidence'
                                    }
                                }
                            }
                            continue
                        }
                        # Hash rows individually so allowed startup additions cannot
                        # change page boundaries of the preserved authority rows.
                        # Preserve SQL's bytes/offsets directly, rather than
                        # reserialising dates in the updater's local time zone.
                        $hasher.AppendData([Text.Encoding]::UTF8.GetBytes($rawRow))
                        $retainedCount++
                    }
                    $tables[$table]=[ordered]@{ RowCount=$retainedCount; Fingerprint=[Convert]::ToHexString($hasher.GetHashAndReset()) }
                } finally { $reader.Dispose() }
            } finally { $command.Dispose(); $hasher.Dispose() }
            if ($table -cin $appendTables) { $preservedKeys[$table]=@($keys) }
        }
        return [ordered]@{ Projection=[ordered]@{ Version=1; Tables=$columnsByTable; PreservedKeys=$preservedKeys }; Tables=$tables }
    } finally { $connection.Dispose() }
}

function Assert-RepositoryRecoveryRetainedState {
    param([Parameter(Mandatory)]$Baseline, [Parameter(Mandatory)]$Current)
    if ((Get-RepositoryRecoveryValueHash $Baseline) -cne (Get-RepositoryRecoveryValueHash $Current)) { throw 'repository-recovery-retained-state-changed; retain the hold and inspect table fingerprints.' }
}

function Assert-RepositoryRecoveryEvidenceEmpty {
    param([Parameter(Mandatory)][string]$ConnectionString)
    $connection=[Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open()
        if ([int](Invoke-RepositoryRecoveryScalar $connection $null @'
SELECT CASE WHEN EXISTS (SELECT 1 FROM SourceRevisions WHERE CurrentDiscoveryEvidenceJson IS NOT NULL)
    OR EXISTS (SELECT 1 FROM PipelineRecords WHERE RepositoryRecoveryBindingJson IS NOT NULL) THEN 1 ELSE 0 END;
'@) -ne 0) { throw 'repository-recovery-evidence-changed; no new authority may be written during held fresh migration validation.' }
    } finally { $connection.Dispose() }
}

Export-ModuleMember -Function Get-RepositoryRecoveryMigrationContract, Get-RepositoryRecoveryValueHash,
    Get-RepositoryRecoveryDatabaseState, Assert-RepositoryRecoveryMigrationBaseline, Get-RepositoryRecoveryTargetState,
    Assert-RepositoryRecoveryDatabaseMatch, Invoke-RepositoryRecoveryMigrationAttempt,
    Get-RepositoryRecoveryRetainedState, Assert-RepositoryRecoveryRetainedState, Assert-RepositoryRecoveryEvidenceEmpty
