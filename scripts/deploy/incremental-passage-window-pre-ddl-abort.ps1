# Typed cancellation of a failed release that never reached migration intent.
# Loaded only by the canonical incremental updater; it never changes payloads or SQL.
function Set-PassageWindowAbortPayloads {
    param([hashtable]$Binding)
    $script:ReviewedSourceJournalPayloads=@($Binding.OriginalPayloads|ForEach-Object {$_.Role+'='+$_.Sha256})
}

function Invoke-PassageWindowPreDdlAbort {
    param([hashtable]$Ports)
    &$Ports.AssertEvidence $false
    &$Ports.WriteIntent
    $started=$false;$releaseAttempted=$false
    try{
        $baseline=&$Ports.Capture
        $started=$true
        &$Ports.Start
        &$Ports.AssertEvidence $true
        &$Ports.Probe
        &$Ports.AssertUnchanged $baseline
        &$Ports.AssertEvidence $true
        $releaseAttempted=$true
        &$Ports.Release
        &$Ports.Probe
        &$Ports.Complete
    }catch{
        if($started -and !$releaseAttempted){&$Ports.Stop}
        throw
    }
}

function Invoke-DeploymentPassageWindowPreDdlAbort {
    param([string]$PriorReleaseId,[string]$ExpectedBindingSha256,[switch]$Apply,[switch]$Outlook,[switch]$CodexWorkspace,[switch]$GpuResidency,[switch]$MediaTranscription,[switch]$VerifyOnly)
    if($PriorReleaseId -cnotmatch '^\d{8}T\d{6}Z-[a-f0-9]{12}$' -or
        $ExpectedBindingSha256 -cnotmatch '^[A-Fa-f0-9]{64}$' -or !$ExpectedSourceJournalDatabaseIdentity){
        throw 'window-pre-ddl-abort-binding-required'
    }
    $ApplyOutlookWatchedSourceMigration = $Outlook
    $ApplyCodexWorkspaceNotesMigration = $CodexWorkspace
    $ApplyGpuModelResidencyMigration = $GpuResidency
    $ApplyMediaTranscriptionGpuMigration = $MediaTranscription
    $contract = Get-PassageWindowEligibilityMigrationContract -Outlook:$Outlook -CodexWorkspace:$CodexWorkspace -MediaTranscription:$MediaTranscription -GpuResidency:$GpuResidency
    $purpose = $contract.ArtifactPrefix + '-migration'
    $receiptPrefix = if ($GpuResidency) { 'gpu-residency-pre-ddl-abort' } elseif ($MediaTranscription) { 'media-transcription-pre-ddl-abort' } elseif ($CodexWorkspace) { 'codex-workspace-pre-ddl-abort' } elseif ($Outlook) { 'outlook-pre-ddl-abort' } else { 'pre-ddl-abort' }
    $prior=Join-Path $IncrementalRecoveryRoot $PriorReleaseId
    $bindingPath=Join-Path $prior ($purpose+'-release-binding.json')
    $binding=Read-SegmentedUpdateJson $bindingPath $ExpectedBindingSha256
    if($binding.Version -ne 1 -or $binding.Purpose -cne ($purpose+'-release') -or
        $binding.ReleaseId -cne $PriorReleaseId -or !$PriorReleaseId.EndsWith('-'+$binding.Commit.Substring(0,12),[StringComparison]::Ordinal) -or
        $binding.Identity -cne $ExpectedSourceJournalDatabaseIdentity -or
        $binding.Target -cne $contract.Target -or $binding.SqlSha256 -cne $contract.UpSha256 -or
        $binding.EnableCompanionAfterValidation -isnot [bool] -or (!($Outlook -or $CodexWorkspace -or $MediaTranscription -or $GpuResidency) -and $binding.EnableCompanionAfterValidation) -or
        @($binding.OriginalPayloads).Count -ne 3 -or
        (@($binding.OriginalPayloads.Role|Sort-Object) -join '|') -cne 'Cli|OutlookHost|Web'){
        throw 'window-pre-ddl-abort-release-invalid'
    }
    if(!$VerifyOnly){Set-PassageWindowAbortPayloads $binding}
    $manifest=Read-SegmentedUpdateJson (Join-Path $prior 'manifest.json')
    if($manifest.commit -cne $binding.Commit -or $manifest.migrations -cne $true -or
        $manifest.clean_slate -cne $false -or $manifest.application_root -cne $CanonicalDeployRoot -or
        $manifest[$contract.ManifestField].ReleaseBindingSha256 -cne $ExpectedBindingSha256){throw 'window-pre-ddl-abort-manifest-changed'}
    $state=@{ReleaseId=$PriorReleaseId;Identity=$binding.Identity}
    $readProof={param([bool]$Running)
        if($MediaTranscription -and @([IO.Directory]::EnumerateFileSystemEntries($prior,'media-transcription-hold-forward-intent.json')).Count){
            throw 'media-continuation-predecessor-already-claimed'
        }
        if($VerifyOnly -and @([IO.Directory]::EnumerateFileSystemEntries($prior,($receiptPrefix+'-*.json'))).Count){
            throw 'media-continuation-predecessor-cancellation-exists'
        }
        # Enumerating this validated parent proves absence without collapsing access errors.
        if(@([IO.Directory]::EnumerateFileSystemEntries($prior,($purpose+'-intent.json'))).Count){
            throw 'window-pre-ddl-abort-migration-intent-present'
        }
        $failure=Read-SegmentedUpdateJson (Join-Path $prior ($purpose+'-failure-reconciliation.json'))
        if($failure.ReleaseId -cne $PriorReleaseId -or $failure.Identity -cne $binding.Identity -or
            $failure.Attempted -cne $false -or $failure.SettledState -cne 'Old' -or $failure.HoldRetained -cne $true){throw 'window-pre-ddl-abort-not-unattempted'}
        Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $PriorReleaseId
        if(!$Running){Assert-PassageWindowEligibilityStopped $state $binding}
        elseif((Get-WebAppPoolState -Name $SiteName).Value -cne 'Started'){throw 'window-pre-ddl-abort-web-not-running'}
        $expectedRoots=@{Web=$CanonicalDeployRoot;OutlookHost=$InteractiveHostRoot;Cli=(Join-Path $SourceJournalHelperRoot 'payload')}
        foreach($payload in $binding.OriginalPayloads){
            if($payload.Root -cne $expectedRoots[$payload.Role] -or
                (Get-HybridPayloadFingerprint (Assert-SegmentedUpdatePath $payload.Root)) -cne $payload.Sha256){throw 'window-pre-ddl-abort-original-payload-changed'}
        }
        foreach($connection in @((Get-DeploymentSqlConnectionString),(Get-RepositoryRecoveryCompanionConnectionString))){
            if((Get-PassageWindowEligibilityStateKind (Get-PassageWindowEligibilityDatabaseState $connection -Outlook:$Outlook -CodexWorkspace:$CodexWorkspace -MediaTranscription:$MediaTranscription -GpuResidency:$GpuResidency) $binding.Identity -Outlook:$Outlook -CodexWorkspace:$CodexWorkspace -MediaTranscription:$MediaTranscription -GpuResidency:$GpuResidency) -cne 'Old'){
                throw 'typed-pre-ddl-abort-requires-exact-baseline'
            }
        }
        foreach($pair in @(@{Path=(Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json');Hash=$binding.ConfigurationSha256},
            @{Path=(Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json');Hash=$binding.CompanionConfigurationSha256})){
            if((Get-FileHash -LiteralPath (Assert-SegmentedUpdatePath $pair.Path)).Hash -cne $pair.Hash){throw 'window-pre-ddl-abort-configuration-changed'}
        }
        $task=Get-RepositoryRecoveryTaskObservation
        if($task.Enabled -or $task.State -ceq 'Running' -or $task.IdentityHash -cne $binding.TaskIdentityHash -or
            $task.LauncherSha256 -cne $binding.LauncherSha256){throw 'window-pre-ddl-abort-companion-changed'}
        Wait-InteractiveHostStopped -TaskName $InteractiveHostTaskName -TimeoutSeconds $ReadinessTimeoutSeconds
        Assert-DeploymentSourceJournalCliWritersStopped
        $checkpointPath = Join-Path $SourceJournalHelperRoot ('updates/'+$PriorReleaseId+'/state.json')
        $checkpoint=Read-SegmentedUpdateJson $checkpointPath
        $helperUnchanged = $checkpoint.Phase -ceq 'RolledBack' -or (($Outlook -or $CodexWorkspace -or $MediaTranscription -or $GpuResidency) -and $checkpoint.Phase -ceq 'Prepared')
        if(!$helperUnchanged -or $checkpoint.ReleaseId -cne $PriorReleaseId -or
            (Get-SourceJournalHelperPayloadManifest (Join-Path $SourceJournalHelperRoot 'payload')).Sha256 -cne $checkpoint.OriginalPayloadSha256){
            throw 'window-pre-ddl-abort-helper-rollback-unproved'
        }
        $helper=Get-SourceJournalHelperTaskObservation -InstallRoot $SourceJournalHelperRoot -TaskName 'FluxKnowledge.SourceAccess'
        foreach($field in @('TaskName','PrincipalSid','Executable','Arguments','WorkingDirectory','DefinitionSha256','SecuritySha256','Enabled','Running')){
            if($helper[$field] -cne $binding.HelperTask[$field] -or $checkpoint.Task[$field] -cne $binding.HelperTask[$field]){
                throw 'window-pre-ddl-abort-helper-task-changed'
            }
        }
        foreach($pair in @(@{File='policy.json';Hash=$binding.HelperRepairPolicySha256},
            @{File='root-read-plan.json';Hash=$binding.HelperRootReadPlanSha256},@{File='journal-policy.json';Hash=$binding.HelperJournalPolicySha256})){
            $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]](Read-SourceJournalHelperFile (Join-Path $SourceJournalHelperRoot $pair.File))))
            if($hash -cne $pair.Hash){throw 'window-pre-ddl-abort-helper-policy-changed'}
        }
    }
    $mutex=[Threading.Mutex]::new($false,'Global\FluxKnowledge.IncrementalIisUpdate.v1');$owned=$false
    try{
        try{$owned=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owned=$true}
        if(!$owned){throw 'window-pre-ddl-abort-update-already-running'}
        &$readProof $false
        if($VerifyOnly){return $binding}
        if(!$Apply){
            @{ok=$true;mode='plan-only';release=$PriorReleaseId;baseline=$contract.Baseline;
                action='start unchanged Web under owned hold; validate then release';
                companionEnabled=$binding.EnableCompanionAfterValidation;sql='none';payloadChanges='none'}|ConvertTo-Json
            return
        }
        $ports=@{
            AssertEvidence=$readProof
            WriteIntent={Write-PassageWindowEligibilityRecord (Join-Path $prior ($receiptPrefix+'-intent.json')) @{ReleaseId=$PriorReleaseId;BindingSha256=$ExpectedBindingSha256;RecordedUtc=[DateTimeOffset]::UtcNow}}
            Capture={Get-RetainedPipelineStateBaseline}
            Start={Start-IndexCompatibleWebAppPool -Name $SiteName -RequiredHoldReleaseId $PriorReleaseId;Wait-IisAppPoolState -Name $SiteName -ExpectedState 'Started' -TimeoutSeconds $ReadinessTimeoutSeconds}
            Stop={Stop-HybridIisAfterGpuDrain}
            Probe={Invoke-RequiredLoopbackProbes -Origin 'http://127.0.0.1:5137' -TimeoutSeconds $ReadinessTimeoutSeconds}
            AssertUnchanged={param($Baseline) Assert-RetainedPipelineStateUnchanged -Baseline $Baseline -Current (Get-RetainedPipelineStateBaseline)}
            Release={$state.ReleasedHold=Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $PriorReleaseId -RequireOwned}
            Complete={
                if ($binding.EnableCompanionAfterValidation) {
                    Enable-IndexCompatibleOutlookTask -TaskName $InteractiveHostTaskName -ExpectedReleaseId $PriorReleaseId -ReleasedHold $state.ReleasedHold | Out-Null
                    if (!(Get-ScheduledTask -TaskName $InteractiveHostTaskName -ErrorAction Stop).Settings.Enabled) {
                        throw 'typed-pre-ddl-abort-companion-enablement-unconfirmed'
                    }
                }
                Write-PassageWindowEligibilityRecord (Join-Path $prior ($receiptPrefix+'-completed.json')) @{
                    ReleaseId=$PriorReleaseId;OriginalSchema=$contract.PriorHistoryCount;CompletedUtc=[DateTimeOffset]::UtcNow
                }
                @{ok=$true;mode='pre-ddl-abort-completed';release=$PriorReleaseId}|ConvertTo-Json
            }
        }
        Invoke-PassageWindowPreDdlAbort $ports
    }finally{if($owned){$mutex.ReleaseMutex()};$mutex.Dispose()}
}
