param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-update.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
. (Join-Path $SourceRoot 'scripts/deploy/incremental-stopped-release-recovery.ps1')

function Assert-StoppedPoolRecoveryBaseline { if(!$script:PoolStopped){throw 'fixture-worker-remains'} }
function Get-RepositoryRecoveryTaskObservation { @{Enabled=$script:TaskEnabled;State='Disabled';IdentityHash=$script:TaskHash;LauncherSha256=('C'*64)} }
function Wait-InteractiveHostStopped { param($TaskName,$TimeoutSeconds) if($script:CompanionRunning){throw 'fixture-companion-remains'} }
function Get-CimInstance { param($ClassName,$ErrorAction) @($script:Launchers) }
function Get-DeploymentSqlConnectionString { 'fixture-web' }
function Get-RepositoryRecoveryCompanionConnectionString { 'fixture-companion' }
function Get-SourceJournalDatabaseState { param($ConnectionString) @{Identity=$script:DatabaseIdentity} }
function Assert-Fails([scriptblock]$Action) {
    try { & $Action } catch { return }
    throw 'Expected stopped recovery to refuse without mutation.'
}
function New-RecoveryFixture {
    $script:FixtureRoot=Join-Path ([IO.Path]::GetTempPath()) ('StoppedReleaseRecovery-'+[guid]::NewGuid().ToString('N'))
    $script:CanonicalLiveRoot=Join-Path $FixtureRoot 'Live'
    $script:CanonicalDeployRoot=Join-Path $CanonicalLiveRoot 'App'
    $script:IncrementalRecoveryRoot=Join-Path $CanonicalLiveRoot 'Recovery/IncrementalUpdates'
    $script:InteractiveHostRoot=Join-Path $FixtureRoot 'Companion/payload'
    $script:SourceJournalHelperRoot=Join-Path $FixtureRoot 'Helper'
    $script:ValidationHoldPath=Join-Path $CanonicalLiveRoot 'Runtime/deployment-validation-hold.json'
    $script:InteractiveHostTaskName='FluxKnowledge.OutlookHost'
    $script:SiteName='FluxKnowledge'
    $script:SiteUrl='http://127.0.0.1:5137'
    $script:PoolStopped=$true; $script:TaskEnabled=$false; $script:CompanionRunning=$false; $script:Launchers=@()
    $script:TaskHash='B'*64
    $script:DatabaseIdentity='fixture|isolated|11111111-1111-1111-1111-111111111111'
    $script:PriorReleaseId='20261031T000000Z-aaaaaaaaaaaa'
    $script:NextReleaseId='20261031T000001Z-dddddddddddd'
    $prior=Join-Path $IncrementalRecoveryRoot $PriorReleaseId
    foreach($path in @($CanonicalDeployRoot,$InteractiveHostRoot,(Join-Path $SourceJournalHelperRoot 'payload'),
        (Join-Path $CanonicalLiveRoot 'Config'),(Split-Path -Parent $ValidationHoldPath),$prior,
        (Join-Path $prior 'candidate'),(Join-Path $prior 'candidate-interactive-host'))) {
        [void][IO.Directory]::CreateDirectory($path)
    }
    foreach($path in @($CanonicalDeployRoot,(Join-Path $prior 'candidate'))){[IO.File]::WriteAllText((Join-Path $path 'marker.txt'),'web')}
    foreach($path in @($InteractiveHostRoot,(Join-Path $prior 'candidate-interactive-host'))){[IO.File]::WriteAllText((Join-Path $path 'marker.txt'),'companion')}
    [IO.File]::WriteAllText((Join-Path $SourceJournalHelperRoot 'payload/marker.txt'),'helper')
    $script:ConfigPath=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
    $script:CompanionConfig=Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json'
    [IO.File]::WriteAllText($ConfigPath,'{"fixture":true}')
    [IO.File]::WriteAllText($CompanionConfig,'{"fixture":true}')
    Write-SegmentedUpdateJson $ValidationHoldPath $PriorReleaseId
    $manifest=Join-Path $prior 'manifest.json'
    Write-SegmentedUpdateJson $manifest @{commit=('a'*40);application_root=$CanonicalDeployRoot;migrations=$false;clean_slate=$false}
    $script:Binding=@{Version=1;Purpose='incremental-stopped-release';PriorReleaseId=$PriorReleaseId;PriorCommit=('a'*40);
        PriorManifestSha256=(Get-FileHash -LiteralPath $manifest).Hash;HoldSha256=(Get-FileHash -LiteralPath $ValidationHoldPath).Hash;
        ConfigurationSha256=(Get-FileHash -LiteralPath $ConfigPath).Hash;CompanionConfigurationSha256=(Get-FileHash -LiteralPath $CompanionConfig).Hash;
        DatabaseIdentity=$DatabaseIdentity;TaskIdentityHash=$TaskHash;LauncherSha256=('C'*64);EnableCompanionAfterValidation=$true;
        Payloads=@(@{Role='Web';Root=$CanonicalDeployRoot;Sha256=(Get-HybridPayloadFingerprint $CanonicalDeployRoot)},
            @{Role='OutlookHost';Root=$InteractiveHostRoot;Sha256=(Get-HybridPayloadFingerprint $InteractiveHostRoot)},
            @{Role='Cli';Root=(Join-Path $SourceJournalHelperRoot 'payload');Sha256=(Get-HybridPayloadFingerprint (Join-Path $SourceJournalHelperRoot 'payload'))})}
    $script:BindingPath=Join-Path $FixtureRoot 'binding.json'
    Write-SegmentedUpdateJson $BindingPath $Binding
    $script:BindingSha256=(Get-FileHash -LiteralPath $BindingPath).Hash
    $script:StatePath=Join-Path $FixtureRoot 'adoption.json'
}
function Read-FixtureBinding { Read-IncrementalStoppedRecoveryBinding -Path $BindingPath -ExpectedSha256 $BindingSha256 }

New-RecoveryFixture
$accepted=Read-FixtureBinding
Assert-IncrementalStoppedRecoveryBinding $accepted
Move-IncrementalStoppedRecoveryHold -Binding $accepted -ReleaseId $NextReleaseId -StatePath $StatePath
if((Read-SegmentedUpdateJson $ValidationHoldPath) -cne $NextReleaseId -or !(Read-SegmentedUpdateJson $StatePath).HoldTransferred){throw 'Exact stopped hold was not adopted.'}
Assert-Fails { Move-IncrementalStoppedRecoveryHold -Binding $accepted -ReleaseId $NextReleaseId -StatePath $StatePath }
if((Read-SegmentedUpdateJson $ValidationHoldPath) -cne $NextReleaseId){throw 'Replay changed the adopted hold.'}

foreach($drift in @('hold','payload','config','companion-config','database','task','enabled','worker','companion','launcher','manifest')) {
    New-RecoveryFixture
    $accepted=Read-FixtureBinding
    switch($drift) {
        'hold' { Write-SegmentedUpdateJson $ValidationHoldPath 'foreign-owner' }
        'payload' { [IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'marker.txt'),'changed') }
        'config' { [IO.File]::WriteAllText($ConfigPath,'changed') }
        'companion-config' { [IO.File]::WriteAllText($CompanionConfig,'changed') }
        'database' { $script:DatabaseIdentity='foreign-database' }
        'task' { $script:TaskHash='D'*64 }
        'enabled' { $script:TaskEnabled=$true }
        'worker' { $script:PoolStopped=$false }
        'companion' { $script:CompanionRunning=$true }
        'launcher' { $script:Launchers=@(@{Name='wscript.exe';CommandLine='wscript.exe //B //NoLogo "E:\Codex Workspaces\Scripts\Outlook\run-outlook-hidden.vbs"'}) }
        'manifest' { [IO.File]::WriteAllText((Join-Path $IncrementalRecoveryRoot "$PriorReleaseId/manifest.json"),'changed') }
    }
    $holdBefore=[IO.File]::ReadAllBytes($ValidationHoldPath)
    Assert-Fails { Move-IncrementalStoppedRecoveryHold -Binding $accepted -ReleaseId $NextReleaseId -StatePath $StatePath }
    if(Test-Path -LiteralPath $StatePath){throw "Drift was not rejected before transfer intent: $drift"}
    if([Convert]::ToBase64String($holdBefore) -cne [Convert]::ToBase64String([IO.File]::ReadAllBytes($ValidationHoldPath))){throw "Drift changed the hold: $drift"}
}
New-RecoveryFixture
$wrong=$Binding.Clone();$wrong.PriorReleaseId='../foreign'
Write-SegmentedUpdateJson $BindingPath $wrong
$script:BindingSha256=(Get-FileHash -LiteralPath $BindingPath).Hash
Assert-Fails { Read-FixtureBinding }
Assert-Fails { Read-IncrementalStoppedRecoveryBinding -Path $BindingPath -ExpectedSha256 ('F'*64) }

# Keep the actual atomic writer and inject interruption at its two durable boundaries.
$script:AtomicWriter=(Get-Command Write-SegmentedUpdateJson).ScriptBlock
function Write-SegmentedUpdateJson {
    param($Path,$Value)
    if($script:Interrupt -and $Path -ceq $ValidationHoldPath) {
        if(!(Test-Path -LiteralPath $StatePath)){throw 'Transfer attempted before durable intent.'}
        if($script:Interrupt -ceq 'after-intent'){throw 'injected-after-intent'}
    }
    & $script:AtomicWriter $Path $Value
    if($script:Interrupt -ceq 'after-replacement' -and $Path -ceq $ValidationHoldPath){throw 'injected-after-replacement'}
}
foreach($boundary in @('after-intent','after-replacement')) {
    $script:Interrupt=$null
    New-RecoveryFixture
    $accepted=Read-FixtureBinding
    $script:Interrupt=$boundary
    Assert-Fails { Move-IncrementalStoppedRecoveryHold -Binding $accepted -ReleaseId $NextReleaseId -StatePath $StatePath }
    $script:Interrupt=$null
    $state=Read-SegmentedUpdateJson $StatePath
    $expectedOwner=if($boundary -ceq 'after-intent'){$PriorReleaseId}else{$NextReleaseId}
    if($state.HoldTransferred -or !(Test-Path -LiteralPath $ValidationHoldPath) -or
        (Read-SegmentedUpdateJson $ValidationHoldPath) -cne $expectedOwner){throw "Interrupted transfer lost deny-all ownership: $boundary"}
    Assert-Fails { Move-IncrementalStoppedRecoveryHold -Binding $accepted -ReleaseId $NextReleaseId -StatePath $StatePath }
    if((Read-SegmentedUpdateJson $ValidationHoldPath) -cne $expectedOwner){throw 'Interrupted replay changed the hold.'}
    foreach($payload in $accepted.Payloads){if((Get-HybridPayloadFingerprint $payload.Root) -cne $payload.Sha256){throw 'Transfer mutated a payload.'}}
}
Write-Output 'Incremental stopped-release recovery passed: exact adoption, drift refusal, continuous hold and interrupted-transfer replay refusal.'
