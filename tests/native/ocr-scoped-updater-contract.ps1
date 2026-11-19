param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
$area=Join-Path ([IO.Path]::GetTempPath()) ('FluxOcrScope_'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($area)

function Invoke-ScopedUpdaterContract {
    # The canonical updater dot-sources its helpers in its own script scope.
    # Keep them out of global scope so dynamic-module closures cannot find them.
    . (Join-Path $SourceRoot 'scripts/deploy/incremental-ocr-lifecycle-recovery.ps1')
    Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
    $update=(Get-Item Function:Invoke-OcrLifecycleRecoveryUpdate).ScriptBlock.ToString()
    $productionMutex="'Global\FluxKnowledge.IncrementalIisUpdate.v1'"
    if(!$update.Contains($productionMutex)){throw 'Fixture mutex substitution no longer matches.'}
    Set-Item Function:Invoke-OcrLifecycleRecoveryUpdate ([scriptblock]::Create(
        $update.Replace($productionMutex,"'Local\FluxKnowledge.OcrScopeFixture.$([Guid]::NewGuid().ToString('N'))'")))

    $CanonicalLiveRoot=Join-Path $area 'Live'
    $CanonicalDeployRoot=Join-Path $area 'App'
    $InteractiveHostRoot=Join-Path $area 'Host/payload'
    $IncrementalRecoveryRoot=Join-Path $area 'Recovery'
    $ValidationHoldPath=Join-Path $area 'hold.json'
    $SiteName='SyntheticPool';$InteractiveHostTaskName='SyntheticTask'
    $box=@{Preflights=0;Candidates=0;DatabaseReads=0;Holds=0;Stops=0;Restores=0}
    function Assert-IncrementalIisPreflight {$box.Preflights++}
    function Assert-RepositoryRecoveryCandidateCommit {param($Root,$Commit) $box.Candidates++}
    function Get-RepositoryRecoveryTaskObservation {@{IdentityHash=('A'*64);LauncherSha256=('B'*64)}}
    function Get-DeploymentSqlConnectionString {'synthetic-no-database'}
    function Get-OcrLifecycleDatabaseState {param($ConnectionString)
        if($ConnectionString -cne 'synthetic-no-database'){throw 'Unexpected database binding.'}
        $box.DatabaseReads++;throw 'fixture-controlled-database-read'
    }
    $payloads=@(foreach($role in @('Web','OutlookHost')){
        $path=if($role -ceq 'Web'){$CanonicalDeployRoot}else{$InteractiveHostRoot}
        [void][IO.Directory]::CreateDirectory($path)
        [IO.File]::WriteAllText((Join-Path $path 'synthetic.dll'),'synthetic payload '+$role)
        @{Role=$role;Root=$path;Sha256=(Get-HybridPayloadFingerprint $path)}
    })
    $configuration=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
    $companionConfiguration=Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json'
    foreach($path in @($configuration,$companionConfiguration)){
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::WriteAllText($path,'synthetic config')
    }
    $plan=@{Version=1;Binding=@{Handle=@{BatchId=[Guid]::NewGuid()}};OperationRoot=$area;
        ReleaseId='20260101T000000Z-aaaaaaaaaaaa-ocrlifecycle';Commit=('a'*40);
        HoldPath=$ValidationHoldPath;IisPool=$SiteName;TaskName=$InteractiveHostTaskName;
        ConfigurationPath=$configuration;ConfigurationSha256=(Get-FileHash $configuration).Hash;
        CompanionConfigurationPath=$companionConfiguration;CompanionConfigurationSha256=(Get-FileHash $companionConfiguration).Hash;
        ImplementationSha256=(Get-OcrRecoveryImplementationHash);Payloads=$payloads;
        OriginalWebSha256=$payloads[0].Sha256;OriginalCompanionSha256=$payloads[1].Sha256;
        Task=@{IdentityHash=('A'*64);LauncherSha256=('B'*64)}}
    $planPath=Join-Path $area 'plan.json';Write-OcrRecoveryJson $planPath $plan
    $reason=''
    try{Invoke-OcrLifecycleRecoveryUpdate $planPath (Get-FileHash $planPath).Hash|Out-Null}
    catch{$reason=$_.Exception.Message}
    if($reason -cne 'fixture-controlled-database-read' -or $box.Preflights -ne 1 -or $box.Candidates -ne 2 -or $box.DatabaseReads -ne 1){
        throw "Scoped real updater failed before its controlled preflight: reason=$reason preflights=$($box.Preflights) candidates=$($box.Candidates) reads=$($box.DatabaseReads)"
    }
    $statePath=Join-Path $area 'deployment-state.json'
    if([IO.File]::Exists($statePath) -or [IO.File]::Exists($ValidationHoldPath) -or [IO.Directory]::Exists($IncrementalRecoveryRoot)){
        throw 'Refused scoped preflight mutated deployment state.'
    }

    # Exercise the real state writer from the same non-global helper scope.
    $ports=@{AssertBindings={};EnsureHold={
        $receipt=Get-Content -Raw -LiteralPath $statePath|ConvertFrom-Json
        if($receipt.Phase -cne $(if($box.Holds -eq 0){'HoldIntent'}else{'StopIntent'})){throw 'Hold intent was not durable before the port ran.'}
        $box.Holds++
    };StopOwners={
        $box.Stops++
        if($box.Stops -eq 1){throw 'fixture-controlled-stop-failure'}
    };RestorePriorPayloads={$box.Restores++}}
    $reason=''
    try{Invoke-OcrRecoveryFlow $statePath ('C'*64) $ports|Out-Null}catch{$reason=$_.Exception.Message}
    if($reason -notlike '*failed-held: StopIntent*'){throw "Scoped receipt/containment failed: $reason"}
    $failed=Get-Content -Raw -LiteralPath $statePath|ConvertFrom-Json
    if($failed.Phase -cne 'FailedHeld' -or $failed.FailedAt -cne 'StopIntent' -or $failed.Completed -or
        $failed.ActivationStarted -or $failed.ContainmentIncomplete -or $box.Holds -ne 2 -or $box.Stops -ne 2 -or $box.Restores -ne 1){
        throw 'Scoped state receipt or pre-activation containment was incorrect.'
    }
}

try{
    Invoke-ScopedUpdaterContract
    'Scoped real updater preflight and durable failure containment passed; no production calls.'
}finally{
    $resolved=[IO.Path]::GetFullPath($area)
    if(!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or
        ![IO.Path]::GetFileName($resolved).StartsWith('FluxOcrScope_')){throw 'Unsafe scoped fixture cleanup.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
