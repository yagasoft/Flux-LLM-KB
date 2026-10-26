# Loaded by the canonical incremental updater. Targets and lifecycle state come
# from that script; this file has no standalone production action.
function Get-DeploymentSourceJournalReviewedHash([string]$Role) {
    $values=@($ReviewedSourceJournalPayloads|Where-Object {$_ -cmatch ('^'+$Role+'=')})
    if($ReviewedSourceJournalPayloads.Count -ne 3 -or $values.Count -ne 1){throw 'journal-release-complete-reviewed-set-required'}
    $values[0].Split('=')[1].ToUpperInvariant()
}

function Assert-DeploymentSourceJournalReleasePayloadSet([bool]$HelperInstalled) {
    foreach($payload in @(Get-DeploymentSourceJournalPayloads)){
        $path=if($payload.Role -ceq 'Cli' -and !$HelperInstalled){$sourceJournalHelperCandidateRoot}else{$payload.Root}
        if((Get-HybridPayloadFingerprint $path) -cne (Get-DeploymentSourceJournalReviewedHash $payload.Role)){
            throw 'journal-release-reviewed-payload-set-changed'
        }
    }
}

function Assert-DeploymentSourceJournalReleaseStopped {
    Assert-StoppedPoolRecoveryBaseline
    Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $releaseId
    $task=Get-RepositoryRecoveryTaskObservation
    if($task.Enabled -or $task.State -ceq 'Running'){throw 'journal-release-companion-must-remain-disabled'}
    Wait-InteractiveHostStopped -TaskName $InteractiveHostTaskName -TimeoutSeconds $ReadinessTimeoutSeconds
    Assert-DeploymentSourceJournalCliWritersStopped
    Assert-SourceAccessConfigurationChange -Change $sourceJournalChange -Original:(!$sourceJournalChange.Attempted)
}

function Assert-DeploymentSourceJournalCliWritersStopped {
    # The helper cannot write SQL. Other CLI invocations could bypass the held
    # hosted workers, so their absence is required rather than killing them.
    $helperExecutable=Join-Path $SourceJournalHelperRoot 'payload/FluxKnowledge.Cli.exe'
    $helperArguments='source-access serve --broker-root "'+$SourceJournalHelperRoot+'"'
    $helperProcesses=0
    foreach($process in @(Get-CimInstance Win32_Process -Filter "Name='FluxKnowledge.Cli.exe' OR Name='dotnet.exe'" -OperationTimeoutSec 5 -ErrorAction Stop)){
        if(!$process.ExecutablePath -or !$process.CommandLine){throw 'journal-release-cli-command-line-unavailable'}
        if($process.Name -ieq 'dotnet.exe'){
            if($process.CommandLine -imatch 'FluxKnowledge\.Cli\.dll(?=["\s]|$)'){throw 'journal-release-other-cli-writer-exit-unproved'}
            continue
        }
        if($process.ExecutablePath -ine $helperExecutable -or $process.CommandLine -cnotin
            @(('"'+$helperExecutable+'" '+$helperArguments),($helperExecutable+' '+$helperArguments))){
            throw 'journal-release-other-cli-writer-exit-unproved'
        }
        $owner=Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid -OperationTimeoutSec 5 -ErrorAction Stop
        if($owner.ReturnValue -ne 0 -or $owner.Sid -cne 'S-1-5-18' -or ++$helperProcesses -gt 1){throw 'journal-release-helper-process-identity-unproved'}
    }
}

function Get-DeploymentSourceJournalRepairMode {
    $settings=ConvertFrom-UniqueConfigurationJson $sourceJournalChange.After
    $appRepair=$false
    if($settings.Contains('SourceAccess')){
        if($settings.SourceAccess -isnot [Collections.IDictionary]){throw 'journal-release-repair-configuration-invalid'}
        if($settings.SourceAccess.Contains('Enabled')){
            if($settings.SourceAccess.Enabled -isnot [bool]){throw 'journal-release-repair-configuration-invalid'}
            $appRepair=$settings.SourceAccess.Enabled
        }
    }
    $helperRepair=$true # Before journal policy exists the original helper serves repair.
    $path=Join-Path $SourceJournalHelperRoot 'journal-policy.json'
    if(Test-Path -LiteralPath $path){
        $policy=ConvertFrom-UniqueConfigurationJson (Get-SourceAccessConfigurationBytes $path)
        if($policy.Contains('RepairEnabled') -and $policy.RepairEnabled -isnot [bool]){throw 'journal-release-repair-policy-invalid'}
        $helperRepair=$policy.Contains('RepairEnabled') -and $policy.RepairEnabled
    }
    if($appRepair -and !$helperRepair){throw 'journal-release-enabled-app-repair-has-disabled-helper'}
    @{AppEnabled=$appRepair;HelperEnabled=$helperRepair;ExpectedTransport=$(if($appRepair){'NotApproved'}else{'Disabled'})}
}

function Test-DeploymentSourceJournalHelperNativeState([bool]$Candidate) {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do{
        $task=&$sourceJournalNativePorts.ReadTask
        foreach($key in @('TaskName','PrincipalSid','Executable','Arguments','WorkingDirectory','DefinitionSha256','SecuritySha256')){
            if($task[$key] -cne $sourceJournalHelperState.OriginalTask[$key]){throw 'journal-release-helper-task-binding-changed'}
        }
        $processes=@(Get-SourceJournalHelperNativeProcesses $sourceJournalHelperState.OriginalTask.Executable)
        if($task.Enabled -eq $sourceJournalHelperState.OriginalTask.Enabled -and
            $task.Running -eq $sourceJournalHelperState.OriginalTask.Running -and
            (!$task.Running -or $processes.Count -eq 1)){break}
        if($watch.Elapsed.TotalSeconds -ge 15){throw 'journal-release-helper-native-start-unconfirmed'}
        Start-Sleep -Milliseconds 200
    }while($true)
    $expected=if($Candidate){$sourceJournalHelperState.CandidatePayloadSha256}else{$sourceJournalHelperState.OriginalPayloadSha256}
    if((Get-SourceJournalHelperPayloadManifest $sourceJournalHelperState.PayloadRoot).Sha256 -cne $expected){
        throw 'journal-release-helper-payload-unconfirmed'
    }
    foreach($binding in @(@('policy.json','RepairPolicySha256'),@('root-read-plan.json','RootReadPlanSha256'))){
        if((Get-FileHash -LiteralPath (Join-Path $SourceJournalHelperRoot $binding[0])).Hash -cne $sourceJournalHelperState[$binding[1]]){
            throw 'journal-release-helper-repair-binding-changed'
        }
    }
    Assert-DeploymentSourceJournalCliWritersStopped
    $sourceJournalRelease.TransportDeferred=$true
}

function Confirm-DeploymentSourceJournalHelperTransport {
    Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $releaseId
    Assert-SourceAccessConfigurationChange -Change $sourceJournalChange
    Assert-SourceAccessConfigurationChange -Change $sourceJournalHelperState.Policy
    Test-DeploymentSourceJournalHelperNativeState $true
    $policy=ConvertFrom-UniqueConfigurationJson (Get-SourceAccessConfigurationBytes (Join-Path $SourceJournalHelperRoot 'policy.json'))
    $options=(ConvertFrom-UniqueConfigurationJson $sourceJournalChange.After).SourceJournal
    $response=Invoke-RestMethod -Uri ($loopbackOrigin.Origin+'/health/source-journal-helper') -Method Get -TimeoutSec 12
    if($response.Version -ne 1 -or $response.VolumesChecked -ne $options.ApprovedVolumes.Count -or
        $response.RepairTransport -cne $sourceJournalRelease.RepairMode.ExpectedTransport -or $response.CallerSid -cne $policy.IndexerSid){
        throw 'journal-release-held-transport-unconfirmed'
    }
    Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $releaseId
    Assert-SourceAccessConfigurationChange -Change $sourceJournalChange
    Assert-SourceAccessConfigurationChange -Change $sourceJournalHelperState.Policy
    Test-DeploymentSourceJournalHelperNativeState $true
    $sourceJournalRelease.TransportConfirmed=$true
    Write-SegmentedUpdateJson (Join-Path $releaseRoot 'source-journal-transport.json') @{
        Version=1;ReleaseId=$releaseId;CallerSid=$response.CallerSid;VolumesChecked=$response.VolumesChecked;
        RepairTransport=$response.RepairTransport;ObservedAtUtc=[DateTimeOffset]::UtcNow;
        RepairPolicySha256=$sourceJournalHelperState.RepairPolicySha256;JournalPolicySha256=$sourceJournalHelperState.Policy.AfterHash
    }
}

function Invoke-DeploymentSourceJournalVerificationRetirement {
    Assert-DeploymentSourceJournalReleaseStopped
    $operatorRoot=Join-Path $SourceJournalHelperRoot 'payload'
    $entry=Join-Path $operatorRoot 'FluxKnowledge.Cli.dll'
    if(!('FluxKnowledge.Deployment.ProjectionOperatorLoadContext' -as [type])){
        Add-Type -Path (Join-Path $PSScriptRoot 'ProjectionOperatorLoadContext.cs') -ErrorAction Stop
    }
    $context=[FluxKnowledge.Deployment.ProjectionOperatorLoadContext]::new($entry,(Get-ProjectionOperatorFramework $operatorRoot))
    $assembly=$context.LoadFromAssemblyPath((Join-Path $operatorRoot 'FluxKnowledge.Integrations.dll'))
    $identity=$assembly.GetType('FluxKnowledge.Integrations.Files.PhysicalFileIdentity',$true).GetMethod('GetDirectory').Invoke(
        $null,@((Join-Path $CanonicalLiveRoot 'Runtime')))
    $configurationHash=(Get-FileHash -LiteralPath $sourceJournalChange.Path).Hash
    $incarnation=[Guid]::Parse($ExpectedSourceJournalDatabaseIdentity.Split('|')[-1])
    $evidencePath=Join-Path $releaseRoot 'source-journal-stopped-writers.json'
    Write-SegmentedUpdateJson $evidencePath @{
        Version=1;ReleaseId=$releaseId;DatabaseIncarnation=$incarnation;RuntimeRoot=(Join-Path $CanonicalLiveRoot 'Runtime');
        RuntimeIdentityFingerprint=$identity.IdentityFingerprint;ConfigurationSha256=$configurationHash;WritersStopped=$true;
        StoppedProcessIds=@($sourceJournalRelease.StopProof.ExitedIdentities|ForEach-Object {[int]$_.ProcessId});ObservedAtUtc=[DateTimeOffset]::UtcNow
    }
    $binding=@{ReleaseId=$releaseId;ConfigurationSha256=$configurationHash;HoldSha256=(Get-FileHash -LiteralPath $ValidationHoldPath).Hash;
        RuntimeIdentityFingerprint=$identity.IdentityFingerprint;DatabaseIncarnation=$incarnation;
        OperatorEntrySha256=(Get-FileHash -LiteralPath $entry).Hash;StoppedWritersEvidencePath=$evidencePath;
        StoppedWritersEvidenceSha256=(Get-FileHash -LiteralPath $evidencePath).Hash}
    $receipt=Invoke-IncrementalSourceJournalRetirement -OperatorRoot $operatorRoot -BindingsJson ($binding|ConvertTo-Json -Depth 5) `
        -AssertQuiescent {Assert-DeploymentSourceJournalReleaseStopped}
    Write-SegmentedUpdateJson (Join-Path $releaseRoot 'source-journal-retirement.json') $receipt
    $sourceJournalRelease.RetirementConfirmed=$true
}
