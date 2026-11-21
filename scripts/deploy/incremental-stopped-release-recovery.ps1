# An ordinary failed release can leave a plain deny-all hold and compatible
# payloads installed. Adoption is separate from application activation and never
# removes that hold between owners. The caller holds the incremental-update mutex.
function Assert-IncrementalStoppedRecoveryMode {
    param([hashtable]$Binding,[bool]$VectorMetadataMigration)
    if($VectorMetadataMigration){
        if($Binding.Version -ne 2 -or $Binding.Purpose -cne 'vector-publication-metadata-resume'){throw 'vector-metadata-resume-requires-typed-binding'}
    } elseif($Binding.Version -ne 1 -or $Binding.Purpose -cne 'incremental-stopped-release'){
        throw 'stopped-release-migration-binding-requires-explicit-mode'
    }
}

function Assert-IncrementalVectorMetadataResumeEvidence {
    param([hashtable]$Binding,[hashtable]$Manifest,[hashtable]$ReleaseBinding,[hashtable]$Intent)
    Assert-IncrementalStoppedRecoveryMode $Binding $true
    $contract=Get-VectorPublicationMetadataMigrationContract
    if($Manifest.migrations -cne $true -or $Manifest.clean_slate -cne $false -or
        $Manifest.commit -cne $Binding.PriorCommit -or
        !$Manifest.ContainsKey('vector_publication_metadata')){throw 'vector-metadata-resume-manifest-invalid'}
    $migration=$Manifest.vector_publication_metadata
    if($migration.Target -cne $contract.Target -or $migration.SqlSha256 -cne $contract.UpSha256 -or
        $migration.ReleaseBindingSha256 -cne $Binding.MigrationReleaseBindingSha256){throw 'vector-metadata-resume-manifest-binding-changed'}
    $hasIntent=![string]::IsNullOrEmpty($Binding.MigrationIntentSha256)
    if($ReleaseBinding.Version -ne 1 -or $ReleaseBinding.Purpose -cne 'vector-publication-metadata-release' -or
        $ReleaseBinding.InitialKind -cnotin @('Old','New')){throw 'vector-metadata-resume-release-invalid'}
    $records=@($ReleaseBinding)
    if($hasIntent){
        if($null -eq $Intent -or $Intent.Version -ne 1 -or $Intent.Purpose -cne 'vector-publication-metadata-migration' -or
            $Intent.Attempted -cne $true -or $Intent.InitialKind -cne $ReleaseBinding.InitialKind){throw 'vector-metadata-resume-intent-invalid'}
        $records+=,$Intent
    } elseif($null -ne $Intent){throw 'vector-metadata-unbound-intent-refused'}
    foreach($record in $records){
        if($record.ReleaseId -cne $Binding.PriorReleaseId -or $record.Commit -cne $Binding.PriorCommit -or
            $record.Identity -cne $Binding.DatabaseIdentity -or $record.Target -cne $contract.Target -or
            $record.SqlSha256 -cne $contract.UpSha256){throw 'vector-metadata-resume-attempt-binding-changed'}
    }
    foreach($key in @('ConfigurationSha256','CompanionConfigurationSha256','TaskIdentityHash','LauncherSha256','EnableCompanionAfterValidation')){
        if($ReleaseBinding[$key] -cne $Binding[$key]){throw ('vector-metadata-resume-release-binding-changed: '+$key)}
    }
    foreach($set in @('OriginalPayloads','CandidatePayloads')){
        if(@($ReleaseBinding[$set]).Count -ne 3 -or (@($ReleaseBinding[$set].Role|Sort-Object) -join '|') -cne 'Cli|OutlookHost|Web'){
            throw 'vector-metadata-resume-release-payload-set-invalid'
        }
    }
    foreach($installed in $Binding.Payloads){
        $original=@($ReleaseBinding.OriginalPayloads|Where-Object Role -CEQ $installed.Role)[0]
        $candidate=@($ReleaseBinding.CandidatePayloads|Where-Object Role -CEQ $installed.Role)[0]
        foreach($known in @($original,$candidate)){
            if($known.Root -cne $installed.Root -or $known.Sha256 -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'vector-metadata-resume-release-payload-invalid'}
        }
        if($installed.Sha256 -cnotin @($original.Sha256,$candidate.Sha256)){throw 'vector-metadata-resume-installed-payload-unrecognised'}
        if(!$hasIntent -and $installed.Sha256 -cne $original.Sha256){throw 'vector-metadata-pre-intent-original-payload-required'}
    }
}

function Assert-IncrementalVectorMetadataIntentAbsent {
    param([string]$PriorReleaseRoot)
    # Enumerating the exact parent proves absence without File.Exists/Test-Path
    # treating access errors or dangling links as a missing intent.
    for($path=$PriorReleaseRoot;$path;$path=[IO.Path]::GetDirectoryName($path)){
        if([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint){throw 'vector-metadata-intent-parent-linked'}
    }
    if(@([IO.Directory]::EnumerateFileSystemEntries($PriorReleaseRoot,'vector-publication-metadata-migration-intent.json')).Count){
        throw 'vector-metadata-unbound-intent-present'
    }
}

function Read-IncrementalStoppedRecoveryBinding {
    param([string]$Path,[string]$ExpectedSha256)
    if($ExpectedSha256 -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'stopped-release-binding-hash-required'}
    $binding=Read-SegmentedUpdateJson $Path $ExpectedSha256
    $vector=$binding.Version -eq 2 -and $binding.Purpose -ceq 'vector-publication-metadata-resume'
    if((!$vector -and ($binding.Version -ne 1 -or $binding.Purpose -cne 'incremental-stopped-release')) -or
        $binding.PriorReleaseId -cnotmatch '^\d{8}T\d{6}Z-[a-f0-9]{12}$' -or
        $binding.PriorCommit -cnotmatch '^[a-f0-9]{40}$' -or
        !$binding.PriorReleaseId.EndsWith('-'+$binding.PriorCommit.Substring(0,12),[StringComparison]::Ordinal) -or
        $binding.EnableCompanionAfterValidation -isnot [bool] -or !$binding.DatabaseIdentity){throw 'stopped-release-binding-invalid'}
    foreach($name in @('PriorManifestSha256','HoldSha256','ConfigurationSha256','CompanionConfigurationSha256','TaskIdentityHash','LauncherSha256')) {
        if($binding[$name] -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'stopped-release-binding-incomplete'}
    }
    if($vector){
        if(!$binding.ContainsKey('MigrationIntentSha256') -or $binding.MigrationIntentSha256 -isnot [string] -or
            ($binding.MigrationIntentSha256 -and $binding.MigrationIntentSha256 -cnotmatch '^[A-Fa-f0-9]{64}$') -or
            $binding.MigrationReleaseBindingSha256 -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'vector-metadata-resume-receipt-binding-incomplete'}
    }
    $roots=@{Web=$CanonicalDeployRoot;OutlookHost=$InteractiveHostRoot;Cli=(Join-Path $SourceJournalHelperRoot 'payload')}
    if(@($binding.Payloads).Count -ne 3 -or (@($binding.Payloads.Role|Sort-Object) -join '|') -cne 'Cli|OutlookHost|Web') {
        throw 'stopped-release-payload-set-invalid'
    }
    foreach($payload in $binding.Payloads) {
        if($payload.Root -cne $roots[$payload.Role] -or $payload.Sha256 -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'stopped-release-payload-binding-invalid'}
    }
    $binding.ReceiptPath=[IO.Path]::GetFullPath($Path)
    $binding.ReceiptSha256=$ExpectedSha256
    return $binding
}

function Assert-IncrementalStoppedRecoveryBinding {
    param([hashtable]$Binding)
    [void](Read-SegmentedUpdateJson $Binding.ReceiptPath $Binding.ReceiptSha256)
    Assert-StoppedPoolRecoveryBaseline
    $task=Get-RepositoryRecoveryTaskObservation
    if($task.Enabled -or $task.IdentityHash -cne $Binding.TaskIdentityHash -or $task.LauncherSha256 -cne $Binding.LauncherSha256) {
        throw 'stopped-release-task-or-launcher-changed'
    }
    Wait-InteractiveHostStopped -TaskName $InteractiveHostTaskName -TimeoutSeconds 1
    $launcher=[regex]::Escape('E:\Codex Workspaces\Scripts\Outlook\run-outlook-hidden.vbs')
    $hostScript=[regex]::Escape((Join-Path $InteractiveHostRoot 'run-outlook-host.ps1'))
    $owners=@(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        $_.Name -ieq 'FluxKnowledge.OutlookHost.exe' -or
        ($_.Name -ieq 'wscript.exe' -and $_.CommandLine -imatch $launcher) -or
        ($_.Name -in @('pwsh.exe','powershell.exe') -and $_.CommandLine -imatch $hostScript) -or
        ($_.Name -ieq 'dotnet.exe' -and $_.CommandLine -imatch 'FluxKnowledge\.OutlookHost\.dll')
    })
    if($owners.Count){throw 'stopped-release-companion-exit-unproved'}
    if((Get-FileHash -LiteralPath (Assert-SegmentedUpdatePath $ValidationHoldPath)).Hash -cne $Binding.HoldSha256 -or
        (Read-SegmentedUpdateJson $ValidationHoldPath) -cne $Binding.PriorReleaseId){throw 'stopped-release-hold-changed'}
    $prior=Join-Path $IncrementalRecoveryRoot $Binding.PriorReleaseId
    $manifest=Read-SegmentedUpdateJson (Join-Path $prior 'manifest.json') $Binding.PriorManifestSha256
    $vector=$Binding.Version -eq 2 -and $Binding.Purpose -ceq 'vector-publication-metadata-resume'
    if($manifest.commit -cne $Binding.PriorCommit -or $manifest.application_root -cne $CanonicalDeployRoot -or
        $manifest.migrations -isnot [bool] -or (!$vector -and $manifest.migrations) -or
        $manifest.clean_slate -isnot [bool] -or $manifest.clean_slate){throw 'stopped-release-prior-manifest-invalid'}
    $releaseBinding=$null
    if($vector){
        $releaseBinding=Read-SegmentedUpdateJson (Join-Path $prior 'vector-publication-metadata-release-binding.json') $Binding.MigrationReleaseBindingSha256
        $intent=$null
        if($Binding.MigrationIntentSha256){$intent=Read-SegmentedUpdateJson (Join-Path $prior 'vector-publication-metadata-migration-intent.json') $Binding.MigrationIntentSha256}
        else {Assert-IncrementalVectorMetadataIntentAbsent $prior}
        Assert-IncrementalVectorMetadataResumeEvidence $Binding $manifest $releaseBinding $intent
    }
    $Binding.SourceAccessHelperRefresh=Read-SourceAccessHelperRefreshRecovery $Binding $manifest
    $Binding.SourceAccessHelperRefreshReference=if($Binding.SourceAccessHelperRefresh){$manifest.source_access_helper_refresh}else{$null}
    foreach($payload in $Binding.Payloads) {
        [void](Assert-SegmentedUpdatePath $payload.Root)
        if((Get-HybridPayloadFingerprint $payload.Root) -cne $payload.Sha256){throw 'stopped-release-installed-payload-changed'}
        $candidate=if($payload.Role -ceq 'Web'){Join-Path $prior 'candidate'}elseif($payload.Role -ceq 'OutlookHost'){Join-Path $prior 'candidate-interactive-host'}else{$null}
        $candidateHash=if($vector){@($releaseBinding.CandidatePayloads|Where-Object Role -CEQ $payload.Role)[0].Sha256}else{$payload.Sha256}
        if($candidate -and (Get-HybridPayloadFingerprint (Assert-SegmentedUpdatePath $candidate)) -cne $candidateHash){throw 'stopped-release-prior-candidate-changed'}
    }
    foreach($config in @(@{Path=(Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json');Hash=$Binding.ConfigurationSha256},
        @{Path=(Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json');Hash=$Binding.CompanionConfigurationSha256})) {
        if((Get-FileHash -LiteralPath (Assert-SegmentedUpdatePath $config.Path)).Hash -cne $config.Hash){throw 'stopped-release-configuration-changed'}
    }
    foreach($connection in @((Get-DeploymentSqlConnectionString),(Get-RepositoryRecoveryCompanionConnectionString))) {
        if((Get-SourceJournalDatabaseState -ConnectionString $connection).Identity -cne $Binding.DatabaseIdentity){throw 'stopped-release-database-changed'}
        if($vector){
            $kind=Get-VectorPublicationMetadataStateKind (Get-VectorPublicationMetadataDatabaseState -ConnectionString $connection) $Binding.DatabaseIdentity
            if(!$Binding.MigrationIntentSha256 -and $kind -cne $releaseBinding.InitialKind){throw 'vector-metadata-pre-intent-schema-changed'}
        }
    }
}

function Read-SourceAccessHelperRefreshRecovery($RecoveryBinding,$Manifest) {
    if(!$Manifest.Contains('source_access_helper_refresh') -or !$Manifest.source_access_helper_refresh){return $null}
    $reference=$Manifest.source_access_helper_refresh
    if($reference -isnot [Collections.IDictionary] -or
       $reference.ReleaseId -cnotmatch '^\d{8}T\d{6}Z-[a-f0-9]{12}$' -or
       $reference.BindingSha256 -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'helper-refresh-recovery-manifest-invalid'}
    $prior=Join-Path $IncrementalRecoveryRoot $reference.ReleaseId
    $path=Join-Path $prior 'source-access-helper-refresh-binding.json'
    $refresh=Read-SegmentedUpdateJson $path $reference.BindingSha256
    if($refresh.ReleaseId -cne $reference.ReleaseId -or $refresh.Commit -cne $RecoveryBinding.PriorCommit -or
       $refresh.DatabaseIdentity -cne $RecoveryBinding.DatabaseIdentity -or
       $refresh.ConfigurationSha256 -cne $RecoveryBinding.ConfigurationSha256 -or
       $refresh.Companion.IdentityHash -cne $RecoveryBinding.TaskIdentityHash -or
       $refresh.Companion.LauncherSha256 -cne $RecoveryBinding.LauncherSha256 -or
       $refresh.Companion.Enabled -cne $RecoveryBinding.EnableCompanionAfterValidation){
        throw 'helper-refresh-recovery-binding-drift'
    }
    foreach($payload in $RecoveryBinding.Payloads){
        if(($payload.Role + '=' + $payload.Sha256) -inotIn $refresh.CandidatePayloads){
            throw 'helper-refresh-recovery-requires-complete-successor'
        }
    }
    Assert-SourceAccessHelperRefreshRecoveryInstalled $refresh
    return $refresh
}

function Assert-SourceAccessHelperRefreshRecoveryInstalled($Refresh) {
    $checkpoint=Join-Path $SourceJournalHelperRoot ('updates/'+$Refresh.ReleaseId+'/state.json')
    if($checkpoint -ine $Refresh.HelperCheckpointPath){throw 'helper-refresh-recovery-checkpoint-path-changed'}
    $state=ConvertFrom-UniqueConfigurationJson (Read-SourceJournalHelperFile $checkpoint 65536)
    if($state.Phase -cne 'Completed' -or $state.ReleaseId -cne $Refresh.ReleaseId -or
       $state.CandidatePayloadSha256 -cne (Get-SourceJournalHelperPayloadManifest (Join-Path $SourceJournalHelperRoot 'payload')).Sha256){
        throw 'helper-refresh-recovery-checkpoint-unsettled'
    }
    foreach($entry in @(@('policy.json','RepairPolicySha256'),@('journal-policy.json','JournalPolicySha256'),@('root-read-plan.json','RootReadPlanSha256'))){
        $bytes=Read-SourceJournalHelperFile (Join-Path $SourceJournalHelperRoot $entry[0])
        if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -cne $Refresh[$entry[1]] -or
           $state[$entry[1]] -cne $Refresh[$entry[1]]){throw 'helper-refresh-recovery-policy-drift'}
    }
    $task=Get-SourceJournalHelperTaskObservation -InstallRoot $SourceJournalHelperRoot -TaskName 'FluxKnowledge.SourceAccess'
    foreach($key in @('TaskName','PrincipalSid','Executable','Arguments','WorkingDirectory','DefinitionSha256','SecuritySha256','Enabled','Running')){
        if($task[$key] -cne $Refresh.HelperTask[$key]){throw 'helper-refresh-recovery-task-drift'}
    }
    foreach($payload in Get-DeploymentSourceJournalPayloads){
        $fingerprint=Get-HybridPayloadFingerprint $payload.Root
        if(($payload.Role + '=' + $fingerprint) -inotIn $Refresh.CandidatePayloads){throw 'helper-refresh-recovery-payload-drift'}
    }
}

function Confirm-SourceAccessHelperRefreshRecoveryTransport($Refresh) {
    Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $releaseId
    Assert-SourceAccessHelperRefreshRecoveryInstalled $Refresh
    $app=ConvertFrom-UniqueConfigurationJson (Get-SourceAccessConfigurationBytes (Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'))
    $policy=ConvertFrom-UniqueConfigurationJson (Read-SourceJournalHelperFile (Join-Path $SourceJournalHelperRoot 'policy.json'))
    $response=Invoke-RestMethod -Uri ($loopbackOrigin.Origin+'/health/source-journal-helper') -Method Get -TimeoutSec 12
    $volumes=[Collections.Generic.HashSet[string]]::new([string[]]$app.SourceJournal.ApprovedVolumes,[StringComparer]::OrdinalIgnoreCase)
    if($response.Version -ne 1 -or $response.VolumesChecked -ne $volumes.Count -or
        @($response.ApprovedVolumes).Count -ne $volumes.Count -or !$volumes.SetEquals([string[]]$response.ApprovedVolumes) -or
        $response.PipeName -cne $app.SourceJournal.PipeName -or $response.RepairTransport -cne 'NotApproved' -or
        $response.CallerSid -cne $policy.IndexerSid){throw 'helper-refresh-recovery-held-transport-unconfirmed'}
    Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $releaseId
    Assert-SourceAccessHelperRefreshRecoveryInstalled $Refresh
    Write-SegmentedUpdateJson (Join-Path $releaseRoot 'source-access-helper-refresh-recovery-transport.json') @{
        ReleaseId=$releaseId;SourceReleaseId=$Refresh.ReleaseId;ObservedAtUtc=[DateTimeOffset]::UtcNow
        JournalVolumesChecked=$response.VolumesChecked;RepairTransport=$response.RepairTransport
    }
}

function Move-IncrementalStoppedRecoveryHold {
    param([hashtable]$Binding,[string]$ReleaseId,[string]$StatePath)
    if($ReleaseId -cnotmatch '^\d{8}T\d{6}Z-[a-f0-9]{12}$' -or $ReleaseId -ceq $Binding.PriorReleaseId){throw 'stopped-release-new-owner-invalid'}
    if(Test-Path -LiteralPath $StatePath){throw 'stopped-release-transfer-reconciliation-required'}
    Assert-IncrementalStoppedRecoveryBinding $Binding
    if($Binding.SourceAccessHelperRefreshReference){
        $manifestPath=Join-Path (Split-Path -Parent $StatePath) 'manifest.json'
        if(Test-Path -LiteralPath $manifestPath){throw 'helper-refresh-recovery-adoption-manifest-occupied'}
        Write-SegmentedUpdateJson $manifestPath @{
            commit=$Binding.PriorCommit;application_root=$CanonicalDeployRoot;migrations=$false;clean_slate=$false
            source_access_helper_refresh=$Binding.SourceAccessHelperRefreshReference
        }
    }
    Write-IncrementalStoppedRecoveryHoldTransfer -Binding $Binding -ReleaseId $ReleaseId -StatePath $StatePath
}

# The caller has proved the stopped predecessor under the incremental deployment mutex.
function Write-IncrementalStoppedRecoveryHoldTransfer {
    param([hashtable]$Binding,[string]$ReleaseId,[string]$StatePath)
    if($ReleaseId -cnotmatch '^\d{8}T\d{6}Z-[a-f0-9]{12}$' -or $ReleaseId -ceq $Binding.PriorReleaseId){throw 'stopped-release-new-owner-invalid'}
    if(Test-Path -LiteralPath $StatePath){throw 'stopped-release-transfer-reconciliation-required'}
    Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $Binding.PriorReleaseId
    $state=@{Version=1;Purpose='incremental-stopped-release-transfer';BindingSha256=$Binding.ReceiptSha256;
        PriorReleaseId=$Binding.PriorReleaseId;ReleaseId=$ReleaseId;HoldTransferIntentUtc=[datetime]::UtcNow.ToString('O');HoldTransferred=$false}
    Write-SegmentedUpdateJson $StatePath $state
    # Same-directory atomic replacement preserves deny-all admission continuously.
    Write-SegmentedUpdateJson $ValidationHoldPath $ReleaseId
    if((Read-SegmentedUpdateJson $ValidationHoldPath) -cne $ReleaseId){throw 'stopped-release-transfer-unconfirmed'}
    $state.HoldTransferred=$true
    Write-SegmentedUpdateJson $StatePath $state
}
