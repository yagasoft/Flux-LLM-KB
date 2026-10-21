# Dot-sourced by the canonical updater. This is a code-only extension of
# RecoverStoppedPool; it never settles GPU work or converts an index.
function Get-OfflineCodeDatabaseBinding {
    & (Get-Module 'incremental-segmented-index-schema') {
    param($connectionString)
    $connection=Open-SegmentedSchemaConnection $connectionString
    try {
        $schema=Read-SegmentedSchemaState $connection
        Assert-SegmentedIndexApplicationSchema $schema
        if($schema.RequiredFormat -ne 2){throw 'offline-code-format2-required'}
        $command=$connection.CreateCommand()
        try {
            $command.CommandTimeout=10
            $command.CommandText=@'
SELECT s.ActiveIndexGenerationId,s.CorpusEpoch,s.CorpusVersion,s.RequiredIndexFormat,
       g.MetadataChecksum,g.IndexPath,g.VectorCount,g.SegmentCount,g.ModelFingerprint,g.Dimensions,g.ValidatedAtUtc
FROM dbo.IndexState s JOIN dbo.IndexGenerations g ON g.Id=s.ActiveIndexGenerationId
WHERE s.Id=1 AND s.RequiredIndexFormat=2 AND s.CorpusRebuildOperationId IS NULL
  AND g.FormatVersion=2 AND g.CorpusEpoch=s.CorpusEpoch AND g.CorpusVersion=s.CorpusVersion
  AND g.ValidatedAtUtc IS NOT NULL AND g.RetiredAtUtc IS NULL AND g.ReclaimedAtUtc IS NULL
FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
'@
            $projection=[string]$command.ExecuteScalar()
            if(!$projection){throw 'offline-code-current-projection-required'}
        } finally {$command.Dispose()}
        return @{Identity=$schema.Identity;SchemaSha256=$schema.SchemaSha256;History=(@($schema.History)-join '|');
            Projection=$projection;RequiredFormat=2}
    } finally {$connection.Dispose()}
    } (Get-DeploymentSqlConnectionString)
}

function Assert-OfflineCodeMaintenance {
    param([switch]$RequireStopped,[switch]$OriginalPayloads,[switch]$CandidatePayloads)
    $receipt=Read-SegmentedUpdateJson $OfflineCodeMaintenanceReceipt $ExpectedOfflineCodeMaintenanceReceiptSha256
    if($receipt.Version -ne 1 -or !$receipt.Stop.Completed -or $receipt.Stop.Phase -cne 'iis-writers-stopped' -or
        !$receipt.Stop.ExitProof.ContainsKey('ExitedIdentities') -or $receipt.Database.RequiredFormat -ne 2){throw 'offline-code-reviewed-receipt-required'}
    $verified=[DateTime]::Parse($receipt.Stop.ExitProof.VerifiedAtUtc).ToUniversalTime()
    if($verified -le [DateTime]::Parse($receipt.Stop.StartedUtc).ToUniversalTime() -or $verified -gt [DateTime]::UtcNow.AddSeconds(5)){
        throw 'offline-code-stop-time-invalid'
    }
    $state=if($offlineCodeState){Read-SegmentedUpdateJson $offlineCodeState.StatePath}else{$null}
    if($state -and ($state.ReceiptSha256 -cne $ExpectedOfflineCodeMaintenanceReceiptSha256 -or $state.ReleaseId -cne $offlineCodeState.ReleaseId)){
        throw 'offline-code-state-binding-changed'
    }
    if($RequireStopped -and $state -and $state.StartIntentUtc){throw 'offline-code-fresh-stop-required-after-start-intent'}
    if($RequireStopped){Assert-StoppedPoolRecoveryBaseline}
    $task=Get-RepositoryRecoveryTaskObservation
    if($task.Enabled -or $task.State -ceq 'Running' -or $task.IdentityHash -cne $receipt.TaskIdentityHash -or
        $task.LauncherSha256 -cne $receipt.LauncherSha256){throw 'offline-code-companion-owner-changed'}
    $launcher=[regex]::Escape($task.LauncherPath)
    $hostScript=[regex]::Escape((Join-Path $InteractiveHostRoot 'run-outlook-host.ps1'))
    $processes=@(Get-CimInstance Win32_Process -ErrorAction Stop)
    foreach($identity in $receipt.Stop.ExitProof.ExitedIdentities) {
        if(@($processes | Where-Object {$_.ProcessId -eq [int]$identity.ProcessId -and
            $_.CreationDate.ToUniversalTime() -eq [DateTime]::Parse($identity.Created).ToUniversalTime()}).Count){throw 'offline-code-owner-exit-not-proved'}
    }
    if(@($processes | Where-Object {
        $_.ProcessId -ne $PID -and ($_.Name -ceq 'FluxKnowledge.OutlookHost.exe' -or
            ($_.Name -ceq 'wscript.exe' -and $_.CommandLine -match ('(?:^|\s)"?'+$launcher+'"?(?:\s|$)')) -or
            ($_.Name -in @('pwsh.exe','powershell.exe') -and $_.CommandLine -match ('(?i)-File\s+"?'+$hostScript+'"?(?:\s|$)')) -or
            ($_.Name -ceq 'dotnet.exe' -and $_.CommandLine -match '(?:^|\s)(?:"[^"]*\\FluxKnowledge\.OutlookHost\.dll"|[^"\s]*\\FluxKnowledge\.OutlookHost\.dll)(?:\s|$)'))
    }).Count){throw 'offline-code-companion-exit-not-proved'}
    $holdOwner=if($state -and $state.HoldTransferred){$state.ReleaseId}else{$receipt.Stop.HoldOwner}
    if((Read-SegmentedUpdateJson $ValidationHoldPath) -cne $holdOwner){throw 'offline-code-hold-changed'}
    if(!$state -and (Get-FileHash -LiteralPath $ValidationHoldPath).Hash -cne $receipt.Stop.HoldSha256){throw 'offline-code-hold-changed'}
    $database=Get-OfflineCodeDatabaseBinding
    foreach($key in @('Identity','SchemaSha256','History','Projection','RequiredFormat')) {
        if($database[$key] -cne $receipt.Database[$key]){throw ('offline-code-database-changed: '+$key)}
    }
    foreach($binding in @(@{Path=(Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json');Hash=$receipt.ConfigurationSha256},
        @{Path=(Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json');Hash=$receipt.CompanionConfigurationSha256})) {
        [void](Assert-SegmentedUpdatePath $binding.Path)
        if((Get-FileHash -LiteralPath $binding.Path).Hash -cne $binding.Hash){throw 'offline-code-configuration-changed'}
    }
    if($OriginalPayloads -and ((Get-HybridPayloadFingerprint $CanonicalDeployRoot) -cne $receipt.WebSha256 -or
        (Get-HybridPayloadFingerprint $InteractiveHostRoot) -cne $receipt.CompanionSha256)){throw 'offline-code-original-payload-changed'}
    if($CandidatePayloads -and (!$state -or !$state.HoldTransferred -or
        (Get-HybridPayloadFingerprint $CanonicalDeployRoot) -cne $state.CandidateWebSha256 -or
        (Get-HybridPayloadFingerprint $InteractiveHostRoot) -cne $state.CandidateCompanionSha256)){throw 'offline-code-candidate-payload-changed'}
}

function New-OfflineCodeMaintenanceState {
    param([string]$StatePath,[string]$ReleaseId,[string]$CandidateWebSha256,[string]$CandidateCompanionSha256)
    if([IO.File]::Exists($StatePath)){throw 'offline-code-state-already-exists; reconcile instead of replaying'}
    Assert-OfflineCodeMaintenance -RequireStopped -OriginalPayloads
    $state=@{Version=1;StatePath=$StatePath;ReleaseId=$ReleaseId;ReceiptSha256=$ExpectedOfflineCodeMaintenanceReceiptSha256;
        ReceiptPath=$OfflineCodeMaintenanceReceipt;CandidateWebSha256=$CandidateWebSha256;CandidateCompanionSha256=$CandidateCompanionSha256;
        HoldTransferIntentUtc=$null;HoldTransferred=$false;StartIntentUtc=$null;ReleaseIntentUtc=$null}
    Write-SegmentedUpdateJson $StatePath $state
    return $state
}

function Move-OfflineCodeMaintenanceHold {
    param([string]$StatePath,[hashtable]$State)
    Assert-OfflineCodeMaintenance -RequireStopped -OriginalPayloads
    if($State.StartIntentUtc -or $State.HoldTransferIntentUtc){throw 'offline-code-hold-transfer-reconciliation-required'}
    $State.HoldTransferIntentUtc=[DateTime]::UtcNow.ToString('O')
    Write-SegmentedUpdateJson $StatePath $State
    # The deny-all file is replaced atomically; it is never absent between owners.
    Write-SegmentedUpdateJson $ValidationHoldPath $State.ReleaseId
    $State.HoldTransferred=$true
    Write-SegmentedUpdateJson $StatePath $State
}

function Start-OfflineCodeMaintenanceIntent {
    param([string]$StatePath,[hashtable]$State)
    Assert-OfflineCodeMaintenance -RequireStopped -CandidatePayloads
    $State.StartIntentUtc=[DateTime]::UtcNow.ToString('O')
    Write-SegmentedUpdateJson $StatePath $State
}
