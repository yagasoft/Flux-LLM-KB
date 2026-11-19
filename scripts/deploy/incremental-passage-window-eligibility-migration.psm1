Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'incremental-vector-publication-metadata-migration.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'incremental-source-deletion-pending-queue-index.psm1') -ErrorAction Stop

function Get-PassageWindowEligibilityHash([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)))
}

function Get-PassageWindowEligibilityMigrationContract {
    param([switch]$Outlook,[switch]$CodexWorkspace,[switch]$MediaTranscription,[switch]$GpuResidency)
    $contract = @{
        Baseline='20261101195325_AddVectorPublicationMetadata'
        Target='20261117210606_PersistPassageWindowEligibility'
        PriorHistoryCount=79
        PriorHistorySha256='E5B03C2F5DF66E0E9DEAEBE209E5B61A5DE32D92D27113A2EC8CB511BAAB2B06'
        JournalSchemaSha256='287B7F32C73251714682BA952F001B4CD67B096D57A211A80E1E1AE8EF89ED8F'
        JournalQuerySha256='4FAA55351825ECF82F73ED01006832C13CFC393C53BB12A0108C09C8E5006CB1'
        UpSha256='A5E04911FA8738E2161FD71A6A7DEA96D887D95F74849E90E948932E8F3868EC'
        SchemaQuerySha256='59AD02B02ED8CBCF272AD46620D272217CE29461B4020BF4051CE56D45839127'
        NewSchemaSha256='C7FE8FCC0594C99297D725414AFE9A8C98BF3AC2D0A5A61C9E72B8C964DBF782'
        OldSchemaSha256=(Get-PassageWindowEligibilityHash '{}')
        MetadataIndexSha256=(Get-VectorPublicationMetadataMigrationContract).IndexSha256
        QueueIndexSha256=(Get-SourceDeletionPendingQueueContract).IndexSha256
        TimeoutSeconds=120
        ArtifactPrefix='passage-window-eligibility'
        ManifestField='passage_window_eligibility'
        Description='additive migration 79 to 80; eligibility proofs and withdrawal cursor; no corpus backfill'
    }
    if ($Outlook -or $CodexWorkspace -or $MediaTranscription -or $GpuResidency) {
        $contract.Baseline = $contract.Target
        $contract.Target = '20261118224032_BindOutlookWatchedSources'
        $contract.PriorHistoryCount = 80
        $contract.PriorHistorySha256 = '6DF768C9BC61859A8BAF6E611F0D4D3D7416BEAA76BE8D04E47E5B96D8712740'
        $contract.UpSha256 = '3B090C12B0DD55CD7B0CFAAF8623435322AC01AD038D21337A49E99CBEDA1CBD'
        $contract.OutlookSchemaQuerySha256 = 'F03782E62C901FC329798E6181D0FE4AFB3CD27101C73AFB8A3EAB92A4BCFBC6'
        $contract.OutlookOldSchemaSha256 = '267216937E3332CEC817C34F63A04FEF96F88FFE35A3317C923269379BF8ABA5'
        $contract.OutlookNewSchemaSha256 = '43EDA5A878152DA099B10A4DC44F5514EFBA7EF8038561CBBC976819BF89E92B'
        $contract.ArtifactPrefix = 'outlook-watched-source'
        $contract.ManifestField = 'outlook_watched_source'
        $contract.Description = 'additive migration 80 to 81; watched Outlook binding and bundle receipts; no export or backfill'
    }
    if ($CodexWorkspace -or $MediaTranscription -or $GpuResidency) {
        $contract.Baseline = $contract.Target
        $contract.Target = '20261118225053_BindCodexNotesToWorkspace'
        $contract.PriorHistoryCount = 81
        $contract.PriorHistorySha256 = 'CEC9A69100F98D49D33012D6AFE274ED5E537AAA87206ADB418709B89506FE37'
        $contract.UpSha256 = 'E04F8B2FD6478D9A05D08C3C0966AD5C9BADCC70D1D859582C36032E27C8FCCC'
        $contract.CodexWorkspaceSchemaQuerySha256 = '6CBE6F86671F98EF01ACBD8B002222D56639E3F6CB3356D2FA86A30D844BC2BD'
        $contract.ArtifactPrefix = 'codex-workspace-notes'
        $contract.ManifestField = 'codex_workspace_notes'
        $contract.Description = 'additive migration 81 to 82; nullable workspace binding and filtered index; no backfill'
    }
    if ($MediaTranscription -or $GpuResidency) {
        $contract.Baseline = $contract.Target
        $contract.Target = '20261119125051_AddMediaTranscriptionGpuRequests'
        $contract.PriorHistoryCount = 82
        $contract.PriorHistorySha256 = 'CB7ABAE921C58E917F380094FA6829247248268DC18DE477B2748250194F9A35'
        $contract.UpSha256 = 'D37933C7B951F81A6A39E92F4D0814725AB1AA0DE166F5BE795BE7141FE0B46B'
        $contract.DownSha256 = 'AF94F983A26AF71A1DE61AACE435A8CB6DE1EEDE55F2925453042B3292AB4374'
        $contract.MediaSchemaQuerySha256 = '43FC00EA22FD9A55D3215C14A8FEC97C9C62A9C0A0132148506D56114AE37793'
        $contract.MediaSchemaSha256 = 'DBAEED568E77C9B38D318BF86660A82552C749486E9CC71F0E4E89675EE299D3'
        $contract.ArtifactPrefix = 'media-transcription-gpu'
        $contract.ManifestField = 'media_transcription_gpu'
        $contract.Description = 'additive migration 82 to 83; shared GPU transcription request lifecycle'
    }
    if ($GpuResidency) {
        $contract.Baseline = $contract.Target
        $contract.Target = '20261119201434_AddGpuModelResidencies'
        $contract.PriorHistoryCount = 83
        $contract.PriorHistorySha256 = 'E45C0B944EC8258C841FF8EE13749108521E15CA5DF1CE74D3773971399C73B6'
        $contract.UpSha256 = '7A9540981ED00AA671284E011673B194E637043066906B2288E51356B52AF986'
        $contract.DownSha256 = '4D24833560240B10E19D4DB50EFCF0297979F12C9997BFC35BBF03B40790CC53'
        $contract.ResidencySchemaQuerySha256 = '4E7E57F9DA3F00ABB99E6BB79824F0F564C03435BD907C4E996F45063F75131B'
        $contract.ResidencyOldSchemaSha256 = '6AD4EB72D26EBFEA639EFD9A35F3722F16F580236DFB15B82930B74A1AB55C72'
        $contract.ResidencySchemaSha256 = '56AE30DE6835EADA60124D90405A439D2A8D84F110AC7900BAD421C5F414F3A4'
        $contract.ArtifactPrefix = 'gpu-model-residency'
        $contract.ManifestField = 'gpu_model_residency'
        $contract.Description = 'additive migration 83 to 84; native model ownership and dispatch detachment receipts'
    }
    [pscustomobject]$contract
}

function Get-OutlookWatchedSourceSchemaSha256 {
    param([Parameter(Mandatory)]$Connection, [Parameter(Mandatory)]$Transaction)
    $command = $Connection.CreateCommand()
    try {
        $command.Transaction = $Transaction
        $command.CommandTimeout = 10
        $command.CommandText = Read-PassageWindowEligibilitySqlText (Join-Path $PSScriptRoot 'outlook-watched-source-schema.sql') `
            (Get-PassageWindowEligibilityMigrationContract -Outlook).OutlookSchemaQuerySha256 'outlook-watched-source-schema-query-changed'
        $json = [Text.StringBuilder]::new()
        $reader = $command.ExecuteReader()
        try {
            while ($reader.Read()) {
                $null = $json.Append($reader.GetString(0))
                if ($json.Length -gt 65536) { throw 'outlook-watched-source-schema-observation-too-large' }
            }
        } finally { $reader.Dispose() }
        if (!$json.Length) { throw 'outlook-watched-source-schema-observation-missing' }
        Get-PassageWindowEligibilityHash $json.ToString()
    } finally { $command.Dispose() }
}

function Get-MediaTranscriptionSchemaFingerprint {
    param([Parameter(Mandatory)]$Connection,[Parameter(Mandatory)]$Transaction)
    $command = $Connection.CreateCommand()
    try {
        $command.Transaction = $Transaction
        $command.CommandTimeout = 10
        $command.CommandText = Read-PassageWindowEligibilitySqlText (Join-Path $PSScriptRoot 'media-transcription-gpu-schema.sql') `
            (Get-PassageWindowEligibilityMigrationContract -MediaTranscription).MediaSchemaQuerySha256 'media-transcription-gpu-schema-query-changed'
        [string]$command.ExecuteScalar()
    } finally { $command.Dispose() }
}

function Get-GpuModelResidencySchemaFingerprint {
    param([Parameter(Mandatory)]$Connection,[Parameter(Mandatory)]$Transaction)
    $command=$Connection.CreateCommand()
    try {
        $command.Transaction=$Transaction
        $command.CommandTimeout=10
        $command.CommandText=Read-PassageWindowEligibilitySqlText (Join-Path $PSScriptRoot 'gpu-model-residency-schema.sql') `
            (Get-PassageWindowEligibilityMigrationContract -GpuResidency).ResidencySchemaQuerySha256 'gpu-residency-schema-query-changed'
        [string]$command.ExecuteScalar()
    } finally { $command.Dispose() }
}

function Get-CodexWorkspaceNotesSchemaKind {
    param([Parameter(Mandatory)]$Connection,[Parameter(Mandatory)]$Transaction)
    $command = $Connection.CreateCommand()
    try {
        $command.Transaction = $Transaction
        $command.CommandTimeout = 10
        $command.CommandText = Read-PassageWindowEligibilitySqlText (Join-Path $PSScriptRoot 'codex-workspace-notes-schema.sql') `
            (Get-PassageWindowEligibilityMigrationContract -CodexWorkspace).CodexWorkspaceSchemaQuerySha256 'codex-workspace-notes-schema-query-changed'
        [string]$command.ExecuteScalar()
    } finally { $command.Dispose() }
}

function Get-PassageWindowEligibilitySchemaSha256 {
    param([Parameter(Mandatory)]$Connection,$Transaction=$null)
    $command=$Connection.CreateCommand()
    try {
        $command.Transaction=$Transaction;$command.CommandTimeout=10
        $command.CommandText=Read-PassageWindowEligibilitySqlText (Join-Path $PSScriptRoot 'passage-window-eligibility-schema.sql') `
            (Get-PassageWindowEligibilityMigrationContract).SchemaQuerySha256 'window-eligibility-schema-query-changed'
        $parts=[Text.StringBuilder]::new();$reader=$command.ExecuteReader()
        try{while($reader.Read()){$null=$parts.Append($reader.GetString(0));if($parts.Length -gt 1048576){throw 'window-eligibility-schema-observation-too-large'}}}finally{$reader.Dispose()}
        if(!$parts.Length){throw 'window-eligibility-schema-observation-missing'}
        Get-PassageWindowEligibilityHash $parts.ToString()
    } finally {$command.Dispose()}
}

function Get-PassageWindowEligibilityStateKind {
    param([Parameter(Mandatory)]$DatabaseState,[Parameter(Mandatory)][string]$ExpectedDatabaseIdentity,[switch]$Outlook,[switch]$CodexWorkspace,[switch]$MediaTranscription,[switch]$GpuResidency)
    $contract=Get-PassageWindowEligibilityMigrationContract -Outlook:$Outlook -CodexWorkspace:$CodexWorkspace -MediaTranscription:$MediaTranscription -GpuResidency:$GpuResidency;$history=@($DatabaseState.History)
    if(-not $ExpectedDatabaseIdentity -or $DatabaseState.Identity -cne $ExpectedDatabaseIdentity -or
        $DatabaseState.ResolvesDbo -cne $true -or
        $DatabaseState.SchemaSha256 -cne $contract.JournalSchemaSha256 -or
        $DatabaseState.MetadataIndexSha256 -cne $contract.MetadataIndexSha256 -or $DatabaseState.QueueIndexSha256 -cne $contract.QueueIndexSha256){throw 'window-eligibility-database-or-journal-schema-changed'}
    if($history.Count -notin @($contract.PriorHistoryCount,($contract.PriorHistoryCount+1))){throw 'window-eligibility-history-not-exact'}
    $prefix=@($history[0..($contract.PriorHistoryCount-1)])
    if($prefix[-1] -cne $contract.Baseline -or (Get-PassageWindowEligibilityHash ($prefix -join "`n")) -cne $contract.PriorHistorySha256){throw 'window-eligibility-predecessor-history-changed'}
    if ($GpuResidency) {
        if (($history.Count -eq 83 -and $DatabaseState.MediaSchemaFingerprint -cne $contract.MediaSchemaSha256) -or
            $DatabaseState.FeatureSchemaSha256 -cne $contract.NewSchemaSha256 -or $DatabaseState.OutlookSchemaSha256 -cne $contract.OutlookNewSchemaSha256 -or
            $DatabaseState.CodexWorkspaceSchemaKind -cne 'New') { throw 'gpu-residency-predecessor-schema-changed' }
        if ($history.Count -eq 83 -and $DatabaseState.ResidencySchemaFingerprint -ceq $contract.ResidencyOldSchemaSha256) { return 'Old' }
        if ($history.Count -eq 84 -and $history[-1] -ceq $contract.Target -and
            $DatabaseState.ResidencySchemaFingerprint -ceq $contract.ResidencySchemaSha256) { return 'New' }
        throw "gpu-residency-partial-or-unreviewed-state-retain-hold (observed fingerprint $($DatabaseState.ResidencySchemaFingerprint))"
    }
    if ($MediaTranscription) {
        if ($DatabaseState.FeatureSchemaSha256 -cne $contract.NewSchemaSha256 -or
            $DatabaseState.OutlookSchemaSha256 -cne $contract.OutlookNewSchemaSha256 -or
            $DatabaseState.CodexWorkspaceSchemaKind -cne 'New') { throw 'media-transcription-predecessor-schema-changed' }
        if ($history.Count -eq 82 -and $DatabaseState.MediaSchemaFingerprint -ceq 'Old') { return 'Old' }
        if ($history.Count -eq 83 -and $history[-1] -ceq $contract.Target -and
            $DatabaseState.MediaSchemaFingerprint -ceq $contract.MediaSchemaSha256 -and
            $DatabaseState.ResidencySchemaFingerprint -ceq (Get-PassageWindowEligibilityMigrationContract -GpuResidency).ResidencyOldSchemaSha256) { return 'New' }
        throw 'media-transcription-partial-or-unreviewed-state-retain-hold'
    }
    if ($CodexWorkspace) {
        if ($DatabaseState.FeatureSchemaSha256 -cne $contract.NewSchemaSha256 -or
            $DatabaseState.OutlookSchemaSha256 -cne $contract.OutlookNewSchemaSha256) {
            throw 'codex-workspace-notes-predecessor-schema-changed'
        }
        if ($history.Count -eq $contract.PriorHistoryCount -and $DatabaseState.CodexWorkspaceSchemaKind -ceq 'Old') { return 'Old' }
        if ($history.Count -eq ($contract.PriorHistoryCount + 1) -and $history[-1] -ceq $contract.Target -and
            $DatabaseState.CodexWorkspaceSchemaKind -ceq 'New') { return 'New' }
        throw 'codex-workspace-notes-partial-or-unreviewed-state-retain-hold'
    }
    if ($Outlook) {
        if ($DatabaseState.FeatureSchemaSha256 -cne $contract.NewSchemaSha256) { throw 'outlook-watched-source-window-eligibility-schema-changed' }
        if ($history.Count -eq 80 -and $DatabaseState.OutlookSchemaSha256 -ceq $contract.OutlookOldSchemaSha256) { return 'Old' }
        if ($history.Count -eq 81 -and $history[-1] -ceq $contract.Target -and
            $DatabaseState.OutlookSchemaSha256 -ceq $contract.OutlookNewSchemaSha256) { return 'New' }
        throw 'outlook-watched-source-partial-or-unreviewed-state-retain-hold'
    }
    if($history.Count -eq $contract.PriorHistoryCount -and $DatabaseState.FeatureSchemaSha256 -ceq $contract.OldSchemaSha256){return 'Old'}
    if($history.Count -eq ($contract.PriorHistoryCount+1) -and $history[-1] -ceq $contract.Target -and $DatabaseState.FeatureSchemaSha256 -ceq $contract.NewSchemaSha256){return 'New'}
    throw 'window-eligibility-partial-or-unreviewed-state-retain-hold'
}

function Read-PassageWindowEligibilitySqlText([string]$Path,[string]$ExpectedSha256,[string]$Failure) {
    if(-not [IO.Path]::IsPathFullyQualified($Path)){throw 'window-eligibility-absolute-sql-path-required'}
    for($cursor=$Path;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint){throw 'window-eligibility-sql-path-linked'}
    }
    $file=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if($file.Length -lt 1 -or $file.Length -gt 1048576){throw 'window-eligibility-sql-size-invalid'}
        $bytes=[byte[]]::new([int]$file.Length);$file.ReadExactly($bytes)
        if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -cne $ExpectedSha256){throw $Failure}
        [Text.UTF8Encoding]::new($false,$true).GetString($bytes).TrimStart([char]0xfeff)
    } finally {$file.Dispose()}
}

function Read-PassageWindowEligibilityConnectionState {
    param([Parameter(Mandatory)]$Connection,[Parameter(Mandatory)]$Transaction,[switch]$Outlook,[switch]$CodexWorkspace,[switch]$MediaTranscription,[switch]$GpuResidency)
    $command=$Connection.CreateCommand()
    try {
        $command.Transaction=$Transaction;$command.CommandTimeout=10
        $command.CommandText="SELECT CONCAT(CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),N'|',DB_NAME(),N'|',CONVERT(nvarchar(36),database_guid)) FROM sys.database_recovery_status WHERE database_id=DB_ID();"
        $identity=[string]$command.ExecuteScalar()
        $command.CommandText="SELECT CASE WHEN OBJECT_ID(N'Vectors')=OBJECT_ID(N'dbo.Vectors') AND OBJECT_ID(N'__EFMigrationsHistory')=OBJECT_ID(N'dbo.__EFMigrationsHistory') AND OBJECT_ID(N'dbo.Vectors') IS NOT NULL AND OBJECT_ID(N'dbo.__EFMigrationsHistory') IS NOT NULL AND OBJECT_ID(N'Artifacts')=OBJECT_ID(N'dbo.Artifacts') AND OBJECT_ID(N'TextChunks')=OBJECT_ID(N'dbo.TextChunks') AND OBJECT_ID(N'SourceDeletionOperations')=OBJECT_ID(N'dbo.SourceDeletionOperations') AND (OBJECT_ID(N'TextChunkWindowEligibilities') IS NULL OR OBJECT_ID(N'TextChunkWindowEligibilities')=OBJECT_ID(N'dbo.TextChunkWindowEligibilities')) AND (OBJECT_ID(N'TextChunkWindowBackfillStates') IS NULL OR OBJECT_ID(N'TextChunkWindowBackfillStates')=OBJECT_ID(N'dbo.TextChunkWindowBackfillStates')) THEN 1 ELSE 0 END;"
        $resolvesDbo=$command.ExecuteScalar() -eq 1
        if (($Outlook -or $CodexWorkspace -or $MediaTranscription -or $GpuResidency) -and $resolvesDbo) {
            $command.CommandText = "SELECT CASE WHEN OBJECT_ID(N'OutlookCaptureProfiles')=OBJECT_ID(N'dbo.OutlookCaptureProfiles') AND OBJECT_ID(N'OutlookCaptureExports')=OBJECT_ID(N'dbo.OutlookCaptureExports') AND OBJECT_ID(N'SourceRootConfigurations')=OBJECT_ID(N'dbo.SourceRootConfigurations') THEN 1 ELSE 0 END;"
            $resolvesDbo = $command.ExecuteScalar() -eq 1
        }
        if (($CodexWorkspace -or $MediaTranscription -or $GpuResidency) -and $resolvesDbo) {
            $command.CommandText = "SELECT CASE WHEN OBJECT_ID(N'KnowledgeItems',N'U')=OBJECT_ID(N'dbo.KnowledgeItems',N'U') THEN 1 ELSE 0 END;"
            $resolvesDbo = $command.ExecuteScalar() -eq 1
        }
        if (($MediaTranscription -or $GpuResidency) -and $resolvesDbo) {
            $command.CommandText = "SELECT CASE WHEN SCHEMA_NAME()=N'dbo' AND (OBJECT_ID(N'MediaTranscriptionGpuRequests') IS NULL OR OBJECT_ID(N'MediaTranscriptionGpuRequests')=OBJECT_ID(N'dbo.MediaTranscriptionGpuRequests')) THEN 1 ELSE 0 END;"
            $resolvesDbo = $command.ExecuteScalar() -eq 1
        }
        $command.CommandText="SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER');"
        $canAlter=$command.ExecuteScalar() -eq 1
        $command.CommandText='SELECT MigrationId FROM dbo.__EFMigrationsHistory WITH (HOLDLOCK) ORDER BY MigrationId;'
        $history=[Collections.Generic.List[string]]::new();$reader=$command.ExecuteReader()
        try{while($reader.Read()){$history.Add($reader.GetString(0))}}finally{$reader.Dispose()}
        $query=Join-Path $PSScriptRoot 'source-journal-schema-contract.sql'
        $command.CommandText=Read-PassageWindowEligibilitySqlText $query (Get-PassageWindowEligibilityMigrationContract).JournalQuerySha256 'window-eligibility-journal-query-changed'
        $schema=[string]$command.ExecuteScalar()
        if(-not $schema){throw 'window-eligibility-journal-schema-observation-missing'}
        [pscustomobject]@{Identity=$identity;History=$history.ToArray();CanAlter=$canAlter;ResolvesDbo=$resolvesDbo;
            SchemaSha256=(Get-PassageWindowEligibilityHash $schema);
            ResidencySchemaFingerprint=$(if ($GpuResidency -or ($MediaTranscription -and $history.Count -eq 83)) { Get-GpuModelResidencySchemaFingerprint $Connection $Transaction });
            MediaSchemaFingerprint=$(if ($MediaTranscription -or $GpuResidency) { Get-MediaTranscriptionSchemaFingerprint $Connection $Transaction });
            CodexWorkspaceSchemaKind=$(if ($CodexWorkspace -or $MediaTranscription -or $GpuResidency) { Get-CodexWorkspaceNotesSchemaKind $Connection $Transaction });
            OutlookSchemaSha256=$(if ($Outlook -or $CodexWorkspace -or $MediaTranscription -or $GpuResidency) { Get-OutlookWatchedSourceSchemaSha256 $Connection $Transaction });
            FeatureSchemaSha256=(Get-PassageWindowEligibilitySchemaSha256 -Connection $Connection -Transaction $Transaction);
            MetadataIndexSha256=(Get-VectorPublicationMetadataIndexSha256 -Connection $Connection -Transaction $Transaction);
            QueueIndexSha256=(Get-SourceDeletionPendingQueueIndexSha256 -Connection $Connection -Transaction $Transaction)}
    } finally {$command.Dispose()}
}

function New-PassageWindowEligibilityConnection([string]$ConnectionString) {
    $normalised=$ConnectionString -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' `
        -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
    $binding=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
    if(-not $binding.DataSource -or -not $binding.InitialCatalog -or $binding.InitialCatalog -iin @('master','model','msdb','tempdb')){throw 'window-eligibility-database-target-invalid'}
    $binding['Pooling']=$false;$binding['Enlist']=$false;$binding['ConnectRetryCount']=0;$binding['Connect Timeout']=5
    $binding['Application Name']='FluxKnowledge.PassageWindowEligibilityMigration'
    [Data.SqlClient.SqlConnection]::new($binding.ConnectionString)
}

function Get-PassageWindowEligibilityDatabaseState {
    param([Parameter(Mandatory)][string]$ConnectionString,[switch]$Outlook,[switch]$CodexWorkspace,[switch]$MediaTranscription,[switch]$GpuResidency)
    $connection=New-PassageWindowEligibilityConnection $ConnectionString
    try {
        $connection.Open();$transaction=$connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        try {$state=Read-PassageWindowEligibilityConnectionState $connection $transaction -Outlook:$Outlook -CodexWorkspace:$CodexWorkspace -MediaTranscription:$MediaTranscription -GpuResidency:$GpuResidency;$transaction.Commit();$state}
        finally {$transaction.Dispose()}
    } finally {$connection.Dispose()}
}

function Invoke-PassageWindowEligibilityMigrationSql {
    param([Parameter(Mandatory)][string]$ConnectionString,[Parameter(Mandatory)][string]$SqlPath,
        [Parameter(Mandatory)][string]$ExpectedDatabaseIdentity,[switch]$Outlook,[switch]$CodexWorkspace,[switch]$MediaTranscription,[switch]$GpuResidency)
    $contract=Get-PassageWindowEligibilityMigrationContract -Outlook:$Outlook -CodexWorkspace:$CodexWorkspace -MediaTranscription:$MediaTranscription -GpuResidency:$GpuResidency
    $text=Read-PassageWindowEligibilitySqlText $SqlPath $contract.UpSha256 'window-eligibility-up-sql-changed'
    $connection=New-PassageWindowEligibilityConnection $ConnectionString
    try {
        $connection.Open()
        $transaction=$connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        try {
            $before=Read-PassageWindowEligibilityConnectionState $connection $transaction -Outlook:$Outlook -CodexWorkspace:$CodexWorkspace -MediaTranscription:$MediaTranscription -GpuResidency:$GpuResidency
            $kind=Get-PassageWindowEligibilityStateKind $before $ExpectedDatabaseIdentity -Outlook:$Outlook -CodexWorkspace:$CodexWorkspace -MediaTranscription:$MediaTranscription -GpuResidency:$GpuResidency
            if(-not $before.ResolvesDbo){throw 'window-eligibility-unqualified-objects-do-not-resolve-to-dbo'}
            if($kind -ceq 'New'){$transaction.Commit();return}
            if(-not $before.CanAlter){throw 'window-eligibility-ddl-connection-alter-permission-required'}
            $timer=[Diagnostics.Stopwatch]::StartNew()
            foreach($batch in ([regex]::Split($text,'(?im)^\s*GO\s*(?:--.*)?$')|Where-Object{-not [string]::IsNullOrWhiteSpace($_)})){
                $remaining=$contract.TimeoutSeconds-[int][Math]::Ceiling($timer.Elapsed.TotalSeconds)
                if($remaining -le 0){throw 'window-eligibility-migration-time-budget-exhausted'}
                $command=$connection.CreateCommand()
                try{$command.Transaction=$transaction;$command.CommandTimeout=$remaining;$command.CommandText=$batch;$null=$command.ExecuteNonQuery()}
                finally{$command.Dispose()}
            }
            if((Get-PassageWindowEligibilityStateKind (Read-PassageWindowEligibilityConnectionState $connection $transaction -Outlook:$Outlook -CodexWorkspace:$CodexWorkspace -MediaTranscription:$MediaTranscription -GpuResidency:$GpuResidency) $ExpectedDatabaseIdentity -Outlook:$Outlook -CodexWorkspace:$CodexWorkspace -MediaTranscription:$MediaTranscription -GpuResidency:$GpuResidency) -cne 'New'){throw 'window-eligibility-transaction-successor-invalid'}
            $transaction.Commit()
        } catch {try{$transaction.Rollback()}catch{};throw}
        finally {$transaction.Dispose()}
    } finally {$connection.Dispose()}
}

function Write-PassageWindowEligibilityRecord([string]$Path,$Value) {
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 12 -Compress))
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read,4096,[IO.FileOptions]::WriteThrough)
    try{$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
}

function New-PassageWindowEligibilityMigrationState {
    param([Parameter(Mandatory)]$DatabaseState,[Parameter(Mandatory)][string]$ReceiptDirectory,
        [Parameter(Mandatory)][string]$ReleaseId,[Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$SqlSha256,[switch]$Outlook,[switch]$CodexWorkspace,[switch]$MediaTranscription,[switch]$GpuResidency)
    $contract=Get-PassageWindowEligibilityMigrationContract -Outlook:$Outlook -CodexWorkspace:$CodexWorkspace -MediaTranscription:$MediaTranscription -GpuResidency:$GpuResidency
    $kind=Get-PassageWindowEligibilityStateKind $DatabaseState $DatabaseState.Identity -Outlook:$Outlook -CodexWorkspace:$CodexWorkspace -MediaTranscription:$MediaTranscription -GpuResidency:$GpuResidency
    if($SqlSha256 -cne $contract.UpSha256 -or $Commit -cnotmatch '^[a-f0-9]{40}$' -or $ReleaseId -cnotmatch '^[A-Za-z0-9-]{1,100}$'){throw 'window-eligibility-release-sql-binding-invalid'}
    if($kind -ceq 'Old' -and $DatabaseState.CanAlter -cne $true){throw 'window-eligibility-alter-permission-required'}
    if(-not [IO.Path]::IsPathFullyQualified($ReceiptDirectory) -or [IO.Path]::GetFullPath($ReceiptDirectory) -cne $ReceiptDirectory){throw 'window-eligibility-absolute-receipt-path-required'}
    for($path=$ReceiptDirectory;$path;$path=[IO.Path]::GetDirectoryName($path)){
        if(-not(Test-Path -LiteralPath $path) -or ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint)){throw 'window-eligibility-receipt-path-unsafe'}
    }
    $purpose=$contract.ArtifactPrefix+'-migration'
    $intent=Join-Path $ReceiptDirectory ($purpose+'-intent.json')
    if(Test-Path -LiteralPath $intent){throw 'window-eligibility-existing-intent-requires-reconciliation'}
    @{
        Version=1;Purpose=$purpose;ReleaseId=$ReleaseId;Commit=$Commit;Identity=$DatabaseState.Identity
        Target=$contract.Target;SqlSha256=$SqlSha256;InitialKind=$kind;OriginalHistory=@($DatabaseState.History)
        IntentPath=$intent;OutcomePath=(Join-Path $ReceiptDirectory ($purpose+'-outcome.json'))
        Attempted=$false;Applied=$false;SettledState='Unattempted'
    }
}

function Confirm-PassageWindowEligibilityMigrationOutcome {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][scriptblock]$ReadState)
    $State.SettledState='Unknown'
    $State.SettledState=Get-PassageWindowEligibilityStateKind (&$ReadState) $State.Identity -Outlook:($State.Purpose -ceq 'outlook-watched-source-migration') -CodexWorkspace:($State.Purpose -ceq 'codex-workspace-notes-migration') -MediaTranscription:($State.Purpose -ceq 'media-transcription-gpu-migration') -GpuResidency:($State.Purpose -ceq 'gpu-model-residency-migration')
    $State.SettledState
}

function Invoke-PassageWindowEligibilityMigrationAttempt {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][scriptblock]$ReadState,
        [Parameter(Mandatory)][scriptblock]$AssertStopped,[Parameter(Mandatory)][scriptblock]$RunUp)
    if($State.Attempted){throw 'window-eligibility-attempt-requires-reconciliation-not-replay'}
    &$AssertStopped
    $before=&$ReadState
    if((Get-PassageWindowEligibilityStateKind $before $State.Identity -Outlook:($State.Purpose -ceq 'outlook-watched-source-migration') -CodexWorkspace:($State.Purpose -ceq 'codex-workspace-notes-migration') -MediaTranscription:($State.Purpose -ceq 'media-transcription-gpu-migration') -GpuResidency:($State.Purpose -ceq 'gpu-model-residency-migration')) -cne $State.InitialKind -or
        (@($before.History) -join '|') -cne (@($State.OriginalHistory) -join '|')){throw 'window-eligibility-preflight-state-changed'}
    $State.Attempted=$true
    Write-PassageWindowEligibilityRecord $State.IntentPath $State
    $State.IntentSha256=(Get-FileHash -LiteralPath $State.IntentPath -Algorithm SHA256).Hash
    try {
        if($State.InitialKind -ceq 'Old'){&$RunUp}
        if((Confirm-PassageWindowEligibilityMigrationOutcome $State $ReadState) -cne 'New'){throw 'window-eligibility-successor-not-established'}
        $State.Applied=$true
        Write-PassageWindowEligibilityRecord $State.OutcomePath @{Identity=$State.Identity;Target=$State.Target;IntentSha256=$State.IntentSha256;SettledState=$State.SettledState;Applied=$true}
    } catch {
        $failure=$_
        try{$null=Confirm-PassageWindowEligibilityMigrationOutcome $State $ReadState}catch{$State.SettledState='Unknown'}
        if(-not(Test-Path -LiteralPath $State.OutcomePath)){
            Write-PassageWindowEligibilityRecord $State.OutcomePath @{Identity=$State.Identity;Target=$State.Target;IntentSha256=$State.IntentSha256;SettledState=$State.SettledState;Applied=$false;Error=$failure.Exception.Message}
        }
        throw $failure
    }
}

Export-ModuleMember -Function Get-PassageWindowEligibilityMigrationContract,Get-PassageWindowEligibilityHash,
    Get-PassageWindowEligibilitySchemaSha256,Get-PassageWindowEligibilityStateKind,Get-PassageWindowEligibilityDatabaseState,
    Invoke-PassageWindowEligibilityMigrationSql,New-PassageWindowEligibilityMigrationState,
    Invoke-PassageWindowEligibilityMigrationAttempt,Confirm-PassageWindowEligibilityMigrationOutcome,Write-PassageWindowEligibilityRecord,Get-GpuModelResidencySchemaFingerprint,Get-MediaTranscriptionSchemaFingerprint,Get-OutlookWatchedSourceSchemaSha256,Get-CodexWorkspaceNotesSchemaKind
