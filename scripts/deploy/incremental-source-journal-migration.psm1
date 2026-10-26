Set-StrictMode -Version Latest

function Get-SourceJournalMigrationContract {
    [pscustomobject]@{
        Baseline='20261024061418_AddDocumentOcrExecutionLifecycle'
        BaselineHistoryCount=66
        BaselineHistorySha256='070598634802F671503EB416D44D39EF35499B69F8310B775F1FE320AFD36E7D'
        Migrations=@('20261024095915_AddIncrementalSourceScanning','20261024101913_AddIncrementalScanManifests',
            '20261024105456_BoundIncrementalChangeDebounce','20261024110355_RecordJournalAvailability',
            '20261024122515_PersistSourceWatchPathHints','20261024132626_FenceIncrementalRecovery',
            '20261024151514_SealIncrementalSourceCaptures','20261025101429_AddSourceVerificationExecution',
            '20261025103623_DistinguishParkedSourceRetryDebt','20261025111314_AddSourceVerificationResolution',
            '20261025113436_MakeSourceVerificationResolutionFair','20261025124025_PreserveSourceVerificationReadCounts')
        UpSha256='F04BC4DD13FEAEA75B7779113DDECB356632B4E52E2919DBF665104AF73B8591'
        BaselineSchemaSha256='44136FA355B3678A1146AD16F7E8649E94FB4FC21FE77E8310C060F61CAAFF8A'
        SchemaSha256='287B7F32C73251714682BA952F001B4CD67B096D57A211A80E1E1AE8EF89ED8F'
        SchemaSqlSha256='4FAA55351825ECF82F73ED01006832C13CFC393C53BB12A0108C09C8E5006CB1'
    }
}

function Get-SourceJournalValueHash([string]$Value) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value)))
}

function Read-SourceJournalMigrationSql {
    param([string]$Path=(Join-Path $PSScriptRoot 'source-journal-up.sql'))
    for($current=[IO.Path]::GetFullPath($Path);$current;$current=[IO.Path]::GetDirectoryName($current)){
        if([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint){throw 'journal-schema-sql-path-unsafe'}
    }
    $file=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($file.Length -lt 1 -or $file.Length -gt 1048576){throw 'journal-schema-sql-size-invalid'}
        $bytes=[byte[]]::new([int]$file.Length);$file.ReadExactly($bytes)
        if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -cne (Get-SourceJournalMigrationContract).UpSha256){throw 'journal-schema-sql-bytes-changed'}
        return ,$bytes
    }finally{$file.Dispose()}
}

function Invoke-SourceJournalVerifiedSql {
    param([Parameter(Mandatory)][byte[]]$SqlBytes,[Parameter(Mandatory)][scriptblock]$RunSql)
    if($SqlBytes.Length -lt 1 -or $SqlBytes.Length -gt 1048576){throw 'journal-schema-sql-size-invalid'}
    # Hash and decode the same private copy. The callback receives immutable text;
    # it never reopens an input path after verification or retries a failed script.
    $verified=[byte[]]$SqlBytes.Clone()
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($verified)) -cne (Get-SourceJournalMigrationContract).UpSha256){throw 'journal-schema-sql-bytes-changed'}
    $sql=[Text.UTF8Encoding]::new($false,$true).GetString($verified).TrimStart([char]0xfeff)
    &$RunSql $sql
}

function Invoke-SourceJournalDeploymentSql {
    param([Parameter(Mandatory)][byte[]]$SqlBytes,[Parameter(Mandatory)][string]$ConnectionString,
        [Parameter(Mandatory)][string]$ExpectedDatabaseIdentity)
    if([string]::IsNullOrWhiteSpace($ExpectedDatabaseIdentity)){throw 'journal-schema-reviewed-database-binding-required'}
    Invoke-SourceJournalVerifiedSql -SqlBytes $SqlBytes -RunSql {
        param($verifiedSourceJournalText)
        $normalised=$ConnectionString -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' `
            -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
        $binding=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
        if([string]::IsNullOrWhiteSpace($binding.DataSource) -or [string]::IsNullOrWhiteSpace($binding.InitialCatalog) -or
            $binding.InitialCatalog -iin @('master','model','msdb','tempdb')){throw 'journal-schema-database-target-invalid'}
        $binding['Pooling']=$false;$binding['Enlist']=$false;$binding['ConnectRetryCount']=0;$binding['Connect Timeout']=5
        $connection=[Data.SqlClient.SqlConnection]::new($binding.ConnectionString)
        try{
            $connection.Open();$identity=$connection.CreateCommand()
            try{
                $identity.CommandTimeout=10
                $identity.CommandText="SELECT CONCAT(CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),N'|',DB_NAME(),N'|',CONVERT(nvarchar(36),database_guid)) FROM sys.database_recovery_status WHERE database_id=DB_ID();"
                if([string]$identity.ExecuteScalar() -cne $ExpectedDatabaseIdentity){throw 'journal-schema-sql-database-incarnation-changed'}
            }finally{$identity.Dispose()}
            foreach($batch in [regex]::Split($verifiedSourceJournalText,'(?im)^\s*GO\s*(?:--.*)?$')|Where-Object{![string]::IsNullOrWhiteSpace($_)}){
                $command=$connection.CreateCommand()
                try{$command.CommandTimeout=120;$command.CommandText=$batch;$null=$command.ExecuteNonQuery()}finally{$command.Dispose()}
            }
        }finally{$connection.Dispose()}
    }
}

function Get-SourceJournalDatabaseState {
    param([Parameter(Mandatory)][string]$ConnectionString)
    $normalised=$ConnectionString -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
    $binding=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
    if(!$binding.DataSource -or !$binding.InitialCatalog -or $binding.InitialCatalog -iin @('master','model','msdb','tempdb')) {throw 'journal-schema-database-invalid'}
    $binding['Pooling']=$false;$binding['Enlist']=$false;$binding['ConnectRetryCount']=0;$binding['Connect Timeout']=5
    $binding['Application Name']='FluxKnowledge.SourceJournalSchema'
    $connection=[Data.SqlClient.SqlConnection]::new($binding.ConnectionString)
    try {
        $connection.Open();$transaction=$connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        try {
            $command=$connection.CreateCommand();$command.Transaction=$transaction;$command.CommandTimeout=10
            try {
                $command.CommandText="SELECT CONCAT(CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),N'|',DB_NAME(),N'|',CONVERT(nvarchar(36),database_guid)) FROM sys.database_recovery_status WHERE database_id=DB_ID();"
                $identity=[string]$command.ExecuteScalar()
                if(!$identity){throw 'journal-schema-database-incarnation-unavailable'}
                $command.CommandText="SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER');"
                $canAlter=[int]$command.ExecuteScalar() -eq 1
                $command.CommandText='SELECT MigrationId FROM dbo.__EFMigrationsHistory WITH (HOLDLOCK) ORDER BY MigrationId;'
                $history=[Collections.Generic.List[string]]::new();$reader=$command.ExecuteReader()
                try {while($reader.Read()){$history.Add($reader.GetString(0))}}finally{$reader.Dispose()}
                $bytes=[IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'source-journal-schema-contract.sql'))
                if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -cne (Get-SourceJournalMigrationContract).SchemaSqlSha256){throw 'journal-schema-query-changed'}
                $command.CommandText=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)
                $schema=[string]$command.ExecuteScalar()
                if(!$schema){throw 'journal-schema-observation-missing'}
                $null=$schema|ConvertFrom-Json
                $transaction.Commit()
                [pscustomobject]@{Identity=$identity;History=$history.ToArray();CanAlter=$canAlter;SchemaSha256=(Get-SourceJournalValueHash $schema)}
            }finally{$command.Dispose()}
        }finally{$transaction.Dispose()}
    }finally{$connection.Dispose()}
}

function Assert-SourceJournalSchema {
    param([Parameter(Mandatory)]$DatabaseState,[switch]$AllowBaseline)
    $contract=Get-SourceJournalMigrationContract;$history=@($DatabaseState.History)
    if(!$DatabaseState.Identity -or $history.Count -lt $contract.BaselineHistoryCount){throw 'journal-schema-history-or-incarnation-invalid'}
    $prefix=@($history[0..($contract.BaselineHistoryCount-1)])
    if((Get-SourceJournalValueHash ($prefix -join "`n")) -cne $contract.BaselineHistorySha256){throw 'journal-schema-baseline-history-changed'}
    $base=$history.Count -eq $contract.BaselineHistoryCount -and $DatabaseState.SchemaSha256 -ceq $contract.BaselineSchemaSha256
    $complete=$history.Count -eq ($contract.BaselineHistoryCount+$contract.Migrations.Count) -and
        ($history[$contract.BaselineHistoryCount..($history.Count-1)] -join '|') -ceq ($contract.Migrations -join '|') -and
        $DatabaseState.SchemaSha256 -ceq $contract.SchemaSha256
    if(!$complete -and !($AllowBaseline -and $base)){throw 'journal-schema-partial-or-unreviewed-state-retain-hold'}
}

function New-SourceJournalMigrationState {
    param([Parameter(Mandatory)]$DatabaseState,[Parameter(Mandatory)][string]$ReceiptDirectory,
        [Parameter(Mandatory)][string]$ReleaseId,[Parameter(Mandatory)][string]$SqlSha256)
    Assert-SourceJournalSchema $DatabaseState -AllowBaseline
    $contract=Get-SourceJournalMigrationContract
    if($SqlSha256 -cne $contract.UpSha256 -or $ReleaseId -cnotmatch '^[A-Za-z0-9-]{1,100}$'){throw 'journal-schema-release-binding-invalid'}
    $already=@($DatabaseState.History).Count -gt $contract.BaselineHistoryCount
    if(!$already -and $DatabaseState.CanAlter -cne $true){throw 'journal-schema-alter-permission-required'}
    if(![IO.Path]::IsPathFullyQualified($ReceiptDirectory)){throw 'journal-schema-absolute-receipt-directory-required'}
    $root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($ReceiptDirectory))
    for($path=$root;$path;$path=[IO.Path]::GetDirectoryName($path)) {
        if([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint){throw 'journal-schema-receipt-path-unsafe'}
    }
    $intent=Join-Path $root 'source-journal-migration-intent.json'
    if(Test-Path -LiteralPath $intent){throw 'journal-schema-existing-intent-requires-reconciliation'}
    @{Version=1;ReleaseId=$ReleaseId;Identity=$DatabaseState.Identity;OriginalHistory=@($DatabaseState.History);
        OriginalSchemaSha256=$DatabaseState.SchemaSha256;ExpectedHistory=$(if($already){@($DatabaseState.History)}else{@($DatabaseState.History)+@($contract.Migrations)});
        ExpectedSchemaSha256=$contract.SchemaSha256;SqlSha256=$SqlSha256;IntentPath=$intent;
        AlreadyApplied=$already;Attempted=$false;Applied=$false}
}

function Invoke-SourceJournalMigrationAttempt {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][scriptblock]$ReadState,
        [Parameter(Mandatory)][scriptblock]$AssertStopped,[Parameter(Mandatory)][scriptblock]$RunUp)
    if($State.Attempted -or (Test-Path -LiteralPath $State.IntentPath)){throw 'journal-schema-attempt-requires-reconciliation'}
    &$AssertStopped
    $before=&$ReadState
    Assert-SourceJournalSchema $before -AllowBaseline
    if($before.Identity -cne $State.Identity -or ($before.History -join '|') -cne ($State.OriginalHistory -join '|') -or
        $before.SchemaSha256 -cne $State.OriginalSchemaSha256 -or $State.SqlSha256 -cne (Get-SourceJournalMigrationContract).UpSha256){throw 'journal-schema-preflight-binding-changed'}
    # A durable attempted boundary survives SQL failure, partial migration history,
    # and parent-process loss. It is never an authorisation to run destructive Down.
    $State.Attempted=$true
    $receipt=[ordered]@{Version=1;Purpose='source-journal-additive-migration';ReleaseId=$State.ReleaseId;
        RecordedAtUtc=[DateTimeOffset]::UtcNow;Identity=$State.Identity;SqlSha256=$State.SqlSha256;
        OriginalHistory=$State.OriginalHistory;OriginalSchemaSha256=$State.OriginalSchemaSha256;
        ExpectedHistory=$State.ExpectedHistory;ExpectedSchemaSha256=$State.ExpectedSchemaSha256;Attempted=$true}
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($receipt|ConvertTo-Json -Depth 12))
    $file=[IO.FileStream]::new($State.IntentPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$file.Write($bytes);$file.Flush($true)}finally{$file.Dispose()}
    &$AssertStopped
    if(!$State.AlreadyApplied){&$RunUp}
    &$AssertStopped
    $after=&$ReadState
    Assert-SourceJournalSchema $after
    if($after.Identity -cne $State.Identity -or ($after.History -join '|') -cne ($State.ExpectedHistory -join '|')){throw 'journal-schema-postflight-binding-changed'}
    $State.Applied=$true
}

function Assert-SourceJournalPredecessorStart {
    param([Parameter(Mandatory)]$DatabaseState,[hashtable]$MigrationState=$null,[string]$IntentPath='')
    if(($MigrationState -and ($MigrationState.Attempted -or (Test-Path -LiteralPath $MigrationState.IntentPath))) -or
        ($IntentPath -and (Test-Path -LiteralPath $IntentPath))){throw 'journal-schema-predecessor-start-forbidden-after-intent'}
    Assert-SourceJournalSchema $DatabaseState -AllowBaseline
    if(@($DatabaseState.History).Count -ne (Get-SourceJournalMigrationContract).BaselineHistoryCount){throw 'journal-schema-predecessor-start-forbidden'}
}

function Invoke-SourceJournalApplicationAdmission {
    param([Parameter(Mandatory)][scriptblock]$ReadState,[hashtable[]]$ReviewedPayloads=@(),
        [hashtable[]]$CurrentPayloads=@(),[Parameter(Mandatory)][scriptblock]$ReadPayloadFingerprint,
        [Parameter(Mandatory)][scriptblock]$Action,[string]$ExpectedDatabaseIdentity='',
        [hashtable]$MigrationState=$null,[string]$IntentPath='',[switch]$RequireFeatureSchema)
    $before=&$ReadState
    Assert-SourceJournalSchema $before -AllowBaseline
    $complete=@($before.History).Count -gt (Get-SourceJournalMigrationContract).BaselineHistoryCount
    $intent=($MigrationState -and ($MigrationState.Attempted -or (Test-Path -LiteralPath $MigrationState.IntentPath))) -or
        ($IntentPath -and (Test-Path -LiteralPath $IntentPath))
    if(!$complete){
        if($intent -or $RequireFeatureSchema){throw 'journal-schema-incomplete-after-intent-retain-hold'}
        &$Action
        return
    }
    if(!$ExpectedDatabaseIdentity -or $before.Identity -cne $ExpectedDatabaseIdentity){throw 'journal-schema-reviewed-database-binding-required'}
    if($ReviewedPayloads.Count -ne 3 -or $CurrentPayloads.Count -ne 3){throw 'journal-schema-complete-reviewed-payload-set-required'}
    $roles=@('Web','Cli','OutlookHost')
    if(@($ReviewedPayloads|ForEach-Object Role|Sort-Object -Unique).Count -ne 3 -or
        @($CurrentPayloads|ForEach-Object Role|Sort-Object -Unique).Count -ne 3){throw 'journal-schema-duplicate-payload-role'}
    $held=[Collections.Generic.List[IO.FileStream]]::new()
    try {
        foreach($role in $roles){
            $reviewed=@($ReviewedPayloads|Where-Object Role -CEQ $role)
            $actual=@($CurrentPayloads|Where-Object Role -CEQ $role)
            if($reviewed.Count -ne 1 -or $actual.Count -ne 1 -or $reviewed[0].Sha256 -cnotmatch '^[A-Fa-f0-9]{64}$' -or
                ![IO.Path]::IsPathFullyQualified($reviewed[0].Root) -or ![IO.Path]::IsPathFullyQualified($actual[0].Root)){throw 'journal-schema-payload-binding-invalid'}
            $expectedRoot=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($reviewed[0].Root))
            $root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($actual[0].Root))
            if(!$root.Equals($expectedRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'journal-schema-payload-root-changed'}
            $entry=Join-Path $root ('FluxKnowledge.'+$role+'.dll')
            for($path=$entry;$path;$path=[IO.Path]::GetDirectoryName($path)) {
                if([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint){throw 'journal-schema-payload-path-unsafe'}
            }
            $held.Add([IO.File]::Open($entry,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))
            $hash=&$ReadPayloadFingerprint $root
            if($hash -cnotmatch '^[A-Fa-f0-9]{64}$' -or $hash -ine $reviewed[0].Sha256){throw 'journal-schema-unreviewed-payload-start-forbidden'}
        }
        $current=&$ReadState
        Assert-SourceJournalSchema $current
        if($current.Identity -cne $before.Identity -or ($current.History -join '|') -cne ($before.History -join '|') -or
            $current.SchemaSha256 -cne $before.SchemaSha256){throw 'journal-schema-changed-before-start'}
        &$Action
    }finally{foreach($file in $held){$file.Dispose()}}
}

function Assert-SourceJournalComposedApplicationSchema {
    param([Parameter(Mandatory)]$JournalState,[Parameter(Mandatory)]$ProjectionState)
    Assert-SourceJournalSchema $JournalState -AllowBaseline
    if($ProjectionState.Identity -cne $JournalState.Identity -or
        ($ProjectionState.History -join '|') -cne ($JournalState.History -join '|')){throw 'journal-schema-composed-observation-changed'}
    # Only the fully validated feature suffix can be removed. The original
    # segmented-index admission remains unchanged and observes its own shape.
    $view=[pscustomobject]@{History=@($JournalState.History[0..((Get-SourceJournalMigrationContract).BaselineHistoryCount-1)]);
        SchemaSha256=$ProjectionState.SchemaSha256;AuditSingletonCount=$ProjectionState.AuditSingletonCount;
        RequiredFormat=$ProjectionState.RequiredFormat}
    Import-Module (Join-Path $PSScriptRoot 'incremental-segmented-index-schema.psm1') -ErrorAction Stop
    Assert-SegmentedIndexApplicationSchema $view
}

function Read-SourceJournalEvidenceJson {
    param([string]$Path,[int]$MaximumBytes)
    for($current=$Path;$current;$current=[IO.Path]::GetDirectoryName($current)){
        if([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint){throw 'journal-schema-evidence-path-unsafe'}
    }
    $file=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($file.Length -lt 1 -or $file.Length -gt $MaximumBytes){throw 'journal-schema-evidence-size-invalid'}
        $bytes=[byte[]]::new([int]$file.Length);$file.ReadExactly($bytes)
        $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)
        $document=[Text.Json.JsonDocument]::Parse($text)
        try{
            if($document.RootElement.ValueKind -eq [Text.Json.JsonValueKind]::Object){
                $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                foreach($property in $document.RootElement.EnumerateObject()){
                    if(!$names.Add($property.Name)){throw 'journal-schema-evidence-duplicate-field'}
                }
            }
        }finally{$document.Dispose()}
        ConvertFrom-Json -InputObject $text -AsHashtable -DateKind String
    }finally{$file.Dispose()}
}

function Get-SourceJournalHeldMigrationIntent {
    param([Parameter(Mandatory)][string]$HoldPath,[Parameter(Mandatory)][string]$RecoveryRoot,
        [Parameter(Mandatory)][string]$ExpectedDatabaseIdentity)
    if(![IO.Path]::IsPathFullyQualified($HoldPath) -or ![IO.Path]::IsPathFullyQualified($RecoveryRoot)){
        throw 'journal-schema-absolute-recovery-path-required'
    }
    if(!(Test-Path -LiteralPath $HoldPath)){return ''}
    $releaseId=Get-SourceJournalHeldReleaseId $HoldPath
    $root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($RecoveryRoot))
    $release=[IO.Path]::GetFullPath((Join-Path $root $releaseId))
    if(![IO.Path]::GetDirectoryName($release).Equals($root,[StringComparison]::OrdinalIgnoreCase)){
        throw 'journal-schema-recovery-path-escape'
    }
    for($current=$release;$current;$current=[IO.Path]::GetDirectoryName($current)){
        if([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint){throw 'journal-schema-evidence-path-unsafe'}
    }
    $path=Join-Path $release 'source-journal-migration-intent.json'
    if(!(Test-Path -LiteralPath $path)){return ''}
    $intent=Read-SourceJournalEvidenceJson $path 65536
    $contract=Get-SourceJournalMigrationContract
    if($intent -isnot [Collections.IDictionary] -or $intent.Version -ne 1 -or
        $intent.Purpose -cne 'source-journal-additive-migration' -or $intent.ReleaseId -cne $releaseId -or
        $intent.Identity -cne $ExpectedDatabaseIdentity -or $intent.Attempted -isnot [bool] -or !$intent.Attempted -or
        $intent.SqlSha256 -cne $contract.UpSha256 -or $intent.ExpectedSchemaSha256 -cne $contract.SchemaSha256){
        throw 'journal-schema-held-intent-binding-changed'
    }
    Assert-SourceJournalSchema ([pscustomobject]@{Identity=$intent.Identity;History=@($intent.OriginalHistory);
        SchemaSha256=$intent.OriginalSchemaSha256}) -AllowBaseline
    $prefix=@($intent.OriginalHistory[0..($contract.BaselineHistoryCount-1)])
    if(($intent.ExpectedHistory -join '|') -cne (($prefix+@($contract.Migrations)) -join '|')){
        throw 'journal-schema-held-intent-history-changed'
    }
    $path
}

function Get-SourceJournalHeldReleaseId {
    param([Parameter(Mandatory)][string]$HoldPath)
    $value=Read-SourceJournalEvidenceJson ([IO.Path]::GetFullPath($HoldPath)) 4096
    if($value -is [Collections.IDictionary]){
        $operation=[Guid]::Empty
        if($value.Count -ne 3 -or @($value.Keys|Where-Object {$_ -cnotin @('version','releaseId','corpusRebuildOperationId')}).Count -or
            $value.version -isnot [long] -or $value.version -ne 1 -or $value.corpusRebuildOperationId -isnot [string] -or
            ![Guid]::TryParseExact($value.corpusRebuildOperationId,'D',[ref]$operation) -or $operation -eq [Guid]::Empty){
            throw 'journal-schema-held-release-invalid'
        }
        $value=$value.releaseId
    }
    if($value -isnot [string] -or $value -cnotmatch '^\d{8}T\d{6}Z-[a-f0-9]{12}(?:-(?:repositoryrecovery|hybrid|hybridpatch))?$'){
        throw 'journal-schema-held-release-invalid'
    }
    $value
}

Export-ModuleMember -Function Get-SourceJournalMigrationContract,Get-SourceJournalDatabaseState,Assert-SourceJournalSchema,
    New-SourceJournalMigrationState,Invoke-SourceJournalMigrationAttempt,Assert-SourceJournalPredecessorStart,Invoke-SourceJournalApplicationAdmission,
    Assert-SourceJournalComposedApplicationSchema,Get-SourceJournalHeldMigrationIntent,Get-SourceJournalHeldReleaseId,
    Read-SourceJournalMigrationSql,Invoke-SourceJournalVerifiedSql,Invoke-SourceJournalDeploymentSql
