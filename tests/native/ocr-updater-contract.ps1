param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'scripts/deploy/incremental-ocr-lifecycle-recovery.ps1')
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-update.psm1') -Force
function Assert-Refused([scriptblock]$Action,[string]$Reason) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message.Contains($Reason)) { return }
        throw
    }
    throw "Expected refusal: $Reason"
}
# Load the real preflight functions and dispatch statement, without running the
# updater's top-level imports, canonical production checks or operational path.
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Updater parse failed.' }
foreach ($name in @('Assert-IncrementalIisPreflight','Assert-CanonicalPath','Assert-NotReparsePoint','Assert-RepositoryRecoveryCandidateCommit')) {
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true)
    . ([scriptblock]::Create($node.Extent.Text))
}
$dispatch=$ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -ceq '$ApplyOcrLifecycleRecovery' }
if (@($dispatch).Count -ne 1) { throw 'OCR dispatch not uniquely found.' }
$dispatchText=$dispatch.Extent.Text -replace '(?m)^\s*Import-Module WebAdministration[^\r\n]*','' -replace '(?m)^\s*\. \(Join-Path \$PSScriptRoot ''incremental-ocr-lifecycle-recovery.ps1''\)[^\r\n]*',''
$root=Join-Path $SourceRoot ('.agents/ocr-updater-contract_'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try {
    $CanonicalDeployRoot=Join-Path $root 'App';$CanonicalRecoveryRoot=Join-Path $root 'Recovery'
    $IncrementalRecoveryRoot=Join-Path $CanonicalRecoveryRoot 'IncrementalUpdates'
    $InteractiveHostRoot=Join-Path $root 'Host';$ValidationHoldPath=Join-Path $root 'hold.json'
    $SiteName='FluxKnowledge';$pool='Stopped'
    $ApplyOcrLifecycleRecovery=$false;$ResumeOcrLifecycleRecovery=$false
    $ApplyResumableSourceDeletionCleanupMigration=$false;$SourceDeletionAcceptanceMaintenanceReceipt=''
    $RecoverStoppedPool=$false;$ApplySegmentedIndexProjection=$false
    $ResumeHybridRebuildRelease='';$ResumeHybridPatchRelease=''
    function Get-Website { param($Name) @{physicalPath=$CanonicalDeployRoot;applicationPool='FluxKnowledge';State='Started'} }
    function Get-WebAppPoolState { param($Name) @{Value=$pool} }
    function Get-WebBinding { param($Name,$Protocol) @{bindingInformation='127.0.0.1:5137:'} }
    function Test-ApplicationPayload { param($Path) if (![IO.File]::Exists((Join-Path $Path 'FluxKnowledge.Application.dll'))) { throw 'fixture-application-incomplete' } }
    $task=@{Enabled=$false;State='Disabled';IdentityHash=('A'*64);LauncherSha256=('B'*64)}
    function Get-RepositoryRecoveryTaskObservation { $task }
    $assembly=$env:FLUXKNOWLEDGE_OCR_CONTRACT_ASSEMBLY
    if (!$assembly) { $assembly=Join-Path $SourceRoot 'artifacts/bin/FluxKnowledge.Application/release/FluxKnowledge.Application.dll' }
    if (![IO.File]::Exists($assembly)) { throw 'Build application before updater contract.' }
    $commit=([Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).ProductVersion -split '\+')[-1]
    $payloads=@(foreach ($role in @('Web','OutlookHost')) {
        $path=if ($role -eq 'Web') {$CanonicalDeployRoot}else{$InteractiveHostRoot}
        [void][IO.Directory]::CreateDirectory($path)
        Copy-Item -LiteralPath $assembly -Destination $path
        @{Role=$role;Sha256=(Get-HybridPayloadFingerprint $path)}
    })
    [void][IO.Directory]::CreateDirectory($CanonicalRecoveryRoot)
    $config=Join-Path $root 'config.json';$hostConfig=Join-Path $root 'host-config.json'
    [IO.File]::WriteAllText($config,'synthetic config');[IO.File]::WriteAllText($hostConfig,'synthetic host config')
    $plan=@{Version=1;OperationRoot=$root;ReleaseId='fixture-release';ImplementationSha256=(Get-OcrRecoveryImplementationHash);
        OriginalWebSha256=$payloads[0].Sha256;OriginalCompanionSha256=$payloads[1].Sha256;Payloads=$payloads;
        Task=@{Enabled=$true;IdentityHash=$task.IdentityHash;LauncherSha256=$task.LauncherSha256};Commit=$commit;
        ConfigurationPath=$config;ConfigurationSha256=(Get-FileHash $config).Hash;
        CompanionConfigurationPath=$hostConfig;CompanionConfigurationSha256=(Get-FileHash $hostConfig).Hash}
    $OcrRecoveryPlanPath=Join-Path $root 'plan.json';Write-OcrRecoveryJson $OcrRecoveryPlanPath $plan
    $ExpectedOcrRecoveryPlanSha256=(Get-FileHash $OcrRecoveryPlanPath).Hash
    $state=@{Version=1;PlanSha256=$ExpectedOcrRecoveryPlanSha256;Phase='FailedHeld';FailedAt='SettlementIntent';Completed=$false}
    $statePath=Join-Path $root 'deployment-state.json';Write-OcrRecoveryJson $statePath $state
    [IO.File]::WriteAllText($ValidationHoldPath,($plan.ReleaseId | ConvertTo-Json -Compress))

    Assert-Refused { Assert-IncrementalIisPreflight } 'application pool must be started'
    $ApplyOcrLifecycleRecovery=$true
    Assert-Refused { Assert-IncrementalIisPreflight } 'application pool must be started'
    $ResumeOcrLifecycleRecovery=$true
    Assert-IncrementalIisPreflight
    $savedHash=$ExpectedOcrRecoveryPlanSha256;$ExpectedOcrRecoveryPlanSha256='F'*64
    Assert-Refused { Assert-IncrementalIisPreflight } 'bound-resume-required'
    $ExpectedOcrRecoveryPlanSha256=$savedHash
    $state.PlanSha256='F'*64;Write-OcrRecoveryJson $statePath $state
    Assert-Refused { Assert-IncrementalIisPreflight } 'resume-binding-changed'
    $state.PlanSha256=$savedHash;Write-OcrRecoveryJson $statePath $state
    [IO.File]::WriteAllText($ValidationHoldPath,'"other-release"')
    Assert-Refused { Assert-IncrementalIisPreflight } 'resume-hold-unproven'
    [IO.File]::WriteAllText($ValidationHoldPath,($plan.ReleaseId | ConvertTo-Json -Compress))
    $task.Enabled=$true
    Assert-Refused { Assert-IncrementalIisPreflight } 'resume-companion-unproven'
    $task.Enabled=$false

    # A host crash cannot write FailedHeld. Resume must recognise the durable
    # intent itself and establish containment from the exact original controls.
    foreach ($phase in @('Prepared','HoldIntent','StopIntent')) {
        $state.Phase=$phase;Write-OcrRecoveryJson $statePath $state
        [IO.File]::Delete($ValidationHoldPath);$task.Enabled=$true;$task.State='Running';$pool='Started'
        Assert-IncrementalIisPreflight
        [IO.File]::WriteAllText($config,'drift')
        Assert-Refused { Assert-IncrementalIisPreflight } 'resume-configuration-changed'
        [IO.File]::WriteAllText($config,'synthetic config')
    }
    [IO.File]::WriteAllText($ValidationHoldPath,($plan.ReleaseId | ConvertTo-Json -Compress))
    $task.Enabled=$false;$task.State='Disabled';$pool='Stopped'
    $state.Phase='FailedHeld';Write-OcrRecoveryJson $statePath $state

    $release=Join-Path $IncrementalRecoveryRoot $plan.ReleaseId
    foreach ($role in @('Web','OutlookHost')) {
        $backup=Join-Path $release ('previous-'+$role);[void][IO.Directory]::CreateDirectory($backup)
        Copy-Item -LiteralPath $assembly -Destination $backup
    }
    $state.FailedAt='PayloadActivationIntent';Write-OcrRecoveryJson $statePath $state
    # Empty/missing/corrupt installed payloads can resume only from bound backup bytes.
    [IO.File]::Delete((Join-Path $CanonicalDeployRoot 'FluxKnowledge.Application.dll'))
    Assert-IncrementalIisPreflight
    $state.Phase='PayloadActivationIntent';Write-OcrRecoveryJson $statePath $state
    Assert-IncrementalIisPreflight
    # Re-entering containment must not erase an earlier activation boundary if
    # a second process loss occurs immediately after the new HoldIntent write.
    $capturedIntent=$null
    $resumePorts=@{AssertBindings={};EnsureHold={
        $script:capturedIntent=Get-Content -Raw $statePath | ConvertFrom-Json -AsHashtable
        throw 'synthetic second interruption'
    };StopOwners={};RestorePriorPayloads={}}
    Assert-Refused { Invoke-OcrRecoveryFlow $statePath $savedHash $resumePorts -Resume } 'failed-held'
    if (!$capturedIntent.ActivationStarted -or $capturedIntent.Phase -cne 'HoldIntent') { throw 'Activation origin was erased.' }
    Write-OcrRecoveryJson $statePath $capturedIntent
    Assert-IncrementalIisPreflight
    Write-OcrRecoveryJson $statePath $state
    [IO.Directory]::Delete($CanonicalDeployRoot)
    Assert-IncrementalIisPreflight
    [void][IO.Directory]::CreateDirectory($CanonicalDeployRoot)
    [IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'partial'),'partial activation')
    Assert-IncrementalIisPreflight
    $pool='Started'
    Assert-Refused { Assert-IncrementalIisPreflight } 'partial-payload-pool-not-stopped'
    $pool='Stopped'
    $backupFile=Join-Path $release 'previous-Web/FluxKnowledge.Application.dll'
    [IO.File]::WriteAllText($backupFile,'changed backup')
    Assert-Refused { Assert-IncrementalIisPreflight } 'resume-rollback-payload-unproven'
    Copy-Item -LiteralPath $assembly -Destination $backupFile -Force
    [IO.File]::Delete((Join-Path $CanonicalDeployRoot 'partial'))
    Copy-Item -LiteralPath $assembly -Destination $CanonicalDeployRoot

    foreach ($phase in @('StartIntent','HoldReleaseIntent')) {
        $state.Phase=$phase;Write-OcrRecoveryJson $statePath $state
        Assert-IncrementalIisPreflight
    }
    # Crash after release but before task-policy restoration or completion.
    [IO.File]::Delete($ValidationHoldPath);$task.Enabled=$true;$task.State='Running';$pool='Started'
    Assert-IncrementalIisPreflight
    [IO.File]::WriteAllText($ValidationHoldPath,($plan.ReleaseId | ConvertTo-Json -Compress))
    $task.Enabled=$false;$task.State='Disabled';$pool='Stopped'

    $proof={if ($pool -cne 'Stopped' -or ![IO.File]::Exists($ValidationHoldPath)) { throw 'fixture-stop-unproven' }}
    [IO.File]::WriteAllText($backupFile,'interrupted predecessor copy')
    Confirm-OcrRecoveryBackup $CanonicalDeployRoot (Split-Path -Parent $backupFile) $plan.OriginalWebSha256 $proof
    if ((Get-HybridPayloadFingerprint (Split-Path -Parent $backupFile)) -cne $plan.OriginalWebSha256) { throw 'Backup did not resume.' }
    [IO.File]::WriteAllText($backupFile,'interrupted predecessor copy')
    $pool='Started'
    Assert-Refused { Confirm-OcrRecoveryBackup $CanonicalDeployRoot (Split-Path -Parent $backupFile) $plan.OriginalWebSha256 $proof } 'fixture-stop-unproven'
    $pool='Stopped'
    [IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'partial'),'candidate or partial bytes')
    Assert-Refused { Confirm-OcrRecoveryBackup $CanonicalDeployRoot (Split-Path -Parent $backupFile) $plan.OriginalWebSha256 $proof } 'rollback-payload-changed'
    if ([IO.File]::ReadAllText($backupFile) -cne 'interrupted predecessor copy') { throw 'Unproved backup was changed.' }
    [IO.File]::Delete((Join-Path $CanonicalDeployRoot 'partial'))
    Confirm-OcrRecoveryBackup $CanonicalDeployRoot (Split-Path -Parent $backupFile) $plan.OriginalWebSha256 $proof

    # Completed replay checks actual bytes, configuration, commit and task policy,
    # before any mutation ports can be entered.
    [IO.File]::Delete($ValidationHoldPath);$task.Enabled=$true;$pool='Started'
    Assert-OcrRecoveryCompletedBindings $plan
    $mutations=0
    $ports=@{AssertCompleted={Assert-OcrRecoveryCompletedBindings $plan};AssertBindings={throw 'Must not enter ordinary bindings'};
        EnsureHold={$script:mutations++};StopOwners={$script:mutations++}}
    $state.Completed=$true;$state.Phase='Completed';Write-OcrRecoveryJson $statePath $state
    [void](Invoke-OcrRecoveryFlow $statePath $savedHash $ports -Resume)
    [IO.File]::WriteAllText($config,'drift')
    Assert-Refused { Invoke-OcrRecoveryFlow $statePath $savedHash $ports -Resume } 'completed-configuration-changed'
    [IO.File]::WriteAllText($config,'synthetic config')
    [IO.File]::WriteAllText((Join-Path $InteractiveHostRoot 'drift'),'drift')
    Assert-Refused { Invoke-OcrRecoveryFlow $statePath $savedHash $ports -Resume } 'completed-payload-changed'
    [IO.File]::Delete((Join-Path $InteractiveHostRoot 'drift'))
    $plan.Commit='F'*40
    Assert-Refused { Invoke-OcrRecoveryFlow $statePath $savedHash $ports -Resume } 'candidate-commit-mismatch'
    $plan.Commit=$commit;$task.Enabled=$false
    Assert-Refused { Invoke-OcrRecoveryFlow $statePath $savedHash $ports -Resume } 'completed-task-policy-changed'
    $task.Enabled=$true;$task.IdentityHash='F'*64
    Assert-Refused { Invoke-OcrRecoveryFlow $statePath $savedHash $ports -Resume } 'completed-task-policy-changed'
    if ($mutations) { throw 'Completed replay mutated production controls.' }

    # Exercise the actual dispatch branch, replacing only the external invocation
    # with a sentinel. A successful sentinel throws before the branch's exit 0.
    function Invoke-OcrLifecycleRecoveryUpdate { param($PlanPath,$ExpectedPlanSha256,[switch]$Resume)
        if (!$Resume -or $PlanPath -cne $OcrRecoveryPlanPath -or $ExpectedPlanSha256 -cne $savedHash) { throw 'incorrect-dispatch' }
        throw 'fixture-dispatch-reached'
    }
    $Apply=$true;$PlanOnly=$false;$OcrRecoveryOperationRoot='';$OcrRecoveryBatchId=[Guid]::Empty
    $dispatchHarness=[scriptblock]::Create('param([switch]$ApplyOcrLifecycleRecovery,[switch]$Apply,[switch]$PlanOnly,
        [string]$OcrRecoveryPlanPath,[string]$ExpectedOcrRecoveryPlanSha256,[switch]$ResumeOcrLifecycleRecovery,
        [string]$OcrRecoveryOperationRoot,[string]$OcrDesktopClassificationsPath,[Guid]$OcrRecoveryBatchId=[Guid]::Empty,[switch]$ApplyMigrations)'+"`n"+$dispatchText)
    $dispatchArguments=@{ApplyOcrLifecycleRecovery=$true;Apply=$true;OcrRecoveryPlanPath=$OcrRecoveryPlanPath;
        ExpectedOcrRecoveryPlanSha256=$savedHash;ResumeOcrLifecycleRecovery=$true}
    Assert-Refused { & $dispatchHarness @dispatchArguments } 'fixture-dispatch-reached'
    $dispatchArguments.ApplyMigrations=$true
    Assert-Refused { & $dispatchHarness @dispatchArguments } 'incompatible-mode'
    [void]$dispatchArguments.Remove('ApplyMigrations');$dispatchArguments.OcrRecoveryOperationRoot=$root
    Assert-Refused { & $dispatchHarness @dispatchArguments } 'apply-requires-reviewed-plan'
    'OCR updater contracts passed: actual preflight/dispatch, bound stopped and partial resume, read-only completed drift rejection.'
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    $prefix=[IO.Path]::GetFullPath((Join-Path $SourceRoot '.agents'))+'\'
    if (!$resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or ![IO.Path]::GetFileName($resolved).StartsWith('ocr-updater-contract_')) {
        throw 'Unsafe fixture cleanup target.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
