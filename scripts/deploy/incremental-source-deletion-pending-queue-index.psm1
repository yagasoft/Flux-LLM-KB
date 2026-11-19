Set-StrictMode -Version Latest

function Get-SourceDeletionPendingQueueHash([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)))
}

function Get-SourceDeletionPendingQueueContract {
    $columns=@();$ordinal=0
    foreach($name in @('SourceDeletionOperationId','State','StorageKind','Id')){
        $columns+=[ordered]@{Name=$name;KeyOrdinal=(++$ordinal);Included=$false;Descending=$false}
    }
    $definition=[ordered]@{Type=2;Unique=$false;Filtered=$false;Disabled=$false;Hypothetical=$false;Columns=$columns}
    $json='['+($definition|ConvertTo-Json -Depth 5 -Compress)+']'
    [pscustomobject]@{
        Target='IX_SourceDeletionCleanupItems_PendingQueue'
        HistoryCount=79
        HistorySha256='E5B03C2F5DF66E0E9DEAEBE209E5B61A5DE32D92D27113A2EC8CB511BAAB2B06'
        UpSha256='C247F3429F303F959583F0BC59683A1F208B29B86EA509DD4638CE3CED23707E'
        IndexSha256=(Get-SourceDeletionPendingQueueHash $json)
        AbsentIndexSha256=(Get-SourceDeletionPendingQueueHash '[]')
        TimeoutSeconds=120
    }
}

function Get-SourceDeletionPendingQueueIndexSha256 {
    param([Parameter(Mandatory)]$Connection,$Transaction=$null)
    $command=$Connection.CreateCommand()
    try {
        $command.Transaction=$Transaction;$command.CommandTimeout=10
        $command.CommandText=@'
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SourceDeletionCleanupItems') AND name=N'IX_SourceDeletionCleanupItems_PendingQueue') SELECT N'[]';
ELSE SELECT index_info.type AS [Type],index_info.is_unique AS [Unique],index_info.has_filter AS [Filtered],index_info.is_disabled AS [Disabled],index_info.is_hypothetical AS [Hypothetical],
 JSON_QUERY((SELECT column_info.name AS [Name],membership.key_ordinal AS [KeyOrdinal],membership.is_included_column AS [Included],membership.is_descending_key AS [Descending]
 FROM sys.index_columns membership JOIN sys.columns column_info ON column_info.object_id=membership.object_id AND column_info.column_id=membership.column_id
 WHERE membership.object_id=index_info.object_id AND membership.index_id=index_info.index_id ORDER BY membership.index_column_id FOR JSON PATH)) AS [Columns]
FROM sys.indexes index_info WHERE index_info.object_id=OBJECT_ID(N'dbo.SourceDeletionCleanupItems') AND index_info.name=N'IX_SourceDeletionCleanupItems_PendingQueue' FOR JSON PATH;
'@
        $json=[string]$command.ExecuteScalar()
        if(-not $json){throw 'pending-queue-index-index-observation-missing'}
        Get-SourceDeletionPendingQueueHash $json
    } finally {$command.Dispose()}
}

function Get-SourceDeletionPendingQueueStateKind {
    param([Parameter(Mandatory)]$DatabaseState,[Parameter(Mandatory)][string]$ExpectedDatabaseIdentity)
    $contract=Get-SourceDeletionPendingQueueContract;$history=@($DatabaseState.History)
    if(-not $ExpectedDatabaseIdentity -or $DatabaseState.Identity -cne $ExpectedDatabaseIdentity -or
        $DatabaseState.ResolvesDbo -cne $true){throw 'pending-queue-index-database-changed'}
    if($history.Count -ne $contract.HistoryCount -or
        (Get-SourceDeletionPendingQueueHash ($history -join "`n")) -cne $contract.HistorySha256){throw 'pending-queue-index-history-not-exact'}
    if($DatabaseState.IndexSha256 -ceq $contract.AbsentIndexSha256){return 'Old'}
    if($DatabaseState.IndexSha256 -ceq $contract.IndexSha256){return 'New'}
    throw 'pending-queue-index-partial-or-unreviewed-state-retain-hold'
}

function Read-SourceDeletionPendingQueueSqlText([string]$Path,[string]$ExpectedSha256,[string]$Failure) {
    if(-not [IO.Path]::IsPathFullyQualified($Path)){throw 'pending-queue-index-absolute-sql-path-required'}
    for($cursor=$Path;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint){throw 'pending-queue-index-sql-path-linked'}
    }
    $file=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if($file.Length -lt 1 -or $file.Length -gt 1048576){throw 'pending-queue-index-sql-size-invalid'}
        $bytes=[byte[]]::new([int]$file.Length);$file.ReadExactly($bytes)
        if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -cne $ExpectedSha256){throw $Failure}
        [Text.UTF8Encoding]::new($false,$true).GetString($bytes).TrimStart([char]0xfeff)
    } finally {$file.Dispose()}
}

function Read-SourceDeletionPendingQueueConnectionState {
    param([Parameter(Mandatory)]$Connection,[Parameter(Mandatory)]$Transaction)
    $command=$Connection.CreateCommand()
    try {
        $command.Transaction=$Transaction;$command.CommandTimeout=10
        $command.CommandText="SELECT CONCAT(CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),N'|',DB_NAME(),N'|',CONVERT(nvarchar(36),database_guid)) FROM sys.database_recovery_status WHERE database_id=DB_ID();"
        $identity=[string]$command.ExecuteScalar()
        $command.CommandText="SELECT CASE WHEN OBJECT_ID(N'SourceDeletionCleanupItems')=OBJECT_ID(N'dbo.SourceDeletionCleanupItems') AND OBJECT_ID(N'__EFMigrationsHistory')=OBJECT_ID(N'dbo.__EFMigrationsHistory') AND OBJECT_ID(N'dbo.SourceDeletionCleanupItems') IS NOT NULL AND OBJECT_ID(N'dbo.__EFMigrationsHistory') IS NOT NULL THEN 1 ELSE 0 END;"
        $resolvesDbo=$command.ExecuteScalar() -eq 1
        $command.CommandText="SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER');"
        $canAlter=$command.ExecuteScalar() -eq 1
        $command.CommandText='SELECT MigrationId FROM dbo.__EFMigrationsHistory WITH (HOLDLOCK) ORDER BY MigrationId;'
        $history=[Collections.Generic.List[string]]::new();$reader=$command.ExecuteReader()
        try{while($reader.Read()){$history.Add($reader.GetString(0))}}finally{$reader.Dispose()}
        [pscustomobject]@{Identity=$identity;History=$history.ToArray();CanAlter=$canAlter;ResolvesDbo=$resolvesDbo;
            IndexSha256=(Get-SourceDeletionPendingQueueIndexSha256 -Connection $Connection -Transaction $Transaction)}
    } finally {$command.Dispose()}
}

function New-SourceDeletionPendingQueueConnection([string]$ConnectionString) {
    $normalised=$ConnectionString -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' `
        -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
    $binding=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
    if(-not $binding.DataSource -or -not $binding.InitialCatalog -or $binding.InitialCatalog -iin @('master','model','msdb','tempdb')){throw 'pending-queue-index-database-target-invalid'}
    $binding['Pooling']=$false;$binding['Enlist']=$false;$binding['ConnectRetryCount']=0;$binding['Connect Timeout']=5
    $binding['Application Name']='FluxKnowledge.SourceDeletionPendingQueue'
    [Data.SqlClient.SqlConnection]::new($binding.ConnectionString)
}

function Get-SourceDeletionPendingQueueDatabaseState {
    param([Parameter(Mandatory)][string]$ConnectionString)
    $connection=New-SourceDeletionPendingQueueConnection $ConnectionString
    try {
        $connection.Open();$transaction=$connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        try {$state=Read-SourceDeletionPendingQueueConnectionState $connection $transaction;$transaction.Commit();$state}
        finally {$transaction.Dispose()}
    } finally {$connection.Dispose()}
}

function Invoke-SourceDeletionPendingQueueSql {
    param([Parameter(Mandatory)][string]$ConnectionString,[Parameter(Mandatory)][string]$SqlPath,
        [Parameter(Mandatory)][string]$ExpectedDatabaseIdentity)
    $contract=Get-SourceDeletionPendingQueueContract
    $text=Read-SourceDeletionPendingQueueSqlText $SqlPath $contract.UpSha256 'pending-queue-index-up-sql-changed'
    $connection=New-SourceDeletionPendingQueueConnection $ConnectionString
    try {
        $connection.Open()
        $transaction=$connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        try {
            $before=Read-SourceDeletionPendingQueueConnectionState $connection $transaction
            $kind=Get-SourceDeletionPendingQueueStateKind $before $ExpectedDatabaseIdentity
            if(-not $before.ResolvesDbo){throw 'pending-queue-index-unqualified-objects-do-not-resolve-to-dbo'}
            if($kind -ceq 'New'){$transaction.Commit();return}
            if(-not $before.CanAlter){throw 'pending-queue-index-ddl-connection-alter-permission-required'}
            $timer=[Diagnostics.Stopwatch]::StartNew()
            foreach($batch in ([regex]::Split($text,'(?im)^\s*GO\s*(?:--.*)?$')|Where-Object{-not [string]::IsNullOrWhiteSpace($_)})){
                $remaining=$contract.TimeoutSeconds-[int][Math]::Ceiling($timer.Elapsed.TotalSeconds)
                if($remaining -le 0){throw 'pending-queue-index-migration-time-budget-exhausted'}
                $command=$connection.CreateCommand()
                try{$command.Transaction=$transaction;$command.CommandTimeout=$remaining;$command.CommandText=$batch;$null=$command.ExecuteNonQuery()}
                finally{$command.Dispose()}
            }
            if((Get-SourceDeletionPendingQueueStateKind (Read-SourceDeletionPendingQueueConnectionState $connection $transaction) $ExpectedDatabaseIdentity) -cne 'New'){throw 'pending-queue-index-transaction-successor-invalid'}
            $transaction.Commit()
        } catch {try{$transaction.Rollback()}catch{};throw}
        finally {$transaction.Dispose()}
    } finally {$connection.Dispose()}
}

function Write-SourceDeletionPendingQueueRecord([string]$Path,$Value) {
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 12 -Compress))
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read,4096,[IO.FileOptions]::WriteThrough)
    try{$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
}

function New-SourceDeletionPendingQueueState {
    param([Parameter(Mandatory)]$DatabaseState,[Parameter(Mandatory)][string]$ReceiptDirectory,
        [Parameter(Mandatory)][string]$ReleaseId,[Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$SqlSha256)
    $contract=Get-SourceDeletionPendingQueueContract
    $kind=Get-SourceDeletionPendingQueueStateKind $DatabaseState $DatabaseState.Identity
    if($SqlSha256 -cne $contract.UpSha256 -or $Commit -cnotmatch '^[a-f0-9]{40}$' -or $ReleaseId -cnotmatch '^[A-Za-z0-9-]{1,100}$'){throw 'pending-queue-index-release-sql-binding-invalid'}
    if($kind -ceq 'Old' -and $DatabaseState.CanAlter -cne $true){throw 'pending-queue-index-alter-permission-required'}
    if(-not [IO.Path]::IsPathFullyQualified($ReceiptDirectory) -or [IO.Path]::GetFullPath($ReceiptDirectory) -cne $ReceiptDirectory){throw 'pending-queue-index-absolute-receipt-path-required'}
    for($path=$ReceiptDirectory;$path;$path=[IO.Path]::GetDirectoryName($path)){
        if(-not(Test-Path -LiteralPath $path) -or ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint)){throw 'pending-queue-index-receipt-path-unsafe'}
    }
    $intent=Join-Path $ReceiptDirectory 'source-deletion-pending-queue-index-intent.json'
    if(Test-Path -LiteralPath $intent){throw 'pending-queue-index-existing-intent-requires-reconciliation'}
    @{
        Version=1;Purpose='source-deletion-pending-queue-index';ReleaseId=$ReleaseId;Commit=$Commit;Identity=$DatabaseState.Identity
        Target=$contract.Target;SqlSha256=$SqlSha256;InitialKind=$kind;OriginalHistory=@($DatabaseState.History)
        IntentPath=$intent;OutcomePath=(Join-Path $ReceiptDirectory 'source-deletion-pending-queue-index-outcome.json')
        Attempted=$false;Applied=$false;SettledState='Unattempted'
    }
}

function Confirm-SourceDeletionPendingQueueOutcome {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][scriptblock]$ReadState)
    $State.SettledState='Unknown'
    $State.SettledState=Get-SourceDeletionPendingQueueStateKind (&$ReadState) $State.Identity
    $State.SettledState
}

function Invoke-SourceDeletionPendingQueueAttempt {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][scriptblock]$ReadState,
        [Parameter(Mandatory)][scriptblock]$AssertStopped,[Parameter(Mandatory)][scriptblock]$RunUp)
    if($State.Attempted){throw 'pending-queue-index-attempt-requires-reconciliation-not-replay'}
    &$AssertStopped
    $before=&$ReadState
    if((Get-SourceDeletionPendingQueueStateKind $before $State.Identity) -cne $State.InitialKind -or
        (@($before.History) -join '|') -cne (@($State.OriginalHistory) -join '|')){throw 'pending-queue-index-preflight-state-changed'}
    $State.Attempted=$true
    Write-SourceDeletionPendingQueueRecord $State.IntentPath $State
    $State.IntentSha256=(Get-FileHash -LiteralPath $State.IntentPath -Algorithm SHA256).Hash
    try {
        if($State.InitialKind -ceq 'Old'){&$RunUp}
        if((Confirm-SourceDeletionPendingQueueOutcome $State $ReadState) -cne 'New'){throw 'pending-queue-index-successor-not-established'}
        $State.Applied=$true
        Write-SourceDeletionPendingQueueRecord $State.OutcomePath @{Identity=$State.Identity;Target=$State.Target;IntentSha256=$State.IntentSha256;SettledState=$State.SettledState;Applied=$true}
    } catch {
        $failure=$_
        try{$null=Confirm-SourceDeletionPendingQueueOutcome $State $ReadState}catch{$State.SettledState='Unknown'}
        if(-not(Test-Path -LiteralPath $State.OutcomePath)){
            Write-SourceDeletionPendingQueueRecord $State.OutcomePath @{Identity=$State.Identity;Target=$State.Target;IntentSha256=$State.IntentSha256;SettledState=$State.SettledState;Applied=$false;Error=$failure.Exception.Message}
        }
        throw $failure
    }
}

Export-ModuleMember -Function Get-SourceDeletionPendingQueueContract,Get-SourceDeletionPendingQueueHash,
    Get-SourceDeletionPendingQueueIndexSha256,Get-SourceDeletionPendingQueueStateKind,Get-SourceDeletionPendingQueueDatabaseState,
    Invoke-SourceDeletionPendingQueueSql,New-SourceDeletionPendingQueueState,
    Invoke-SourceDeletionPendingQueueAttempt,Confirm-SourceDeletionPendingQueueOutcome
