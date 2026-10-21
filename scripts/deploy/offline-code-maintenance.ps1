# Dot-sourced by the canonical updater. Version 1 is code-only; version 2
# additionally binds one source-access activation. Neither settles GPU work.
function Get-OfflineSourceAccessHelperObservation {
    param([hashtable]$Binding)
    $root=Assert-SegmentedUpdatePath $Binding.InstallRoot
    $payload=Join-Path $root 'payload'
    $security=[Collections.Generic.List[string]]::new()
    foreach($item in @((Get-Item -LiteralPath $root), (Get-Item -LiteralPath ([IO.Path]::GetPathRoot($root)))) +
        @(Get-ChildItem -LiteralPath $root -Recurse -Force | Sort-Object FullName)) {
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'offline-source-access-helper-reparse-point'}
        # Pending intents are mutable diagnostics, not execution/configuration inputs.
        if($item.FullName.StartsWith((Join-Path $root 'journal')+'\',[StringComparison]::OrdinalIgnoreCase)){continue}
        $security.Add($item.FullName+'|'+(Get-Acl -LiteralPath $item.FullName).Sddl)
    }
    $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect()
    $task=$scheduler.GetFolder('\').GetTask($Binding.TaskName)
    if(!$task.Enabled -or $task.State -ne 4){throw 'offline-source-access-helper-not-running'}
    $hash={param([string]$Text) [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)))}
    return @{PayloadSha256=(Get-HybridPayloadFingerprint $payload);
        PolicySha256=(Get-FileHash -LiteralPath (Join-Path $root 'policy.json')).Hash;
        InstallationReceiptSha256=(Get-FileHash -LiteralPath (Join-Path $root 'installation-receipt.json')).Hash;
        FileSecuritySha256=(& $hash ($security -join "`n"));
        TaskDefinitionSha256=(& $hash $task.Xml);TaskSecuritySha256=(& $hash ($task.GetSecurityDescriptor(7)))}
}

function Get-OfflineSourceAccessRootInspection {
    param([hashtable]$Binding)
    $plan=& (Join-Path $Binding.InstallRoot 'payload/FluxKnowledge.Cli.exe') source-access plan-root-read --request $Binding.RootReadRequestPath
    if($LASTEXITCODE -ne 0){throw 'offline-source-access-root-inspection-failed'}
    return ($plan | ConvertFrom-Json -AsHashtable -DateKind String)
}

function Assert-OfflineSourceAccessHelperBinding {
    param([hashtable]$Binding)
    if(!$Binding -or $Binding.InstallRoot -cnotmatch '^[A-Z]:\\FluxKnowledgeSourceAccess(?:-[A-Za-z0-9-]+)?$' -or
        $Binding.TaskName -cnotmatch '^FluxKnowledge\.SourceAccess(?:-Staging-[A-Za-z0-9-]+)?$' -or
        $Binding.RootReadRequestSha256 -cnotmatch '^[A-F0-9]{64}$' -or $Binding.RootIdentityFingerprint -notmatch '^[a-f0-9]{64}$'){
        throw 'offline-source-access-helper-binding-required'
    }
    $observed=Get-OfflineSourceAccessHelperObservation $Binding
    foreach($key in @('PayloadSha256','PolicySha256','InstallationReceiptSha256','FileSecuritySha256','TaskDefinitionSha256','TaskSecuritySha256')) {
        if($Binding[$key] -cnotmatch '^[A-F0-9]{64}$' -or $observed[$key] -cne $Binding[$key]){throw ('offline-source-access-helper-changed: '+$key)}
    }
    # Only execute the model-free inspection after the sealed payload, private
    # execution chain and protected SYSTEM task have matched the reviewed receipt.
    $policy=Read-SegmentedUpdateJson (Join-Path $Binding.InstallRoot 'policy.json') $Binding.PolicySha256
    $request=Read-SegmentedUpdateJson $Binding.RootReadRequestPath $Binding.RootReadRequestSha256
    if(@($policy.Roots).Count -ne 1 -or $policy.PipeName -cne $Binding.PipeName -or $policy.IndexerSid -cne $Binding.IndexerSid -or
        $policy.Roots[0].Path -cne $Binding.SourceRoot -or $policy.Roots[0].IdentityFingerprint -cne $Binding.RootIdentityFingerprint -or
        $request.RootPath -cne $Binding.SourceRoot -or $request.IndexerSid -cne $Binding.IndexerSid){throw 'offline-source-access-helper-policy-changed'}
    if((Get-OfflineSourceAccessRootInspection $Binding).RootIdentityFingerprint -cne $Binding.RootIdentityFingerprint){throw 'offline-source-access-root-identity-changed'}
}

function Get-OfflineMaintenanceConfigurationHash {
    param([hashtable]$Receipt,[switch]$Candidate)
    if($Receipt.Version -eq 1) {
        if($sourceAccessChange){throw 'offline-source-access-requires-version2'}
        return $Receipt.ConfigurationSha256
    }
    $binding=$Receipt.SourceAccessConfiguration
    if(!$binding -or !$sourceAccessChange -or !$sourceAccessChange.Enabled -or !$binding.Enabled -or
        $binding.BeforeSha256 -cne $Receipt.ConfigurationSha256 -or $binding.BeforeSha256 -cne $sourceAccessChange.BeforeHash -or
        $binding.AfterSha256 -cne $sourceAccessChange.AfterHash -or $binding.PipeName -cne $sourceAccessChange.PipeName -or
        $binding.Helper.PipeName -cne $binding.PipeName){throw 'offline-source-access-configuration-binding-changed'}
    Assert-OfflineSourceAccessHelperBinding $binding.Helper
    $applied=$sourceAccessChange.Attempted -and !$sourceAccessChange.RollbackVerified
    if($Candidate -and !$applied){throw 'offline-source-access-candidate-not-applied'}
    return $(if($applied){$binding.AfterSha256}else{$binding.BeforeSha256})
}

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
    if($receipt.Version -notin @(1,2) -or !$receipt.Stop.Completed -or $receipt.Stop.Phase -cne 'iis-writers-stopped' -or
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
    $configurationHash=Get-OfflineMaintenanceConfigurationHash $receipt -Candidate:$CandidatePayloads
    foreach($binding in @(@{Path=(Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json');Hash=$configurationHash},
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
