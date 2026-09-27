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
        Target = '20260927121634_AddCorpusRebuildWorklist'
        UpSha256 = '33EBCF7F22B4E0E7D82F9750D1A1F4C0BDBCED62DF735B4DD06F74B612C3817B'
        Suffix = @(
            '20260926180256_AddCoherentPassageProjection', '20260926182806_AddInteractiveGpuRequestOwnership',
            '20260926183316_AddGpuOcrTurnBound', '20260927062852_BindInteractiveGpuOwnerProcess',
            '20260927075141_AddCorpusPublicationVersions', '20260927080745_AddCorpusQueryLeases',
            '20260927085306_AddEmbeddingCheckpoints', '20260927093601_AddEmbeddingGpuRequests',
            '20260927115029_BindOutboxMessagesToJobs', '20260927115909_BindCompletedDeliveryArtifacts',
            '20260927121634_AddCorpusRebuildWorklist')
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
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($observations -join "`n"))))
}

Export-ModuleMember -Function Invoke-HybridPassageRebuildFlow, Get-HybridPassageMigrationContract,
    Assert-HybridMigrationHistory, Invoke-WithHybridGpuDrain, Invoke-HybridRebuildOperator, Write-HybridRebuildJson,
    Set-HybridDeploymentHold, Assert-HybridDeploymentHoldOwner, Get-HybridPayloadFingerprint
