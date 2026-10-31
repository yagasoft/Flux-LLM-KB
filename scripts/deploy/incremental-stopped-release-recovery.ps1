# An ordinary failed release can leave a plain deny-all hold and compatible
# payloads installed. Adoption is separate from application activation and never
# removes that hold between owners. The caller holds the incremental-update mutex.
function Read-IncrementalStoppedRecoveryBinding {
    param([string]$Path,[string]$ExpectedSha256)
    if($ExpectedSha256 -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'stopped-release-binding-hash-required'}
    $binding=Read-SegmentedUpdateJson $Path $ExpectedSha256
    if($binding.Version -ne 1 -or $binding.Purpose -cne 'incremental-stopped-release' -or
        $binding.PriorReleaseId -cnotmatch '^\d{8}T\d{6}Z-[a-f0-9]{12}$' -or
        $binding.PriorCommit -cnotmatch '^[a-f0-9]{40}$' -or
        !$binding.PriorReleaseId.EndsWith('-'+$binding.PriorCommit.Substring(0,12),[StringComparison]::Ordinal) -or
        $binding.EnableCompanionAfterValidation -isnot [bool] -or !$binding.DatabaseIdentity){throw 'stopped-release-binding-invalid'}
    foreach($name in @('PriorManifestSha256','HoldSha256','ConfigurationSha256','CompanionConfigurationSha256','TaskIdentityHash','LauncherSha256')) {
        if($binding[$name] -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'stopped-release-binding-incomplete'}
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
    if($manifest.commit -cne $Binding.PriorCommit -or $manifest.application_root -cne $CanonicalDeployRoot -or
        $manifest.migrations -isnot [bool] -or $manifest.migrations -or
        $manifest.clean_slate -isnot [bool] -or $manifest.clean_slate){throw 'stopped-release-prior-manifest-invalid'}
    foreach($payload in $Binding.Payloads) {
        [void](Assert-SegmentedUpdatePath $payload.Root)
        if((Get-HybridPayloadFingerprint $payload.Root) -cne $payload.Sha256){throw 'stopped-release-installed-payload-changed'}
        $candidate=if($payload.Role -ceq 'Web'){Join-Path $prior 'candidate'}elseif($payload.Role -ceq 'OutlookHost'){Join-Path $prior 'candidate-interactive-host'}else{$null}
        if($candidate -and (Get-HybridPayloadFingerprint (Assert-SegmentedUpdatePath $candidate)) -cne $payload.Sha256){throw 'stopped-release-prior-candidate-changed'}
    }
    foreach($config in @(@{Path=(Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json');Hash=$Binding.ConfigurationSha256},
        @{Path=(Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json');Hash=$Binding.CompanionConfigurationSha256})) {
        if((Get-FileHash -LiteralPath (Assert-SegmentedUpdatePath $config.Path)).Hash -cne $config.Hash){throw 'stopped-release-configuration-changed'}
    }
    foreach($connection in @((Get-DeploymentSqlConnectionString),(Get-RepositoryRecoveryCompanionConnectionString))) {
        if((Get-SourceJournalDatabaseState -ConnectionString $connection).Identity -cne $Binding.DatabaseIdentity){throw 'stopped-release-database-changed'}
    }
}

function Move-IncrementalStoppedRecoveryHold {
    param([hashtable]$Binding,[string]$ReleaseId,[string]$StatePath)
    if($ReleaseId -cnotmatch '^\d{8}T\d{6}Z-[a-f0-9]{12}$' -or $ReleaseId -ceq $Binding.PriorReleaseId){throw 'stopped-release-new-owner-invalid'}
    if(Test-Path -LiteralPath $StatePath){throw 'stopped-release-transfer-reconciliation-required'}
    Assert-IncrementalStoppedRecoveryBinding $Binding
    $state=@{Version=1;Purpose='incremental-stopped-release-transfer';BindingSha256=$Binding.ReceiptSha256;
        PriorReleaseId=$Binding.PriorReleaseId;ReleaseId=$ReleaseId;HoldTransferIntentUtc=[datetime]::UtcNow.ToString('O');HoldTransferred=$false}
    Write-SegmentedUpdateJson $StatePath $state
    # Same-directory atomic replacement preserves deny-all admission continuously.
    Write-SegmentedUpdateJson $ValidationHoldPath $ReleaseId
    if((Read-SegmentedUpdateJson $ValidationHoldPath) -cne $ReleaseId){throw 'stopped-release-transfer-unconfirmed'}
    $state.HoldTransferred=$true
    Write-SegmentedUpdateJson $StatePath $state
}
