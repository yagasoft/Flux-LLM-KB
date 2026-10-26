Set-StrictMode -Version Latest

function Get-SourceDeletionCleanupMigrationContract {
    [pscustomobject]@{
        Baseline='20261004175522_AddRepositoryRecoveryAuthority'
        Target='20261014134909_AddResumableSourceDeletionCleanup'
        UpSha256='644D3847B61C49EA57081E44E3C781DFFB226FA52619EB0CA2A75D7D3B3E273F'
        SchemaSha256='00FA188A700D3F97ABD746CEEF4FB7A9641917608AB4FC1AB8DAFDDFF97C439F'
    }
}

function Get-SourceDeletionCleanupDatabaseState {
    param([Parameter(Mandatory)][string]$ConnectionString)
    $connection=[System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open()
        $transaction=$connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        try {
            $command=$connection.CreateCommand()
            try {
                $command.Transaction=$transaction; $command.CommandTimeout=15
                # Preserve the legacy receipt identity. Journal composition also
                # needs the database GUID used by its own incarnation contract.
                $command.CommandText=@'
SELECT CONCAT(CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),N'|',DB_NAME(),N'|',DB_ID(),N'|',CONVERT(nvarchar(33),d.create_date,126)),
 CASE WHEN r.database_guid IS NOT NULL THEN CONCAT(CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),N'|',DB_NAME(),N'|',CONVERT(nvarchar(36),r.database_guid)) END
FROM sys.databases d LEFT JOIN sys.database_recovery_status r ON r.database_id=d.database_id
WHERE d.database_id=DB_ID();
'@
                $identityReader=$command.ExecuteReader()
                try {
                    if(!$identityReader.Read()){throw 'Source deletion database identity is unavailable.'}
                    $identity=$identityReader.GetString(0)
                    $incarnationIdentity=if($identityReader.IsDBNull(1)){$null}else{$identityReader.GetString(1)}
                } finally {$identityReader.Dispose()}
                $command.CommandText="SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER');"
                $canAlter=[int]$command.ExecuteScalar() -eq 1
                $command.CommandText='SELECT MigrationId FROM dbo.__EFMigrationsHistory WITH (HOLDLOCK) ORDER BY MigrationId;'
                $history=[Collections.Generic.List[string]]::new()
                $reader=$command.ExecuteReader()
                try { while ($reader.Read()) { $history.Add($reader.GetString(0)) } } finally { $reader.Dispose() }
                $command.CommandText=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source-deletion-cleanup-schema-contract.sql') -Raw
                $schemaJson=[string]$command.ExecuteScalar()
                $schema=$schemaJson | ConvertFrom-Json
                if (!$schema.PSObject.Properties['columns'] -or !$schema.PSObject.Properties['checks']) { throw "Incomplete source deletion schema observation: $schemaJson" }
                $schemaHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($schemaJson)))
                $command.CommandText='SELECT COUNT_BIG(*) FROM dbo.SourceDeletionOperations WHERE State IN (0,1);'
                $runnable=[long]$command.ExecuteScalar()
                # Explicit legacy projection stays identical across the additive migration.
                $command.CommandText=@'
SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(max), CONCAT(
 (SELECT Id,SourceRootId,State,Phase,ReasonCode,LeaseId,LeaseExpiresAtUtc,PipelineRecordCount,
  SourceArtifactCount,SharedArtifactCount,CreatedAtUtc,UpdatedAtUtc,RowVersion
  FROM dbo.SourceDeletionOperations ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES),
 (SELECT * FROM dbo.SourceDeletionCleanupItems ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES)))),2);
'@
                $legacyHash=[string]$command.ExecuteScalar()
                $initial=$true
                if (@($schema.columns).Count -eq 5) {
                    $command.CommandText='SELECT COUNT_BIG(*) FROM dbo.SourceDeletionOperations WHERE LastCommittedLeaseId IS NOT NULL OR LastProgressAtUtc IS NOT NULL OR MembershipRowsDeleted<>0 OR MembershipVectorCursor IS NOT NULL OR ResumePhase IS NOT NULL;'
                    $initial=[long]$command.ExecuteScalar() -eq 0
                }
                $transaction.Commit()
                [pscustomobject]@{ Identity=$identity; IncarnationIdentity=$incarnationIdentity; History=@($history); CanAlter=$canAlter;
                    ColumnCount=@($schema.columns).Count; CheckCount=@($schema.checks).Count;
                    SchemaSha256=$schemaHash; SchemaJson=$schemaJson; SchemaValid=($schemaHash -ceq (Get-SourceDeletionCleanupMigrationContract).SchemaSha256);
                    RunnableCount=$runnable; LegacySha256=$legacyHash; ProgressIsInitial=$initial }
            } finally { $command.Dispose() }
        } finally { $transaction.Dispose() }
    } finally { $connection.Dispose() }
}

function Test-SourceDeletionCleanupHistory($Actual,$Expected) {
    [string]::Equals(($Actual -join "`n"),($Expected -join "`n"),[StringComparison]::Ordinal)
}

function Assert-SourceDeletionCleanupApplicationSchema {
    param([Parameter(Mandatory)]$DatabaseState)
    $contract=Get-SourceDeletionCleanupMigrationContract
    if (!$DatabaseState.SchemaValid -or @($DatabaseState.History)[-1] -cne $contract.Target) {
        throw 'The candidate requires the complete resumable source deletion schema. Use the separately approved -ApplyResumableSourceDeletionCleanupMigration path; an app-only update is unsafe.'
    }
}

function Assert-SourceDeletionCleanupMigrationBaseline {
    param([Parameter(Mandatory)]$DatabaseState)
    $contract=Get-SourceDeletionCleanupMigrationContract
    $history=@($DatabaseState.History)
    $old=$history.Count -gt 0 -and $history[-1] -ceq $contract.Baseline -and
        $DatabaseState.ColumnCount -eq 0 -and $DatabaseState.CheckCount -eq 0 -and $history -cnotcontains $contract.Target
    if (!$old) { throw "Source deletion cleanup migration requires the exact prior history and wholly absent additive schema; partial, unexpected or already migrated state requires separate review. Observed last=$($history[-1]); columns=$($DatabaseState.ColumnCount); checks=$($DatabaseState.CheckCount)." }
    if ($DatabaseState.RunnableCount -ne 0) { throw 'Source deletion cleanup migration requires zero queued or leased deletion operations.' }
    if (!$DatabaseState.CanAlter) { throw 'Source deletion cleanup migration requires DATABASE ALTER permission.' }
}

function Write-SourceDeletionCleanupReceipt {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][string]$Name)
    $path=Join-Path $State.ReceiptDirectory $Name
    $receipt=[ordered]@{ RecordedAtUtc=[DateTime]::UtcNow.ToString('O'); Binding=$State.Binding;
        OriginalHistory=$State.OriginalHistory; ExpectedHistory=$State.ExpectedHistory; OriginalLegacySha256=$State.OriginalLegacySha256 }
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($receipt | ConvertTo-Json -Depth 12))
    # CreateNew prevents replay/overwriting evidence. Flush(true) precedes every irreversible boundary.
    $stream=[IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

function New-SourceDeletionCleanupMigrationState {
    param([Parameter(Mandatory)]$DatabaseState,[Parameter(Mandatory)][string]$ReceiptDirectory,
        [Parameter(Mandatory)][hashtable]$Binding)
    Assert-SourceDeletionCleanupMigrationBaseline $DatabaseState
    if (!(Test-Path -LiteralPath $ReceiptDirectory -PathType Container) -or
        ((Get-Item -LiteralPath $ReceiptDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Migration receipt directory is missing or unsafe.' }
    foreach ($key in @('ReleaseId','Commit','WebPayloadSha256','CompanionPayloadSha256','SqlSha256')) {
        if (!$Binding.ContainsKey($key) -or [string]::IsNullOrWhiteSpace($Binding[$key])) { throw "Migration binding is missing $key." }
    }
    if ($Binding.SqlSha256 -cne (Get-SourceDeletionCleanupMigrationContract).UpSha256) { throw 'Migration binding SQL hash differs from the reviewed script.' }
    $Binding.DatabaseIdentity=$DatabaseState.Identity
    $Binding.SchemaSha256=(Get-SourceDeletionCleanupMigrationContract).SchemaSha256
    $state=@{ ReceiptDirectory=$ReceiptDirectory; Binding=$Binding; OriginalHistory=@($DatabaseState.History);
        ExpectedHistory=(@($DatabaseState.History)+(Get-SourceDeletionCleanupMigrationContract).Target);
        OriginalLegacySha256=$DatabaseState.LegacySha256; Attempted=$false; Applied=$false; ForwardRecoveryRequired=$false }
    Write-SourceDeletionCleanupReceipt -State $state -Name 'source-deletion-migration-prepared.json'
    $state
}

function Invoke-SourceDeletionCleanupMigrationAttempt {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][scriptblock]$ReadState,
        [Parameter(Mandatory)][scriptblock]$RunUp)
    # The payload swap calls StopApplication again on failure. Never replay SQL.
    if ($State.Attempted) { return }
    $before=& $ReadState
    Assert-SourceDeletionCleanupMigrationBaseline $before
    if ($before.Identity -cne $State.Binding.DatabaseIdentity -or
        !(Test-SourceDeletionCleanupHistory $before.History $State.OriginalHistory) -or
        $before.LegacySha256 -cne $State.OriginalLegacySha256) { throw 'Source deletion database/history/operation state changed after preflight.' }
    $State.Attempted=$true
    Write-SourceDeletionCleanupReceipt -State $State -Name 'source-deletion-migration-attempt.json'
    & $RunUp
    Assert-SourceDeletionCleanupActivation -State $State -ReadState $ReadState
    $State.Applied=$true
    Write-SourceDeletionCleanupReceipt -State $State -Name 'source-deletion-migration-verified.json'
}

function Assert-SourceDeletionCleanupActivation {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][scriptblock]$ReadState)
    $current=& $ReadState
    Assert-SourceDeletionCleanupApplicationSchema $current
    if ($current.Identity -cne $State.Binding.DatabaseIdentity -or
        !(Test-SourceDeletionCleanupHistory $current.History $State.ExpectedHistory) -or
        $current.RunnableCount -ne 0 -or $current.LegacySha256 -cne $State.OriginalLegacySha256 -or !$current.ProgressIsInitial) {
        throw 'Source deletion activation requires the same database, exact target history, unchanged legacy operations/targets, initial progress fields and zero queued/leased deletions.'
    }
}

function Invoke-SourceDeletionCleanupHoldRelease {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][scriptblock]$ReleaseHold)
    if (!$State.Applied -or $State.ForwardRecoveryRequired) { throw 'Hold release requires verified migration and must not replay an ambiguous release.' }
    Write-SourceDeletionCleanupReceipt -State $State -Name 'source-deletion-hold-release-intent.json'
    $State.ForwardRecoveryRequired=$true
    & $ReleaseHold
    Write-SourceDeletionCleanupReceipt -State $State -Name 'source-deletion-hold-released.json'
}

Export-ModuleMember -Function Get-SourceDeletionCleanupMigrationContract,Get-SourceDeletionCleanupDatabaseState,
    Assert-SourceDeletionCleanupApplicationSchema,Assert-SourceDeletionCleanupMigrationBaseline,
    New-SourceDeletionCleanupMigrationState,Invoke-SourceDeletionCleanupMigrationAttempt,
    Assert-SourceDeletionCleanupActivation,Invoke-SourceDeletionCleanupHoldRelease
