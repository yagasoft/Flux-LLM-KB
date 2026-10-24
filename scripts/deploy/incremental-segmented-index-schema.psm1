Set-StrictMode -Version Latest

function Get-SegmentedIndexMigrationContract {
    [pscustomobject]@{
        Baseline='20261014134909_AddResumableSourceDeletionCleanup'
        Migrations=@('20261020183242_AddSegmentedIndexPublication','20261020185203_AddSegmentedPreviewWorklists',
            '20261020192553_AddIndexRetirementIntents','20261020195833_AddSegmentedSourceDeletionTargets',
            '20261020212959_AddBoundedSegmentMerge','20261020222343_AddExplicitProjectionRebuild',
            '20261021010911_AddIndexRecoveryReferenceAudit')
        UpSha256='53601EA1AE3CB906860B0E09940F6059A4EF9442D27E4877FDBE85F7BBD3E0B3'
        BaselineSchemaSha256='C2DE9A69E1274D83CEFCF69B02A87FB9BD9D0B82E1BB3C640F1A17DC81B90005'
        SchemaSha256='097C5B7BEEE7CB87E7FA5FD913D96464A2C4DD976D736A9911C2D0A45460DC10'
        CompatibleApplicationSuccessor='20261024061418_AddDocumentOcrExecutionLifecycle'
    }
}

function Open-SegmentedSchemaConnection([string]$ConnectionString) {
    $normalised=$ConnectionString -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
    $builder=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
    $builder['Pooling']=$false;$builder['Enlist']=$false;$builder['ConnectRetryCount']=0;$builder['Connect Timeout']=5
    $builder['Application Name']='FluxKnowledge.SegmentedSchemaUpgrade'
    if([string]::IsNullOrWhiteSpace($builder.DataSource) -or [string]::IsNullOrWhiteSpace($builder.InitialCatalog) -or
        $builder.InitialCatalog -iin @('master','model','msdb','tempdb')){throw 'index-schema-database-invalid'}
    $connection=[Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    try{$connection.Open();return $connection}catch{$connection.Dispose();throw}
}

function Read-SegmentedSchemaState($Connection,$Transaction=$null) {
    $command=$Connection.CreateCommand()
    try {
        $command.CommandTimeout=10;$command.Transaction=$Transaction
        $command.CommandText="SELECT CONCAT(CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),N'|',DB_NAME(),N'|',CONVERT(nvarchar(36),database_guid)) FROM sys.database_recovery_status WHERE database_id=DB_ID();"
        $identity=[string]$command.ExecuteScalar()
        if(!$identity){throw 'index-schema-database-identity-unavailable'}
        $command.CommandText='SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId;'
        $history=[Collections.Generic.List[string]]::new();$reader=$command.ExecuteReader()
        try{while($reader.Read()){$history.Add($reader.GetString(0))}}finally{$reader.Dispose()}
        $command.CommandText=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'segmented-index-schema-contract.sql'))
        $schema=[string]$command.ExecuteScalar()
        $schemaHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($schema)))
        $shape=$schema | ConvertFrom-Json
        $command.CommandText=@'
IF COL_LENGTH(N'dbo.IndexState',N'RequiredIndexFormat') IS NULL SELECT 1;
ELSE EXEC sys.sp_executesql N'SELECT RequiredIndexFormat FROM dbo.IndexState WHERE Id=1;';
'@
        $format=$command.ExecuteScalar()
        $command.CommandText=@'
IF OBJECT_ID(N'dbo.IndexRecoveryWriters',N'U') IS NULL SELECT -1;
ELSE EXEC sys.sp_executesql N'SELECT COUNT(*) FROM dbo.IndexRecoveryWriters;';
'@
        $writers=[int]$command.ExecuteScalar()
        $command.CommandText=@'
IF OBJECT_ID(N'dbo.IndexRecoveryAudit',N'U') IS NULL SELECT 0;
ELSE EXEC sys.sp_executesql N'SELECT COUNT(*) FROM dbo.IndexRecoveryAudit WHERE Id=1;';
'@
        $audits=[int]$command.ExecuteScalar()
        [pscustomobject]@{Identity=$identity;History=$history.ToArray();SchemaSha256=$schemaHash;
            AddedColumnCount=@($shape.columns).Count;RequiredFormat=$format;WriterCount=$writers;AuditSingletonCount=$audits}
    }finally{$command.Dispose()}
}

function Get-SegmentedIndexDatabaseState {
    param([Parameter(Mandatory)][string]$ConnectionString)
    $connection=Open-SegmentedSchemaConnection $ConnectionString
    try{Read-SegmentedSchemaState $connection}finally{$connection.Dispose()}
}

function Assert-SegmentedIndexApplicationSchema {
    param([Parameter(Mandatory)]$State)
    $contract=Get-SegmentedIndexMigrationContract
    $suffix=@($contract.Baseline)+@($contract.Migrations)
    $history=@($State.History)
    # This reviewed additive OCR migration does not change the index contract.
    # Keep the original index upgrade script and reject every other successor.
    if ($history.Count -gt $suffix.Count -and $history[-1] -ceq $contract.CompatibleApplicationSuccessor) {
        $history=@($history[0..($history.Count-2)])
    }
    if($history.Count -lt $suffix.Count -or ($history[($history.Count-$suffix.Count)..($history.Count-1)] -join '|') -cne ($suffix -join '|') -or
        $State.SchemaSha256 -cne $contract.SchemaSha256 -or $State.AuditSingletonCount -ne 1 -or $State.RequiredFormat -notin @(1,2)) {
        throw ('index-schema-reconciliation-required; observed-schema='+$State.SchemaSha256)
    }
}

function Invoke-SegmentedIndexSchemaUpgrade {
    param([Parameter(Mandatory)][string]$ConnectionString,[Parameter(Mandatory)][string]$ExpectedIdentity,
        [Parameter(Mandatory)][string[]]$ExpectedHistory,[Parameter(Mandatory)][scriptblock]$AssertQuiescent)
    $contract=Get-SegmentedIndexMigrationContract
    if(!$ExpectedHistory.Count -or $ExpectedHistory[-1] -cne $contract.Baseline){throw 'index-schema-reviewed-baseline-required'}
    $path=Join-Path $PSScriptRoot 'segmented-index-schema-up.sql'
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $contract.UpSha256){throw 'index-schema-reviewed-script-changed'}
    $sql=[IO.File]::ReadAllText($path)
    $connection=Open-SegmentedSchemaConnection $ConnectionString
    $transaction=$null
    try {
        & $AssertQuiescent
        $command=$connection.CreateCommand()
        try {
            $command.CommandTimeout=10
            $command.CommandText=@'
DECLARE @r int;
EXEC @r=sys.sp_getapplock @Resource=N'FluxKnowledge.RecoveryReferences',@LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=2000;
IF @r<0 THROW 51000,'index-schema-recovery-writer-busy',1;
EXEC @r=sys.sp_getapplock @Resource=N'FluxKnowledge.DerivedIndexRecovery',@LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=2000;
IF @r<0 THROW 51000,'index-schema-index-owner-busy',1;
'@
            [void]$command.ExecuteNonQuery()
            $transaction=$connection.BeginTransaction([Data.IsolationLevel]::Serializable);$command.Transaction=$transaction
            $command.CommandText='SET LOCK_TIMEOUT 5000; SELECT Id FROM dbo.IndexState WITH(UPDLOCK,HOLDLOCK) WHERE Id=1;'
            if([int]$command.ExecuteScalar() -ne 1){throw 'index-schema-publication-fence-unavailable'}
            $before=Read-SegmentedSchemaState $connection $transaction
            if($before.Identity -cne $ExpectedIdentity){throw 'index-schema-database-binding-changed'}
            $complete=@($ExpectedHistory)+@($contract.Migrations)
            if(($before.History -join '|') -ceq ($complete -join '|')) {
                Assert-SegmentedIndexApplicationSchema $before
                $transaction.Commit()
                return [pscustomobject]@{AlreadyApplied=$true;Identity=$before.Identity;Target=$complete[-1];SchemaSha256=$before.SchemaSha256}
            }
            if(($before.History -join '|') -cne ($ExpectedHistory -join '|') -or $before.SchemaSha256 -cne $contract.BaselineSchemaSha256 -or $before.AddedColumnCount -ne 0 -or
                $before.AuditSingletonCount -ne 0 -or $before.WriterCount -ne -1) {throw 'index-schema-partial-or-unreviewed-state'}
            # Keep all seven EF transactions nested inside one original outer
            # transaction. A failed batch/session cannot commit a partial upgrade.
            # Constraints and DML must compile after preceding ALTER ADDs when
            # these tables have never exposed the new columns. Keep the original
            # outer transaction across all generated migration batches.
            foreach($batch in [regex]::Split($sql,'(?m)^GO\s*\r?$|(?=^ALTER TABLE \[[^\]]+\] ADD CONSTRAINT|^UPDATE \[IndexState\])') | Where-Object {! [string]::IsNullOrWhiteSpace($_)}) {
                & $AssertQuiescent
                $command.CommandText=@'
IF APPLOCK_MODE(N'public',N'FluxKnowledge.RecoveryReferences',N'Session')<>N'Exclusive'
 OR APPLOCK_MODE(N'public',N'FluxKnowledge.DerivedIndexRecovery',N'Session')<>N'Exclusive'
 OR XACT_STATE()<>1 THROW 51000,'index-schema-original-session-lost',1;
'@ + "`n" + $batch
                [void]$command.ExecuteNonQuery()
            }
            & $AssertQuiescent
            $after=Read-SegmentedSchemaState $connection $transaction
            Assert-SegmentedIndexApplicationSchema $after
            if(($after.History -join '|') -cne ($complete -join '|') -or $after.RequiredFormat -ne 1 -or $after.WriterCount -ne 0){throw 'index-schema-initial-state-invalid'}
            $transaction.Commit()
            [pscustomobject]@{AlreadyApplied=$false;Identity=$after.Identity;Target=$complete[-1];SchemaSha256=$after.SchemaSha256}
        }finally{$command.Dispose()}
    }finally{if($transaction){$transaction.Dispose()};$connection.Dispose()}
}

Export-ModuleMember -Function Get-SegmentedIndexMigrationContract,Get-SegmentedIndexDatabaseState,
    Assert-SegmentedIndexApplicationSchema,Invoke-SegmentedIndexSchemaUpgrade
