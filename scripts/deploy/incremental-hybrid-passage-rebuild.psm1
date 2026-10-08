Set-StrictMode -Version Latest

function Invoke-HybridPassageRebuildFlow {
    param([System.Collections.IDictionary]$State, [scriptblock]$SaveState, [scriptblock]$Quiesce,
        [scriptblock]$ApplySchema, [scriptblock]$ResetAndPrepare, [scriptblock]$ActivateAndStart,
        [scriptblock]$RunAndFinish, [scriptblock]$Validate, [scriptblock]$ReleaseHold)
    & $Quiesce
    if (-not $State.SchemaAttempted) {
        $State.SchemaAttempted = $true
        & $SaveState # Must reach durable storage before any non-transactional schema command.
    }
    & $ApplySchema
    & $ResetAndPrepare
    & $SaveState
    & $ActivateAndStart
    & $RunAndFinish
    & $SaveState
    & $Validate
    & $ReleaseHold
    $State.HoldReleased = $true
    & $SaveState
}

function Get-HybridPassageMigrationContract {
    [pscustomobject]@{
        Baseline = '20260924125920_AddCorpusChunkFullTextIndex'
        Target = '20260927202655_AddCorpusRebuildSupersession'
        UpSha256 = '0C0AFC914FFCE360E2B2DED05B64FBF0E3C57C4139DF633C83B35BB049AD04B9'
        Suffix = @(
            '20260926180256_AddCoherentPassageProjection', '20260926182806_AddInteractiveGpuRequestOwnership',
            '20260926183316_AddGpuOcrTurnBound', '20260927062852_BindInteractiveGpuOwnerProcess',
            '20260927075141_AddCorpusPublicationVersions', '20260927080745_AddCorpusQueryLeases',
            '20260927085306_AddEmbeddingCheckpoints', '20260927093601_AddEmbeddingGpuRequests',
            '20260927115029_BindOutboxMessagesToJobs', '20260927115909_BindCompletedDeliveryArtifacts',
            '20260927121634_AddCorpusRebuildWorklist', '20260927202655_AddCorpusRebuildSupersession')
    }
}

function Assert-HybridMigrationHistory {
    param([string[]]$OriginalHistory, [string[]]$CurrentHistory)
    $contract = Get-HybridPassageMigrationContract
    if ($OriginalHistory.Count -eq 0 -or $OriginalHistory[-1] -cne $contract.Baseline) { throw 'hybrid-migration-baseline-mismatch' }
    $expected = @($OriginalHistory) + @($contract.Suffix)
    if ($CurrentHistory.Count -lt $OriginalHistory.Count -or $CurrentHistory.Count -gt $expected.Count) { throw 'hybrid-migration-history-mismatch' }
    for ($index = 0; $index -lt $CurrentHistory.Count; $index++) {
        if ($CurrentHistory[$index] -cne $expected[$index]) { throw 'hybrid-migration-history-mismatch' }
    }
}

function Invoke-WithHybridGpuDrain {
    param([Parameter(Mandatory)][string]$ConnectionString, [ValidateRange(1, 1800)][int]$TimeoutSeconds,
        [Parameter(Mandatory)][scriptblock]$StopApplication)
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    $builder['Pooling'] = $false
    $builder['ConnectRetryCount'] = 0
    $builder['Application Name'] = 'FluxKnowledge.HybridUpdaterDrain'
    $connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = @'
DECLARE @result int;
EXEC @result = sp_getapplock @Resource=N'FluxKnowledge.GpuScheduler.Admission',
    @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=10000;
IF @result < 0 THROW 51000, 'gpu-maintenance-admission-fence-unavailable', 1;
SELECT @@SPID;
'@
            $sessionId = [int]$command.ExecuteScalar()
            $command.CommandText = @'
IF ISNULL(APPLOCK_MODE(N'public', N'FluxKnowledge.GpuScheduler.Admission', N'Session'), N'NoLock') <> N'Exclusive'
    THROW 51000, 'gpu-maintenance-admission-fence-lost', 1;
SELECT CONVERT(bit, CASE WHEN
    EXISTS (SELECT 1 FROM GpuCapacitySlots WHERE SlotKey COLLATE Latin1_General_100_BIN2 = N'paddleocr-vl-local-gpu-0')
    AND NOT EXISTS (SELECT 1 FROM GpuCapacitySlots WHERE State <> 0 OR ActiveBatchId IS NOT NULL)
    AND NOT EXISTS (SELECT 1 FROM GpuMiniTasks WHERE State = 1)
    THEN 1 ELSE 0 END);
'@
            $timer = [Diagnostics.Stopwatch]::StartNew()
            while (-not [bool]$command.ExecuteScalar()) {
                if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'hybrid-gpu-drain-timeout-or-uncertain-capacity' }
                Start-Sleep -Milliseconds 250
            }
            & $StopApplication $sessionId
            # A killed/disconnected session cannot certify the stop or silently reconnect.
            if (-not [bool]$command.ExecuteScalar()) { throw 'hybrid-gpu-drain-proof-lost' }
        }
        finally { $command.Dispose() }
    }
    finally { $connection.Dispose() }
}

function Invoke-HybridRebuildOperator {
    param([string]$OperatorRoot, [string]$ConnectionString, [string[]]$Arguments,
        [ValidateRange(1, 3600)][int]$TimeoutSeconds = 900)
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add((Join-Path $OperatorRoot 'FluxKnowledge.Cli.dll'))
    $start.ArgumentList.Add('corpus-rebuild')
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['ConnectionStrings__FluxKnowledge'] = $ConnectionString
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw 'hybrid-operator-timeout-reconcile-durable-receipt-before-resume'
        }
        $errorText = $errorOutput.GetAwaiter().GetResult().Trim()
        if ($process.ExitCode -ne 0) {
            if ($errorText -notmatch '^corpus-rebuild-[a-z-]+$') { $errorText = 'corpus-rebuild-operator-unavailable' }
            throw $errorText
        }
        return ($output.GetAwaiter().GetResult() | ConvertFrom-Json)
    }
    finally { $process.Dispose() }
}

function Write-HybridRebuildJson {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    $staging = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 30 -Compress))
        $stream = [IO.File]::Open($staging, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        [IO.File]::Move($staging, $Path, $true)
    }
    finally { if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Force } }
}

function Set-HybridDeploymentHold {
    param([string]$Path, [string]$ReleaseId, [Guid]$OperationId, [bool]$Permit)
    Assert-HybridDeploymentHoldOwner -Path $Path -ReleaseId $ReleaseId -OperationId $OperationId
    $payload = if ($Permit) { @{ version=1; releaseId=$ReleaseId; corpusRebuildOperationId=$OperationId.ToString('D') } } else { $ReleaseId }
    Write-HybridRebuildJson -Path $Path -Value $payload
}

function Assert-HybridDeploymentHoldOwner {
    param([string]$Path, [string]$ReleaseId, [Guid]$OperationId)
    foreach ($candidate in @((Split-Path -Parent $Path), $Path)) {
        $item = Get-Item -LiteralPath $candidate -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'hybrid-hold-path-unsafe' }
    }
    $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    if ($value -is [string] -and $value -ceq $ReleaseId) { return }
    if ($value -is [System.Collections.IDictionary] -and $value.Count -eq 3 -and $value.version -eq 1 -and
        $value.releaseId -ceq $ReleaseId -and $value.corpusRebuildOperationId -ceq $OperationId.ToString('D')) { return }
    throw 'hybrid-hold-not-owned-by-release-and-operation'
}

function Get-HybridPayloadFingerprint {
    param([string]$Path)
    $root = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $observations = [Collections.Generic.List[string]]::new()
    foreach ($item in @(Get-ChildItem -LiteralPath $Path -Recurse -Force | Sort-Object FullName)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'hybrid-payload-reparse-point' }
        if (-not $item.PSIsContainer) {
            $observations.Add($item.FullName.Substring($root.Length) + '|' + $item.Length + '|' + (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash)
        }
    }
    if ($observations.Count -eq 0) { throw 'hybrid-payload-empty' }
    $hasher=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes(($observations -join "`n")))).Replace('-','') }
    finally { $hasher.Dispose() }
}

function Get-HybridReplacementSqlReceipt {
    param([string]$ConnectionString, [Guid]$OperationId)
    $connection = [Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open(); $command = $connection.CreateCommand()
        try {
            $command.CommandText = @'
SELECT op.ManifestHash, op.TargetEpoch, op.CompletedAtUtc,
    JSON_VALUE(op.ManifestJson, '$.Supersession.OperationId') AS SupersedesOperationId,
    state.CorpusRebuildOperationId AS ActiveOperationId, state.CorpusEpoch
FROM CorpusRebuildOperations op CROSS JOIN IndexState state
WHERE op.Id = @operation AND state.Id = 1;
'@
            [void]$command.Parameters.Add('@operation', [Data.SqlDbType]::UniqueIdentifier)
            $command.Parameters['@operation'].Value = $OperationId
            $reader = $command.ExecuteReader()
            try {
                if (-not $reader.Read()) { return @{ Committed=$false } }
                $receipt = @{ Committed=$true; OperationId=$OperationId.ToString('D') }
                for ($index=0; $index -lt $reader.FieldCount; $index++) {
                    $receipt[$reader.GetName($index)] = if ($reader.IsDBNull($index)) { $null } else { $reader.GetValue($index) }
                }
                if ($reader.Read()) { throw 'hybrid-replacement-sql-receipt-ambiguous' }
                return $receipt
            } finally { $reader.Dispose() }
        } finally { $command.Dispose() }
    } finally { $connection.Dispose() }
}

function Get-HybridReplacementBinding {
    param([string]$RecoveryRoot, [string]$ReleaseId, [string]$ConnectionString)
    if ($ReleaseId -notmatch '^\d{8}T\d{6}Z-[0-9a-f]{12}-hybrid$') { throw 'hybrid-replacement-release-invalid' }
    $root = Join-Path $RecoveryRoot $ReleaseId
    foreach ($path in @($RecoveryRoot, $root, (Join-Path $root 'hybrid-state.json'), (Join-Path $root 'corpus-rebuild-manifest.json'),
        (Join-Path $root 'candidate-config.json'), (Join-Path $root 'hybrid-idempotent-up.sql'))) {
        if (((Get-Item -LiteralPath $path -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'hybrid-replacement-packet-unsafe' }
    }
    $state = Get-Content -LiteralPath (Join-Path $root 'hybrid-state.json') -Raw | ConvertFrom-Json -AsHashtable
    $manifest = Get-Content -LiteralPath (Join-Path $root 'corpus-rebuild-manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($field in @('Version','ReleaseId','Commit','OperationId','DatabaseServer','DatabaseName','OriginalHistory','SchemaAttempted',
        'ResetCommitted','HoldReleased','HoldReleaseAttempted','ResetManifestHash','CandidateHash','OperatorHash','ActivatedConfigHash','PreservedInputFingerprint','InteractiveHostWasEnabled')) {
        if (-not $state.ContainsKey($field)) { throw 'hybrid-replacement-state-incomplete' }
    }
    foreach ($field in @('SchemaAttempted','ResetCommitted','HoldReleased','HoldReleaseAttempted','InteractiveHostWasEnabled')) {
        if ($state[$field] -isnot [bool]) { throw 'hybrid-replacement-state-ambiguous' }
    }
    $database = [Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    $operation = [Guid]::Empty; $epoch = [Guid]::Empty
    if ($state.Version -ne 1 -or $state.ReleaseId -cne $ReleaseId -or -not $state.SchemaAttempted -or -not $state.ResetCommitted -or
        $state.HoldReleased -or $state.HoldReleaseAttempted -or $state.Commit -cnotmatch ('^' + [Regex]::Escape(($ReleaseId -split '-')[1]) + '[0-9a-f]{28}$') -or
        $state.DatabaseServer -cne $database.DataSource -or $state.DatabaseName -cne $database.InitialCatalog -or
        -not [Guid]::TryParseExact($state.OperationId, 'D', [ref]$operation) -or $operation -eq [Guid]::Empty -or
        -not [Guid]::TryParseExact($manifest.TargetEpoch, 'D', [ref]$epoch) -or $epoch -eq [Guid]::Empty -or
        $manifest.OperationId -cne $state.OperationId -or $manifest.ManifestHash -cne $state.ResetManifestHash -or
        $manifest.DatabaseServer -cne $state.DatabaseServer -or $manifest.DatabaseName -cne $state.DatabaseName) { throw 'hybrid-replacement-identity-mismatch' }
    foreach ($payload in @(@{ Path=(Join-Path $root 'candidate'); Hash=$state.CandidateHash }, @{ Path=(Join-Path $root 'operator'); Hash=$state.OperatorHash })) {
        if ((Get-HybridPayloadFingerprint -Path $payload.Path) -cne $payload.Hash) { throw 'hybrid-replacement-payload-changed' }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $root 'candidate-config.json')).Hash -cne $state.ActivatedConfigHash -or
        (Get-FileHash -LiteralPath (Join-Path $root 'hybrid-idempotent-up.sql')).Hash -cne '33EBCF7F22B4E0E7D82F9750D1A1F4C0BDBCED62DF735B4DD06F74B612C3817B') { throw 'hybrid-replacement-configuration-or-schema-changed' }
    $receipt = Get-HybridReplacementSqlReceipt -ConnectionString $ConnectionString -OperationId $operation
    if (-not $receipt.Committed -or $receipt.CompletedAtUtc -or [string]$receipt.ActiveOperationId -cne $state.OperationId -or
        [string]$receipt.TargetEpoch -cne [string]$manifest.TargetEpoch -or [string]$receipt.CorpusEpoch -cne [string]$manifest.TargetEpoch -or
        $receipt.ManifestHash -cne $state.ResetManifestHash) { throw 'hybrid-replacement-sql-binding-mismatch' }
    return @{
        ReleaseId=$ReleaseId; OperationId=$state.OperationId; TargetEpoch=$manifest.TargetEpoch; ManifestHash=$state.ResetManifestHash
        DatabaseServer=$state.DatabaseServer; DatabaseName=$state.DatabaseName; OriginalHistory=$state.OriginalHistory
        CandidateHash=$state.CandidateHash; ActivatedConfigHash=$state.ActivatedConfigHash; PreservedInputFingerprint=$state.PreservedInputFingerprint
        JournalHash=(Get-FileHash -LiteralPath (Join-Path $root 'hybrid-state.json')).Hash
        ManifestFileHash=(Get-FileHash -LiteralPath (Join-Path $root 'corpus-rebuild-manifest.json')).Hash
        OperatorHash=$state.OperatorHash
        InteractiveHostWasEnabled=$state.InteractiveHostWasEnabled
    }
}

function Assert-HybridReplacementPacket {
    param([string]$RecoveryRoot, [System.Collections.IDictionary]$Binding)
    if ($Binding.ReleaseId -notmatch '^\d{8}T\d{6}Z-[0-9a-f]{12}-hybrid$') { throw 'hybrid-replacement-release-invalid' }
    $root = Join-Path $RecoveryRoot $Binding.ReleaseId
    foreach ($path in @($RecoveryRoot,$root)) {
        if (((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'hybrid-replacement-packet-unsafe' }
    }
    foreach ($file in @(@{ Name='hybrid-state.json'; Hash=$Binding.JournalHash }, @{ Name='corpus-rebuild-manifest.json'; Hash=$Binding.ManifestFileHash })) {
        $path = Join-Path $root $file.Name
        if (((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            (Get-FileHash -LiteralPath $path).Hash -cne $file.Hash) { throw 'hybrid-replacement-predecessor-packet-changed' }
    }
    if ((Get-HybridPayloadFingerprint (Join-Path $root 'candidate')) -cne $Binding.CandidateHash -or
        (Get-HybridPayloadFingerprint (Join-Path $root 'operator')) -cne $Binding.OperatorHash -or
        (Get-FileHash -LiteralPath (Join-Path $root 'candidate-config.json')).Hash -cne $Binding.ActivatedConfigHash -or
        (Get-FileHash -LiteralPath (Join-Path $root 'hybrid-idempotent-up.sql')).Hash -cne '33EBCF7F22B4E0E7D82F9750D1A1F4C0BDBCED62DF735B4DD06F74B612C3817B') {
        throw 'hybrid-replacement-predecessor-packet-changed'
    }
}

function Assert-HybridReplacementReceipt {
    param($Receipt, [System.Collections.IDictionary]$Manifest, [System.Collections.IDictionary]$Binding)
    if (-not $Receipt.Committed -or [string]$Receipt.OperationId -cne [string]$Manifest.OperationId -or
        $Receipt.ManifestHash -cne $Manifest.ManifestHash -or [string]$Receipt.TargetEpoch -cne [string]$Manifest.TargetEpoch -or
        [string]$Receipt.CorpusEpoch -cne [string]$Manifest.TargetEpoch -or
        [string]$Receipt.SupersedesOperationId -cne [string]$Binding.OperationId -or
        [string]$Manifest.Supersession.OperationId -cne [string]$Binding.OperationId -or
        $Manifest.Supersession.ManifestHash -cne $Binding.ManifestHash -or [string]$Manifest.Supersession.TargetEpoch -cne [string]$Binding.TargetEpoch -or
        ($Receipt.ActiveOperationId -and [string]$Receipt.ActiveOperationId -cne [string]$Manifest.OperationId)) { throw 'hybrid-replacement-reset-receipt-mismatch' }
}

function Move-HybridReplacementHold {
    param([string]$Path, [System.Collections.IDictionary]$Binding, [string]$ReleaseId, [Guid]$OperationId)
    # The updater's machine mutex excludes competing release owners. This never creates a missing hold.
    $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    if (($value -is [string] -and $value -ceq $ReleaseId) -or
        ($value -is [System.Collections.IDictionary] -and $value.Count -eq 3 -and $value.version -eq 1 -and
         $value.releaseId -ceq $ReleaseId -and $value.corpusRebuildOperationId -ceq $OperationId.ToString('D'))) {
        Assert-HybridDeploymentHoldOwner -Path $Path -ReleaseId $ReleaseId -OperationId $OperationId
        return
    }
    Assert-HybridDeploymentHoldOwner -Path $Path -ReleaseId $Binding.ReleaseId -OperationId $Binding.OperationId
    Write-HybridRebuildJson -Path $Path -Value $ReleaseId
}

function Get-HybridPatchBinding {
    param([string]$RecoveryRoot, [string]$ReleaseId, [string]$ConnectionString)
    if ($ReleaseId -notmatch '^\d{8}T\d{6}Z-[0-9a-f]{12}-hybrid$') { throw 'hybrid-patch-release-invalid' }
    $root = Join-Path $RecoveryRoot $ReleaseId
    foreach ($path in @($RecoveryRoot,$root,(Join-Path $root 'hybrid-state.json'),
        (Join-Path $root 'corpus-rebuild-manifest.json'),(Join-Path $root 'candidate'),
        (Join-Path $root 'operator'),(Join-Path $root 'candidate-config.json'),(Join-Path $root 'hybrid-idempotent-up.sql'))) {
        if (((Get-Item -LiteralPath $path -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'hybrid-patch-predecessor-path-unsafe' }
    }
    $state=Get-Content -LiteralPath (Join-Path $root 'hybrid-state.json') -Raw | ConvertFrom-Json -AsHashtable
    $manifest=Get-Content -LiteralPath (Join-Path $root 'corpus-rebuild-manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($field in @('Version','ReleaseId','Commit','OperationId','DatabaseServer','DatabaseName','OriginalHistory',
        'SchemaAttempted','ResetCommitted','HoldReleased','HoldReleaseAttempted','ResetManifestHash','CandidateHash',
        'OperatorHash','ActivatedConfigHash','PreservedInputFingerprint','InteractiveHostWasEnabled')) {
        if (-not $state.ContainsKey($field)) { throw 'hybrid-patch-predecessor-incomplete' }
    }
    $database=[Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    $operation=[Guid]::Empty; $epoch=[Guid]::Empty
    if ($state.Version -ne 1 -or $state.ReleaseId -cne $ReleaseId -or
        $state.Commit -cnotmatch ('^' + [Regex]::Escape(($ReleaseId -split '-')[1]) + '[0-9a-f]{28}$') -or
        -not $state.SchemaAttempted -or -not $state.ResetCommitted -or $state.HoldReleased -or $state.HoldReleaseAttempted -or
        $state.DatabaseServer -cne $database.DataSource -or $state.DatabaseName -cne $database.InitialCatalog -or
        -not [Guid]::TryParseExact($state.OperationId,'D',[ref]$operation) -or $operation -eq [Guid]::Empty -or
        -not [Guid]::TryParseExact($manifest.TargetEpoch,'D',[ref]$epoch) -or $epoch -eq [Guid]::Empty -or
        $manifest.OperationId -cne $state.OperationId -or $manifest.ManifestHash -cne $state.ResetManifestHash -or
        $manifest.DatabaseServer -cne $state.DatabaseServer -or $manifest.DatabaseName -cne $state.DatabaseName) { throw 'hybrid-patch-predecessor-identity-mismatch' }
    foreach ($flag in @('SchemaAttempted','ResetCommitted','HoldReleased','HoldReleaseAttempted','InteractiveHostWasEnabled')) {
        if ($state[$flag] -isnot [bool]) { throw 'hybrid-patch-predecessor-ambiguous' }
    }
    if ((Get-HybridPayloadFingerprint (Join-Path $root 'candidate')) -cne $state.CandidateHash -or
        (Get-HybridPayloadFingerprint (Join-Path $root 'operator')) -cne $state.OperatorHash -or
        (Get-FileHash -LiteralPath (Join-Path $root 'candidate-config.json')).Hash -cne $state.ActivatedConfigHash -or
        (Get-FileHash -LiteralPath (Join-Path $root 'hybrid-idempotent-up.sql')).Hash -cne (Get-HybridPassageMigrationContract).UpSha256) {
        throw 'hybrid-patch-predecessor-packet-changed'
    }
    if ($state.ContainsKey('Replacement')) { Assert-HybridReplacementPacket -RecoveryRoot $RecoveryRoot -Binding $state.Replacement }
    $receipt=Get-HybridReplacementSqlReceipt -ConnectionString $ConnectionString -OperationId $operation
    if (-not $receipt.Committed -or $receipt.ManifestHash -cne $manifest.ManifestHash -or
        [string]$receipt.TargetEpoch -cne [string]$manifest.TargetEpoch -or [string]$receipt.CorpusEpoch -cne [string]$manifest.TargetEpoch -or
        (($receipt.CompletedAtUtc -and $receipt.ActiveOperationId) -or
         (-not $receipt.CompletedAtUtc -and [string]$receipt.ActiveOperationId -cne $state.OperationId))) {
        throw 'hybrid-patch-sql-binding-mismatch'
    }
    if ($state.ContainsKey('Replacement') -and [string]$receipt.SupersedesOperationId -cne [string]$state.Replacement.OperationId) {
        throw 'hybrid-patch-supersession-binding-mismatch'
    }
    return @{
        ReleaseId=$ReleaseId; OperationId=$state.OperationId; TargetEpoch=$manifest.TargetEpoch; ManifestHash=$state.ResetManifestHash
        DatabaseServer=$state.DatabaseServer; DatabaseName=$state.DatabaseName; OriginalHistory=$state.OriginalHistory
        CandidateHash=$state.CandidateHash; OperatorHash=$state.OperatorHash; ActivatedConfigHash=$state.ActivatedConfigHash
        PreservedInputFingerprint=$state.PreservedInputFingerprint; InteractiveHostWasEnabled=$state.InteractiveHostWasEnabled
        JournalHash=(Get-FileHash -LiteralPath (Join-Path $root 'hybrid-state.json')).Hash
        ManifestFileHash=(Get-FileHash -LiteralPath (Join-Path $root 'corpus-rebuild-manifest.json')).Hash
    }
}

function Assert-HybridPatchPacket {
    param([string]$RecoveryRoot, [System.Collections.IDictionary]$Binding, [string]$ConnectionString)
    $current=Get-HybridPatchBinding -RecoveryRoot $RecoveryRoot -ReleaseId $Binding.ReleaseId -ConnectionString $ConnectionString
    foreach ($field in @('OperationId','TargetEpoch','ManifestHash','DatabaseServer','DatabaseName','CandidateHash',
        'OperatorHash','ActivatedConfigHash','PreservedInputFingerprint','JournalHash','ManifestFileHash','InteractiveHostWasEnabled')) {
        if ([string]$current[$field] -cne [string]$Binding[$field]) { throw 'hybrid-patch-predecessor-packet-changed' }
    }
    if ((@($current.OriginalHistory) -join '|') -cne (@($Binding.OriginalHistory) -join '|')) { throw 'hybrid-patch-predecessor-history-changed' }
}

Export-ModuleMember -Function Invoke-HybridPassageRebuildFlow, Get-HybridPassageMigrationContract,
    Assert-HybridMigrationHistory, Invoke-WithHybridGpuDrain, Invoke-HybridRebuildOperator, Write-HybridRebuildJson,
    Set-HybridDeploymentHold, Assert-HybridDeploymentHoldOwner, Get-HybridPayloadFingerprint,
    Get-HybridReplacementBinding, Get-HybridReplacementSqlReceipt, Assert-HybridReplacementPacket,
    Assert-HybridReplacementReceipt, Move-HybridReplacementHold, Get-HybridPatchBinding, Assert-HybridPatchPacket
