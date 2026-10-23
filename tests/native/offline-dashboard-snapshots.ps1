param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'scripts/deploy/offline-code-maintenance.ps1')
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-update.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-dashboard-snapshots.psm1') -Force
function Expect-Failure([scriptblock]$Action,[string]$Message){try{& $Action}catch{if($_.Exception.Message -match $Message){return};throw};throw ('Expected refusal: '+$Message)}
$root=[IO.Directory]::CreateTempSubdirectory('FluxOfflineDashboard_').FullName
$CanonicalLiveRoot=$root;$CanonicalDeployRoot=Join-Path $root 'App';$InteractiveHostRoot=Join-Path $root 'companion/outlook-host'
$ValidationHoldPath=Join-Path $root 'Runtime/hold.json';$configurationPath=Join-Path $root 'Config/appsettings.Production.json'
foreach($path in @($CanonicalDeployRoot,$InteractiveHostRoot,(Split-Path -Parent $ValidationHoldPath),(Split-Path -Parent $configurationPath))){[void][IO.Directory]::CreateDirectory($path)}
[IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'marker'),'old-web')
[IO.File]::WriteAllText((Join-Path $InteractiveHostRoot 'marker'),'old-host')
[IO.File]::WriteAllText($configurationPath,'{}')
[IO.File]::WriteAllText((Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json'),'{}')
[IO.File]::WriteAllText($ValidationHoldPath,'"fixture-maintenance"')
$proof=@{Stopped=$true;Processes=@();Task=@{Enabled=$false;State='Disabled';IdentityHash=('A'*64);LauncherSha256=('B'*64);LauncherPath=(Join-Path $root 'launcher.vbs')}}
function Assert-StoppedPoolRecoveryBaseline {if(!$proof.Stopped){throw 'pool-not-stopped'}}
function Get-RepositoryRecoveryTaskObservation {$proof.Task}
function Get-CimInstance {param($ClassName,$ErrorAction);$proof.Processes}
function Get-HybridPayloadFingerprint([string]$Path){(Get-FileHash -LiteralPath (Join-Path $Path 'marker')).Hash}
function Get-OfflineDashboardCandidateCommit {return ('a'*40)}
function Get-DeploymentSqlConnectionString {'disposable-stub-only'}
$database=@{Identity='disposable|fixture|incarnation';SchemaSha256=('C'*64);History='fixture';Projection='fixture-format2-stamp';RequiredFormat=2}
function Get-OfflineCodeDatabaseBinding {$database}
$option=@{Identity=$database.Identity;SnapshotState=0;RcsiEnabled=$false;AdrEnabled=$false}
function Get-DashboardSnapshotDatabaseState {param($ConnectionString);$option.Clone()}
$dashboardSnapshotChange=New-DashboardSnapshotConfigurationChange $configurationPath
$sourceAccessChange=$null;$EnableDashboardSnapshots=$true;$offlineCodeState=$null;$checks=0
$newWeb=Join-Path $root 'new-web';$newHost=Join-Path $root 'new-host'
foreach($path in @($newWeb,$newHost)){[void][IO.Directory]::CreateDirectory($path)}
[IO.File]::WriteAllText((Join-Path $newWeb 'marker'),'new-web');[IO.File]::WriteAllText((Join-Path $newHost 'marker'),'new-host')
$receipt=@{Version=3;Database=$database.Clone();WebSha256=(Get-HybridPayloadFingerprint $CanonicalDeployRoot);CompanionSha256=(Get-HybridPayloadFingerprint $InteractiveHostRoot);
    ConfigurationSha256=$dashboardSnapshotChange.BeforeHash;CompanionConfigurationSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json')).Hash;
    TaskIdentityHash=$proof.Task.IdentityHash;LauncherSha256=$proof.Task.LauncherSha256;
    DashboardSnapshot=@{Capability='dashboard-committed-sample-v1';DatabaseIdentity=$database.Identity;InitialSnapshotState=0;IntendedSnapshotState=1;
        RcsiEnabled=$false;AdrEnabled=$false;BeforeSha256=$dashboardSnapshotChange.BeforeHash;AfterSha256=$dashboardSnapshotChange.AfterHash;
        CandidateCommit=('a'*40);CandidateWebSha256=(Get-HybridPayloadFingerprint $newWeb);CandidateCompanionSha256=(Get-HybridPayloadFingerprint $newHost)};
    Stop=@{Completed=$true;Phase='iis-writers-stopped';StartedUtc=[DateTime]::UtcNow.AddMinutes(-2).ToString('O');HoldOwner='fixture-maintenance';HoldSha256=(Get-FileHash -LiteralPath $ValidationHoldPath).Hash;
        ExitProof=@{VerifiedAtUtc=[DateTime]::UtcNow.AddMinutes(-1).ToString('O');ExitedIdentities=@()}}}
$OfflineCodeMaintenanceReceipt=Join-Path $root 'receipt.json'
function Save-Receipt {Write-SegmentedUpdateJson $OfflineCodeMaintenanceReceipt $receipt;$script:ExpectedOfflineCodeMaintenanceReceiptSha256=(Get-FileHash -LiteralPath $OfflineCodeMaintenanceReceipt).Hash}
Save-Receipt
Assert-OfflineCodeMaintenance -RequireStopped -OriginalPayloads;$checks++
foreach($version in @(1,2)) {
    $receipt.Version=$version;Save-Receipt
    Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'dashboard-requires-version3';$checks++
}
$receipt.Version=3;Save-Receipt
$EnableDashboardSnapshots=$false;Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'dashboard-configuration-binding';$EnableDashboardSnapshots=$true;$checks++
foreach($key in @('DatabaseIdentity','BeforeSha256','AfterSha256','CandidateCommit','Capability')) {
    $saved=$receipt.DashboardSnapshot[$key];$receipt.DashboardSnapshot[$key]='changed';Save-Receipt
    Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'dashboard-configuration-binding'
    $receipt.DashboardSnapshot[$key]=$saved;Save-Receipt;$checks++
}
foreach($change in @('Identity','SnapshotState','RcsiEnabled','AdrEnabled')) {
    $saved=$option[$change];$option[$change]=$(switch($change){'Identity'{'different-incarnation'};'SnapshotState'{3};default{$true}})
    Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'dashboard-option';$option[$change]=$saved;$checks++
}
Expect-Failure {New-OfflineCodeMaintenanceState -StatePath (Join-Path $root 'wrong-state.json') -ReleaseId 'fixture-release' -CandidateWebSha256 ('E'*64) -CandidateCompanionSha256 $receipt.DashboardSnapshot.CandidateCompanionSha256} 'candidate-binding';$checks++
$statePath=Join-Path $root 'state.json'
$offlineCodeState=New-OfflineCodeMaintenanceState -StatePath $statePath -ReleaseId 'fixture-release' -CandidateWebSha256 $receipt.DashboardSnapshot.CandidateWebSha256 -CandidateCompanionSha256 $receipt.DashboardSnapshot.CandidateCompanionSha256
Move-OfflineCodeMaintenanceHold -StatePath $statePath -State $offlineCodeState
Copy-Item -LiteralPath (Join-Path $newWeb 'marker') -Destination $CanonicalDeployRoot -Force
Copy-Item -LiteralPath (Join-Path $newHost 'marker') -Destination $InteractiveHostRoot -Force
Set-DashboardSnapshotConfigurationChange $dashboardSnapshotChange
Expect-Failure {Start-OfflineCodeMaintenanceIntent -StatePath $statePath -State $offlineCodeState} 'activation-required-before-start';$checks++
Invoke-DashboardSnapshotActivation -State $offlineCodeState -Binding $receipt.DashboardSnapshot -ReadState {$option.Clone()} -Enable {$option.SnapshotState=1} -WriteState {Write-SegmentedUpdateJson $statePath $offlineCodeState}
$option.SnapshotState=3
Expect-Failure {Start-OfflineCodeMaintenanceIntent -StatePath $statePath -State $offlineCodeState} 'fully-on';$checks++
$option.SnapshotState=1
# Pre-start recovery preserves the enabled option and exact predecessor bytes.
Restore-DashboardSnapshotConfigurationChange $dashboardSnapshotChange
Copy-Item -LiteralPath (Join-Path $CanonicalDeployRoot 'marker') -Destination (Join-Path $root 'candidate-marker')
[IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'marker'),'old-web')
[IO.File]::WriteAllText((Join-Path $InteractiveHostRoot 'marker'),'old-host')
Assert-OfflineCodeMaintenance -RequireStopped -OriginalPayloads
if($option.SnapshotState -ne 1){throw 'Recovery reversed the database option.'};$checks++
Copy-Item -LiteralPath (Join-Path $newWeb 'marker') -Destination $CanonicalDeployRoot -Force
Copy-Item -LiteralPath (Join-Path $newHost 'marker') -Destination $InteractiveHostRoot -Force
Set-DashboardSnapshotConfigurationChange $dashboardSnapshotChange
Start-OfflineCodeMaintenanceIntent -StatePath $statePath -State $offlineCodeState
if(!(Read-SegmentedUpdateJson $statePath).StartIntentUtc){throw 'Start intent is not durable.'};$checks++
Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'fresh-stop-required';$checks++
Assert-OfflineCodeMaintenance -CandidatePayloads;$checks++
$database.SchemaSha256='changed';Expect-Failure {Assert-OfflineCodeMaintenance -CandidatePayloads} 'database-changed';$database.SchemaSha256='C'*64;$checks++
# Exercise the updater's real callback composition and outer companion recovery.
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Updater parse failed.'}
$swaps=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Invoke-IncrementalApplicationPayloadSwap'},$true))
$swap=@($swaps|Where-Object {$parent=$_.Parent;while($parent -and $parent -isnot [Management.Automation.Language.FunctionDefinitionAst]){$parent=$parent.Parent};!$parent})[0]
function Get-Callback([string]$Name){$elements=$swap.CommandElements;for($i=0;$i -lt $elements.Count;$i++){if($elements[$i] -is [Management.Automation.Language.CommandParameterAst] -and $elements[$i].ParameterName -ceq $Name){return $elements[$i+1].ScriptBlock.GetScriptBlock()}};throw 'Callback missing'}
$outer=$swap.Parent;while($outer -and $outer -isnot [Management.Automation.Language.TryStatementAst]){$outer=$outer.Parent}
$outerCatch=[scriptblock]::Create(($outer.CatchClauses[0].Body.Statements.Extent.Text -join "`n"))
$stopDefinition=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Stop-HybridIisAfterGpuDrain'},$true))[0]
Invoke-Expression $stopDefinition.Extent.Text
foreach($parameter in $ast.ParamBlock.Parameters){$name=$parameter.Name.VariablePath.UserPath;if(!(Get-Variable -Name $name -ErrorAction SilentlyContinue)){Set-Variable -Name $name -Value $null}}
$RecoverStoppedPool=$true;$SiteName='DisposableOfflineDashboard';$ReadinessTimeoutSeconds=10;$releaseRoot=$root
$holding=@{Starts=0;CompanionRestores=0}
$hostingChange=@{Attempted=$false;RollbackVerified=$true}
function New-DeploymentValidationHold {param($Path,$ReleaseId);if((Read-SegmentedUpdateJson $Path) -cne $ReleaseId){throw 'Wrong hold owner'}}
function Get-RetainedPipelineStateBaseline {@{UncertainSlots=1;Corpus='fixture'}}
function Assert-RetainedPipelineStateUnchanged {param($Baseline,$Current);if(($Baseline|ConvertTo-Json -Compress) -cne ($Current|ConvertTo-Json -Compress)){throw 'Retained state changed'}}
function Invoke-CandidatePayloadActivation {param($CandidateRoot,$ApplicationRoot);Copy-Item -LiteralPath $CandidateRoot -Destination $ApplicationRoot -Recurse}
function Test-InteractiveHostPayload {param($Path);if(!(Test-Path -LiteralPath (Join-Path $Path 'marker'))){throw 'Companion missing'}}
function Restore-InteractiveHostPayload {param($PreviousRoot,$LiveRoot);$holding.CompanionRestores++;Copy-Item -LiteralPath (Join-Path $PreviousRoot 'marker') -Destination $LiveRoot -Force}
function Start-IndexCompatibleWebAppPool {param($Name);$holding.Starts++;$proof.Stopped=$false;throw 'synthetic ambiguous start'}
function Reset-Phase([string]$Name,[int]$Initial=1) {
    [IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'marker'),'old-web')
    [IO.File]::WriteAllText((Join-Path $InteractiveHostRoot 'marker'),'old-host')
    [IO.File]::WriteAllText($configurationPath,'{}')
    [IO.File]::WriteAllText($ValidationHoldPath,'"fixture-maintenance"')
    $script:dashboardSnapshotChange=New-DashboardSnapshotConfigurationChange $configurationPath
    $receipt.DashboardSnapshot.InitialSnapshotState=$Initial;$receipt.Stop.HoldSha256=(Get-FileHash -LiteralPath $ValidationHoldPath).Hash;Save-Receipt
    $option.SnapshotState=$Initial;$proof.Stopped=$true;$script:offlineCodeState=$null
    $script:releaseId='fixture-'+$Name
    $script:statePath=Join-Path $root ($Name+'-state.json')
    $script:offlineCodeState=New-OfflineCodeMaintenanceState -StatePath $statePath -ReleaseId $releaseId -CandidateWebSha256 $receipt.DashboardSnapshot.CandidateWebSha256 -CandidateCompanionSha256 $receipt.DashboardSnapshot.CandidateCompanionSha256
    Move-OfflineCodeMaintenanceHold -StatePath $statePath -State $offlineCodeState
    Invoke-DashboardSnapshotActivation -State $offlineCodeState -Binding $receipt.DashboardSnapshot -ReadState {$option.Clone()} -Enable {throw 'Already ON expected'} -WriteState {Write-SegmentedUpdateJson $statePath $offlineCodeState}
    $script:candidateRoot=$newWeb;$script:previousRoot=Join-Path $root ($Name+'-previous');$script:failedRoot=Join-Path $root ($Name+'-failed')
    $script:interactiveHostPreviousRoot=Join-Path $root ($Name+'-host-previous');[void][IO.Directory]::CreateDirectory($interactiveHostPreviousRoot)
    Copy-Item -LiteralPath (Join-Path $InteractiveHostRoot 'marker') -Destination $interactiveHostPreviousRoot
    Copy-Item -LiteralPath (Join-Path $newHost 'marker') -Destination $InteractiveHostRoot -Force
    $script:recoveryOriginalPayloadHash=$receipt.WebSha256;$script:recoveryOriginalInteractiveHostHash=$receipt.CompanionSha256
    $script:interactiveHostMutationStarted=$true;$script:interactiveHostRollbackVerified=$false
    $script:holdReleaseAttempted=$false;$script:holdReleased=$false
    $script:deploymentValidation=@{HoldCreated=$true;Baseline=(Get-RetainedPipelineStateBaseline);PayloadRollbackVerified=$false}
}
function Invoke-ComposedSwap {
    try {
        Invoke-IncrementalApplicationPayloadSwap -ApplicationRoot $CanonicalDeployRoot -CandidateRoot $candidateRoot -PreviousRoot $previousRoot -FailedRoot $failedRoot -RestartPreviousApplication $false `
            -StopApplication (Get-Callback 'StopApplication') -ActivateCandidate (Get-Callback 'ActivateCandidate') `
            -StartApplication (Get-Callback 'StartApplication') -ValidateApplication (Get-Callback 'ValidateApplication') `
            -PrepareRollbackApplication (Get-Callback 'PrepareRollbackApplication') -ValidateRollbackApplication (Get-Callback 'ValidateRollbackApplication')
    } catch { & $outerCatch }
}
$realStart=(Get-Command Start-OfflineCodeMaintenanceIntent).ScriptBlock
Reset-Phase 'before-start'
function Start-OfflineCodeMaintenanceIntent {param($StatePath,$State);Assert-DashboardSnapshotConfigurationChange $dashboardSnapshotChange;throw 'synthetic after config before intent'}
Expect-Failure {Invoke-ComposedSwap} 'companion restored=True'
Assert-DashboardSnapshotConfigurationChange $dashboardSnapshotChange -Original
if((Get-HybridPayloadFingerprint $CanonicalDeployRoot) -cne $receipt.WebSha256 -or
    (Get-HybridPayloadFingerprint $InteractiveHostRoot) -cne $receipt.CompanionSha256 -or $holding.Starts -ne 0 -or
    $offlineCodeState.StartIntentUtc -or $option.SnapshotState -ne 1 -or !$proof.Stopped -or (Read-SegmentedUpdateJson $ValidationHoldPath) -cne $releaseId){throw 'Pre-start composed recovery failed.'};$checks++
Set-Item Function:Start-OfflineCodeMaintenanceIntent $realStart
Reset-Phase 'after-intent'
$restores=$holding.CompanionRestores
Expect-Failure {Invoke-ComposedSwap} 'retain matching payloads'
Assert-DashboardSnapshotConfigurationChange $dashboardSnapshotChange
if((Get-HybridPayloadFingerprint $CanonicalDeployRoot) -cne $receipt.DashboardSnapshot.CandidateWebSha256 -or
    (Get-HybridPayloadFingerprint $InteractiveHostRoot) -cne $receipt.DashboardSnapshot.CandidateCompanionSha256 -or
    !$offlineCodeState.StartIntentUtc -or $holding.CompanionRestores -ne $restores -or
    !(Test-Path -LiteralPath $previousRoot) -or (Test-Path -LiteralPath $failedRoot) -or (Read-SegmentedUpdateJson $ValidationHoldPath) -cne $releaseId){throw 'Ambiguous start changed the matching candidate pair/hold.'};$checks++
# Execute the real initial recovery block. An interrupted ALTER must stop it
# before the subsequent companion-copy and payload-swap statements are reached.
[IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'marker'),'old-web')
[IO.File]::WriteAllText((Join-Path $InteractiveHostRoot 'marker'),'old-host')
[IO.File]::WriteAllText($configurationPath,'{}');[IO.File]::WriteAllText($ValidationHoldPath,'"fixture-maintenance"')
$dashboardSnapshotChange=New-DashboardSnapshotConfigurationChange $configurationPath
$receipt.DashboardSnapshot.InitialSnapshotState=0;$receipt.Stop.HoldSha256=(Get-FileHash -LiteralPath $ValidationHoldPath).Hash;Save-Receipt
$option.SnapshotState=0;$proof.Stopped=$true;$offlineCodeState=$null;$releaseId='fixture-interrupted';$deploymentValidation=@{HoldCreated=$false;Baseline=$null}
$interactiveHostCandidateRoot=$newHost
function Enable-DashboardSnapshotDatabaseOption {param($ConnectionString,$Binding);$option.SnapshotState=3;throw 'synthetic ALTER interruption'}
$recovery=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text.StartsWith('if ($RecoverStoppedPool) {') -and $n.Extent.Text.Contains('New-OfflineCodeMaintenanceState')},$true))
if($recovery.Count -ne 1){throw 'Initial recovery block ambiguous.'}
$starts=$holding.Starts
Expect-Failure {& ([scriptblock]::Create($recovery[0].Extent.Text))} 'ALTER interruption'
$interrupted=Read-SegmentedUpdateJson (Join-Path $root ('offline-code-update-'+$releaseId+'.json'))
if($holding.Starts -ne $starts -or (Get-HybridPayloadFingerprint $CanonicalDeployRoot) -cne $receipt.WebSha256 -or
    (Get-HybridPayloadFingerprint $InteractiveHostRoot) -cne $receipt.CompanionSha256 -or $interrupted.StartIntentUtc -or
    $interrupted.DashboardSnapshotActivation.Status -cne 'ReconciliationRequired' -or $interrupted.DashboardSnapshotActivation.LastObserved.SnapshotState -ne 3){throw 'Interrupted option activation reached payload mutation or lost resulting state.'};$checks++
@{Status='PASS';Checks=$checks;ProductionAccess=$false;Root=$root}|ConvertTo-Json

