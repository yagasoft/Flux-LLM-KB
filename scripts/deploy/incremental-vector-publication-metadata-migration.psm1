Set-StrictMode -Version Latest

function Get-VectorPublicationMetadataHash([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)))
}

function Get-VectorPublicationMetadataMigrationContract {
    $columns=@([ordered]@{Name='VectorId';KeyOrdinal=1;Included=$false;Descending=$false})
    foreach($name in @('TextChunkId','SourceRevision','PayloadChecksum','TextChunkContentHash','SearchInputHash','IsDeleted')){
        $columns+=[ordered]@{Name=$name;KeyOrdinal=0;Included=$true;Descending=$false}
    }
    $definition=[ordered]@{Type=2;Unique=$false;Filtered=$false;Disabled=$false;Hypothetical=$false;Columns=$columns}
    $json='['+($definition|ConvertTo-Json -Depth 5 -Compress)+']'
    [pscustomobject]@{
        Baseline='20261025124025_PreserveSourceVerificationReadCounts'
        Target='20261101195325_AddVectorPublicationMetadata'
        PriorHistoryCount=78
        PriorHistorySha256='6D9B7A0E6C591C189D10FF474E198DF992EEC178534C188D5DA88A27BB9B6F34'
        JournalSchemaSha256='287B7F32C73251714682BA952F001B4CD67B096D57A211A80E1E1AE8EF89ED8F'
        JournalQuerySha256='4FAA55351825ECF82F73ED01006832C13CFC393C53BB12A0108C09C8E5006CB1'
        UpSha256='B5765D327FE5AE08066A84E1F101C771B900E261C655D67B80448223203CE184'
        IndexSha256=(Get-VectorPublicationMetadataHash $json)
        AbsentIndexSha256=(Get-VectorPublicationMetadataHash '[]')
        TimeoutSeconds=120
    }
}

function Get-VectorPublicationMetadataIndexSha256 {
    param([Parameter(Mandatory)]$Connection,$Transaction=$null)
    $command=$Connection.CreateCommand()
    try {
        $command.Transaction=$Transaction;$command.CommandTimeout=10
        $command.CommandText=@'
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Vectors') AND name=N'IX_Vectors_PublicationMetadata') SELECT N'[]';
ELSE SELECT index_info.type AS [Type],index_info.is_unique AS [Unique],index_info.has_filter AS [Filtered],index_info.is_disabled AS [Disabled],index_info.is_hypothetical AS [Hypothetical],
 JSON_QUERY((SELECT column_info.name AS [Name],membership.key_ordinal AS [KeyOrdinal],membership.is_included_column AS [Included],membership.is_descending_key AS [Descending]
 FROM sys.index_columns membership JOIN sys.columns column_info ON column_info.object_id=membership.object_id AND column_info.column_id=membership.column_id
 WHERE membership.object_id=index_info.object_id AND membership.index_id=index_info.index_id ORDER BY membership.index_column_id FOR JSON PATH)) AS [Columns]
FROM sys.indexes index_info WHERE index_info.object_id=OBJECT_ID(N'dbo.Vectors') AND index_info.name=N'IX_Vectors_PublicationMetadata' FOR JSON PATH;
'@
        $json=[string]$command.ExecuteScalar()
        if(-not $json){throw 'vector-metadata-index-observation-missing'}
        Get-VectorPublicationMetadataHash $json
    } finally {$command.Dispose()}
}

function Get-VectorPublicationMetadataStateKind {
    param([Parameter(Mandatory)]$DatabaseState,[Parameter(Mandatory)][string]$ExpectedDatabaseIdentity)
    $contract=Get-VectorPublicationMetadataMigrationContract;$history=@($DatabaseState.History)
    if(-not $ExpectedDatabaseIdentity -or $DatabaseState.Identity -cne $ExpectedDatabaseIdentity -or
        $DatabaseState.ResolvesDbo -cne $true -or
        $DatabaseState.SchemaSha256 -cne $contract.JournalSchemaSha256){throw 'vector-metadata-database-or-journal-schema-changed'}
    if($history.Count -notin @($contract.PriorHistoryCount,($contract.PriorHistoryCount+1))){throw 'vector-metadata-history-not-exact'}
    $prefix=@($history[0..($contract.PriorHistoryCount-1)])
    if($prefix[-1] -cne $contract.Baseline -or (Get-VectorPublicationMetadataHash ($prefix -join "`n")) -cne $contract.PriorHistorySha256){throw 'vector-metadata-predecessor-history-changed'}
    if($history.Count -eq $contract.PriorHistoryCount -and $DatabaseState.VectorPublicationIndexSha256 -ceq $contract.AbsentIndexSha256){return 'Old'}
    if($history.Count -eq ($contract.PriorHistoryCount+1) -and $history[-1] -ceq $contract.Target -and $DatabaseState.VectorPublicationIndexSha256 -ceq $contract.IndexSha256){return 'New'}
    throw 'vector-metadata-partial-or-unreviewed-state-retain-hold'
}

function Read-VectorPublicationMetadataSqlText([string]$Path,[string]$ExpectedSha256,[string]$Failure) {
    if(-not [IO.Path]::IsPathFullyQualified($Path)){throw 'vector-metadata-absolute-sql-path-required'}
    for($cursor=$Path;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint){throw 'vector-metadata-sql-path-linked'}
    }
    $file=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if($file.Length -lt 1 -or $file.Length -gt 1048576){throw 'vector-metadata-sql-size-invalid'}
        $bytes=[byte[]]::new([int]$file.Length);$file.ReadExactly($bytes)
        if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -cne $ExpectedSha256){throw $Failure}
        [Text.UTF8Encoding]::new($false,$true).GetString($bytes).TrimStart([char]0xfeff)
    } finally {$file.Dispose()}
}

function Read-VectorPublicationMetadataConnectionState {
    param([Parameter(Mandatory)]$Connection,[Parameter(Mandatory)]$Transaction)
    $command=$Connection.CreateCommand()
    try {
        $command.Transaction=$Transaction;$command.CommandTimeout=10
        $command.CommandText="SELECT CONCAT(CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),N'|',DB_NAME(),N'|',CONVERT(nvarchar(36),database_guid)) FROM sys.database_recovery_status WHERE database_id=DB_ID();"
        $identity=[string]$command.ExecuteScalar()
        $command.CommandText="SELECT CASE WHEN OBJECT_ID(N'Vectors')=OBJECT_ID(N'dbo.Vectors') AND OBJECT_ID(N'__EFMigrationsHistory')=OBJECT_ID(N'dbo.__EFMigrationsHistory') AND OBJECT_ID(N'dbo.Vectors') IS NOT NULL AND OBJECT_ID(N'dbo.__EFMigrationsHistory') IS NOT NULL THEN 1 ELSE 0 END;"
        $resolvesDbo=$command.ExecuteScalar() -eq 1
        $command.CommandText="SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER');"
        $canAlter=$command.ExecuteScalar() -eq 1
        $command.CommandText='SELECT MigrationId FROM dbo.__EFMigrationsHistory WITH (HOLDLOCK) ORDER BY MigrationId;'
        $history=[Collections.Generic.List[string]]::new();$reader=$command.ExecuteReader()
        try{while($reader.Read()){$history.Add($reader.GetString(0))}}finally{$reader.Dispose()}
        $query=Join-Path $PSScriptRoot 'source-journal-schema-contract.sql'
        $command.CommandText=Read-VectorPublicationMetadataSqlText $query (Get-VectorPublicationMetadataMigrationContract).JournalQuerySha256 'vector-metadata-journal-query-changed'
        $schema=[string]$command.ExecuteScalar()
        if(-not $schema){throw 'vector-metadata-journal-schema-observation-missing'}
        [pscustomobject]@{Identity=$identity;History=$history.ToArray();CanAlter=$canAlter;ResolvesDbo=$resolvesDbo;
            SchemaSha256=(Get-VectorPublicationMetadataHash $schema);
            VectorPublicationIndexSha256=(Get-VectorPublicationMetadataIndexSha256 -Connection $Connection -Transaction $Transaction)}
    } finally {$command.Dispose()}
}

function New-VectorPublicationMetadataConnection([string]$ConnectionString) {
    $normalised=$ConnectionString -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' `
        -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
    $binding=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
    if(-not $binding.DataSource -or -not $binding.InitialCatalog -or $binding.InitialCatalog -iin @('master','model','msdb','tempdb')){throw 'vector-metadata-database-target-invalid'}
    $binding['Pooling']=$false;$binding['Enlist']=$false;$binding['ConnectRetryCount']=0;$binding['Connect Timeout']=5
    $binding['Application Name']='FluxKnowledge.VectorPublicationMetadataMigration'
    [Data.SqlClient.SqlConnection]::new($binding.ConnectionString)
}

function Get-VectorPublicationMetadataDatabaseState {
    param([Parameter(Mandatory)][string]$ConnectionString)
    $connection=New-VectorPublicationMetadataConnection $ConnectionString
    try {
        $connection.Open();$transaction=$connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        try {$state=Read-VectorPublicationMetadataConnectionState $connection $transaction;$transaction.Commit();$state}
        finally {$transaction.Dispose()}
    } finally {$connection.Dispose()}
}

function Invoke-VectorPublicationMetadataMigrationSql {
    param([Parameter(Mandatory)][string]$ConnectionString,[Parameter(Mandatory)][string]$SqlPath,
        [Parameter(Mandatory)][string]$ExpectedDatabaseIdentity)
    $contract=Get-VectorPublicationMetadataMigrationContract
    $text=Read-VectorPublicationMetadataSqlText $SqlPath $contract.UpSha256 'vector-metadata-up-sql-changed'
    $connection=New-VectorPublicationMetadataConnection $ConnectionString
    try {
        $connection.Open()
        $transaction=$connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        try {
            $before=Read-VectorPublicationMetadataConnectionState $connection $transaction
            $kind=Get-VectorPublicationMetadataStateKind $before $ExpectedDatabaseIdentity
            if(-not $before.ResolvesDbo){throw 'vector-metadata-unqualified-objects-do-not-resolve-to-dbo'}
            if($kind -ceq 'New'){$transaction.Commit();return}
            if(-not $before.CanAlter){throw 'vector-metadata-ddl-connection-alter-permission-required'}
            $timer=[Diagnostics.Stopwatch]::StartNew()
            foreach($batch in ([regex]::Split($text,'(?im)^\s*GO\s*(?:--.*)?$')|Where-Object{-not [string]::IsNullOrWhiteSpace($_)})){
                $remaining=$contract.TimeoutSeconds-[int][Math]::Ceiling($timer.Elapsed.TotalSeconds)
                if($remaining -le 0){throw 'vector-metadata-migration-time-budget-exhausted'}
                $command=$connection.CreateCommand()
                try{$command.Transaction=$transaction;$command.CommandTimeout=$remaining;$command.CommandText=$batch;$null=$command.ExecuteNonQuery()}
                finally{$command.Dispose()}
            }
            if((Get-VectorPublicationMetadataStateKind (Read-VectorPublicationMetadataConnectionState $connection $transaction) $ExpectedDatabaseIdentity) -cne 'New'){throw 'vector-metadata-transaction-successor-invalid'}
            $transaction.Commit()
        } catch {try{$transaction.Rollback()}catch{};throw}
        finally {$transaction.Dispose()}
    } finally {$connection.Dispose()}
}

function Write-VectorPublicationMetadataRecord([string]$Path,$Value) {
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 12 -Compress))
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read,4096,[IO.FileOptions]::WriteThrough)
    try{$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
}

function New-VectorPublicationMetadataMigrationState {
    param([Parameter(Mandatory)]$DatabaseState,[Parameter(Mandatory)][string]$ReceiptDirectory,
        [Parameter(Mandatory)][string]$ReleaseId,[Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$SqlSha256,[string]$ResumeIntentSha256='',
        [string]$ResumeReleaseBindingSha256='')
    $contract=Get-VectorPublicationMetadataMigrationContract
    $kind=Get-VectorPublicationMetadataStateKind $DatabaseState $DatabaseState.Identity
    if($SqlSha256 -cne $contract.UpSha256 -or $Commit -cnotmatch '^[a-f0-9]{40}$' -or $ReleaseId -cnotmatch '^[A-Za-z0-9-]{1,100}$'){throw 'vector-metadata-release-sql-binding-invalid'}
    if($kind -ceq 'Old' -and $DatabaseState.CanAlter -cne $true){throw 'vector-metadata-alter-permission-required'}
    if(-not [IO.Path]::IsPathFullyQualified($ReceiptDirectory) -or [IO.Path]::GetFullPath($ReceiptDirectory) -cne $ReceiptDirectory){throw 'vector-metadata-absolute-receipt-path-required'}
    for($path=$ReceiptDirectory;$path;$path=[IO.Path]::GetDirectoryName($path)){
        if(-not(Test-Path -LiteralPath $path) -or ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint)){throw 'vector-metadata-receipt-path-unsafe'}
    }
    if($ResumeIntentSha256 -and $ResumeIntentSha256 -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'vector-metadata-resume-intent-hash-invalid'}
    if($ResumeReleaseBindingSha256 -and $ResumeReleaseBindingSha256 -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'vector-metadata-resume-release-binding-hash-invalid'}
    $intent=Join-Path $ReceiptDirectory 'vector-publication-metadata-migration-intent.json'
    if(Test-Path -LiteralPath $intent){throw 'vector-metadata-existing-intent-requires-reconciliation'}
    @{
        Version=1;Purpose='vector-publication-metadata-migration';ReleaseId=$ReleaseId;Commit=$Commit;Identity=$DatabaseState.Identity
        Target=$contract.Target;SqlSha256=$SqlSha256;InitialKind=$kind;OriginalHistory=@($DatabaseState.History)
        IntentPath=$intent;OutcomePath=(Join-Path $ReceiptDirectory 'vector-publication-metadata-migration-outcome.json')
        ResumeIntentSha256=$ResumeIntentSha256;ResumeReleaseBindingSha256=$ResumeReleaseBindingSha256
        Attempted=$false;Applied=$false;SettledState='Unattempted'
    }
}

function Confirm-VectorPublicationMetadataMigrationOutcome {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][scriptblock]$ReadState)
    $State.SettledState='Unknown'
    $State.SettledState=Get-VectorPublicationMetadataStateKind (&$ReadState) $State.Identity
    $State.SettledState
}

function Invoke-VectorPublicationMetadataMigrationAttempt {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][scriptblock]$ReadState,
        [Parameter(Mandatory)][scriptblock]$AssertStopped,[Parameter(Mandatory)][scriptblock]$RunUp)
    if($State.Attempted){throw 'vector-metadata-attempt-requires-reconciliation-not-replay'}
    &$AssertStopped
    $before=&$ReadState
    if((Get-VectorPublicationMetadataStateKind $before $State.Identity) -cne $State.InitialKind -or
        (@($before.History) -join '|') -cne (@($State.OriginalHistory) -join '|')){throw 'vector-metadata-preflight-state-changed'}
    $State.Attempted=$true
    Write-VectorPublicationMetadataRecord $State.IntentPath $State
    $State.IntentSha256=(Get-FileHash -LiteralPath $State.IntentPath -Algorithm SHA256).Hash
    try {
        if($State.InitialKind -ceq 'Old'){&$RunUp}
        if((Confirm-VectorPublicationMetadataMigrationOutcome $State $ReadState) -cne 'New'){throw 'vector-metadata-successor-not-established'}
        $State.Applied=$true
        Write-VectorPublicationMetadataRecord $State.OutcomePath @{Identity=$State.Identity;Target=$State.Target;IntentSha256=$State.IntentSha256;SettledState=$State.SettledState;Applied=$true}
    } catch {
        $failure=$_
        try{$null=Confirm-VectorPublicationMetadataMigrationOutcome $State $ReadState}catch{$State.SettledState='Unknown'}
        if(-not(Test-Path -LiteralPath $State.OutcomePath)){
            Write-VectorPublicationMetadataRecord $State.OutcomePath @{Identity=$State.Identity;Target=$State.Target;IntentSha256=$State.IntentSha256;SettledState=$State.SettledState;Applied=$false;Error=$failure.Exception.Message}
        }
        throw $failure
    }
}

Export-ModuleMember -Function Get-VectorPublicationMetadataMigrationContract,Get-VectorPublicationMetadataHash,
    Get-VectorPublicationMetadataIndexSha256,Get-VectorPublicationMetadataStateKind,Get-VectorPublicationMetadataDatabaseState,
    Invoke-VectorPublicationMetadataMigrationSql,New-VectorPublicationMetadataMigrationState,
    Invoke-VectorPublicationMetadataMigrationAttempt,Confirm-VectorPublicationMetadataMigrationOutcome
