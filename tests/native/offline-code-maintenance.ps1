param([string]$SourceRoot='')
$ErrorActionPreference='Stop'
if(!$SourceRoot){$SourceRoot=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))}
$updater=Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($updater,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Updater parse failed.'}
if(@($ast.ParamBlock.Parameters | Where-Object {$_.Name.VariablePath.UserPath -ceq 'OfflineCodeMaintenanceReceipt'}).Count -ne 1){
    throw 'Explicit hash-bound offline code maintenance mode is missing.'
}
. (Join-Path $SourceRoot 'scripts/deploy/offline-code-maintenance.ps1')
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-update.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force
function Expect-Failure([scriptblock]$Action,[string]$Message){try{& $Action}catch{if($_.Exception.Message -match $Message){return};throw};throw ('Expected refusal: '+$Message)}
$root=Join-Path ([IO.Path]::GetTempPath()) ('FluxOfflineCode_'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$CanonicalLiveRoot=$root;$CanonicalDeployRoot=Join-Path $root 'App';$InteractiveHostRoot=Join-Path $root 'companion/outlook-host'
$ValidationHoldPath=Join-Path $root 'Runtime/hold.json';$releaseId='fixture-code-release'
foreach($path in @($CanonicalDeployRoot,$InteractiveHostRoot,(Split-Path -Parent $ValidationHoldPath),(Join-Path $root 'Config'))){[void][IO.Directory]::CreateDirectory($path)}
[IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'marker'),'old-web')
[IO.File]::WriteAllText((Join-Path $InteractiveHostRoot 'marker'),'old-host')
[IO.File]::WriteAllText((Join-Path $root 'Config/appsettings.Production.json'),'{}')
[IO.File]::WriteAllText((Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json'),'{}')
[IO.File]::WriteAllText($ValidationHoldPath,'"fixture-maintenance"')
$proof=@{Stopped=$true;Processes=@();Task=@{Enabled=$false;State='Disabled';IdentityHash=('a'*64);LauncherSha256=('b'*64);LauncherPath=(Join-Path $root 'launcher.vbs')}}
function Assert-StoppedPoolRecoveryBaseline {if(!$proof.Stopped){throw 'pool-not-stopped'}}
function Get-RepositoryRecoveryTaskObservation {$proof.Task}
function Get-CimInstance {param($ClassName,$ErrorAction);$proof.Processes}
function Get-HybridPayloadFingerprint([string]$Path){(Get-FileHash -LiteralPath (Join-Path $Path 'marker')).Hash}
$database=@{Identity='disposable|fixture|incarnation';SchemaSha256=('c'*64);History='fixture';Projection='fixture-format2-stamp';RequiredFormat=2}
function Get-OfflineCodeDatabaseBinding {$database}
$receipt=@{Version=1;Database=$database.Clone();WebSha256=(Get-HybridPayloadFingerprint $CanonicalDeployRoot);CompanionSha256=(Get-HybridPayloadFingerprint $InteractiveHostRoot);
    ConfigurationSha256=(Get-FileHash -LiteralPath (Join-Path $root 'Config/appsettings.Production.json')).Hash;
    CompanionConfigurationSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json')).Hash;
    TaskIdentityHash=$proof.Task.IdentityHash;LauncherSha256=$proof.Task.LauncherSha256;
    Stop=@{Completed=$true;Phase='iis-writers-stopped';StartedUtc=[DateTime]::UtcNow.AddMinutes(-2).ToString('o');HoldOwner='fixture-maintenance';HoldSha256=(Get-FileHash -LiteralPath $ValidationHoldPath).Hash;
        ExitProof=@{VerifiedAtUtc=[DateTime]::UtcNow.AddMinutes(-1).ToString('o');ExitedIdentities=@(@{ProcessId=123;Created='2026-01-01T00:00:00Z'})}}}
$OfflineCodeMaintenanceReceipt=Join-Path $root 'receipt.json'
Write-SegmentedUpdateJson $OfflineCodeMaintenanceReceipt $receipt
$ExpectedOfflineCodeMaintenanceReceiptSha256=(Get-FileHash -LiteralPath $OfflineCodeMaintenanceReceipt).Hash
$offlineCodeState=$null;$checks=0
Assert-OfflineCodeMaintenance -RequireStopped -OriginalPayloads;$checks++
$savedReceiptBytes=[IO.File]::ReadAllBytes($OfflineCodeMaintenanceReceipt)
[IO.File]::AppendAllText($OfflineCodeMaintenanceReceipt,' ');Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'receipt-hash';[IO.File]::WriteAllBytes($OfflineCodeMaintenanceReceipt,$savedReceiptBytes);$checks++
$configurationPath=Join-Path $root 'Config/appsettings.Production.json'
[IO.File]::WriteAllText($configurationPath,'{"changed":true}');Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'configuration-changed';[IO.File]::WriteAllText($configurationPath,'{}');$checks++
$database.Projection='changed';Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'database-changed';$database.Projection='fixture-format2-stamp';$checks++
$proof.Processes=@(@{ProcessId=123;CreationDate=[DateTime]'2026-01-01T00:00:00Z';Name='worker.exe';CommandLine=''})
Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'owner-exit';$proof.Processes=@();$checks++
$proof.Processes=@(@{ProcessId=124;CreationDate=[DateTime]::UtcNow;Name='dotnet.exe';CommandLine='dotnet.exe "X:\fixture\FluxKnowledge.OutlookHost.dll"'})
Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'companion-exit';$proof.Processes=@();$checks++
$proof.Task.Enabled=$true;Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'companion-owner';$proof.Task.Enabled=$false;$checks++
[IO.File]::WriteAllText($ValidationHoldPath,'"another-owner"');Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'hold-changed';[IO.File]::WriteAllText($ValidationHoldPath,'"fixture-maintenance"');$checks++
$statePath=Join-Path $root 'state.json'
$candidate=Join-Path $root 'candidate';$previous=Join-Path $root 'previous';$failed=Join-Path $root 'failed'
[void][IO.Directory]::CreateDirectory($candidate);[IO.File]::WriteAllText((Join-Path $candidate 'marker'),'new-web')
$offlineCodeState=New-OfflineCodeMaintenanceState -StatePath $statePath -ReleaseId $releaseId -CandidateWebSha256 (Get-HybridPayloadFingerprint $candidate) -CandidateCompanionSha256 $receipt.CompanionSha256
# Interrupt the actual atomic-write boundary: intent must already be durable,
# and failure must leave the existing deny-all owner intact.
$realWrite=(Get-Command Write-SegmentedUpdateJson).ScriptBlock
function Write-SegmentedUpdateJson([string]$Path,$Value){if($Path -ceq $ValidationHoldPath){throw 'synthetic hold-transfer interruption'};& $realWrite $Path $Value}
Expect-Failure {Move-OfflineCodeMaintenanceHold -StatePath $statePath -State $offlineCodeState} 'hold-transfer interruption'
if(!(Read-SegmentedUpdateJson $statePath).HoldTransferIntentUtc -or (Read-SegmentedUpdateJson $ValidationHoldPath) -cne 'fixture-maintenance'){
    throw 'Interrupted hold transfer lost the deny-all owner or durable intent.'
};$checks++
Set-Item Function:Write-SegmentedUpdateJson $realWrite
Expect-Failure {Move-OfflineCodeMaintenanceHold -StatePath $statePath -State $offlineCodeState} 'transfer-reconciliation';$checks++
# A new disposable operation models separately reviewed reconciliation; the
# interrupted operation is never silently replayed.
$statePath=Join-Path $root 'reconciled-state.json';$offlineCodeState=$null
$offlineCodeState=New-OfflineCodeMaintenanceState -StatePath $statePath -ReleaseId $releaseId -CandidateWebSha256 (Get-HybridPayloadFingerprint $candidate) -CandidateCompanionSha256 $receipt.CompanionSha256
Move-OfflineCodeMaintenanceHold -StatePath $statePath -State $offlineCodeState
if((Read-SegmentedUpdateJson $ValidationHoldPath) -cne $releaseId -or !(Read-SegmentedUpdateJson $statePath).HoldTransferIntentUtc){throw 'Atomic transfer lacks prior durable intent.'};$checks++
Expect-Failure {New-OfflineCodeMaintenanceState -StatePath $statePath -ReleaseId $releaseId -CandidateWebSha256 ('d'*64) -CandidateCompanionSha256 ('e'*64)} 'state-already-exists';$checks++
Assert-OfflineCodeMaintenance -RequireStopped;$checks++
# Execute the real updater stop/start callbacks through its real payload swap.
$swaps=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Invoke-IncrementalApplicationPayloadSwap'},$true))
$swap=@($swaps | Where-Object {$parent=$_.Parent;while($parent -and $parent -isnot [Management.Automation.Language.FunctionDefinitionAst]){$parent=$parent.Parent};!$parent})[0]
function Get-Callback([string]$Name){$elements=$swap.CommandElements;for($i=0;$i -lt $elements.Count;$i++){if($elements[$i] -is [Management.Automation.Language.CommandParameterAst] -and $elements[$i].ParameterName -ceq $Name){return $elements[$i+1].ScriptBlock.GetScriptBlock()}};throw 'Callback missing'}
$stopDefinition=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Stop-HybridIisAfterGpuDrain'},$true))[0]
Invoke-Expression $stopDefinition.Extent.Text
$ApplyResumableSourceDeletionCleanupMigration=$false;$SiteName='DisposableOfflineCode';$ReadinessTimeoutSeconds=10
foreach($parameter in $ast.ParamBlock.Parameters){$name=$parameter.Name.VariablePath.UserPath;if(!(Get-Variable -Name $name -ErrorAction SilentlyContinue)){Set-Variable -Name $name -Value $null}}
$hostingChange=@{Attempted=$false};$deploymentValidation=@{HoldCreated=$true;Baseline='fixture'}
function New-DeploymentValidationHold {param($Path,$ReleaseId);if((Read-SegmentedUpdateJson $Path) -cne $ReleaseId){throw 'Wrong held owner'}}
function Start-IndexCompatibleWebAppPool {param($Name);if($Name -cne $SiteName){throw 'Wrong pool'};$proof.Stopped=$false;throw 'synthetic ambiguous start'}
Expect-Failure {Invoke-IncrementalApplicationPayloadSwap -ApplicationRoot $CanonicalDeployRoot -CandidateRoot $candidate -PreviousRoot $previous -FailedRoot $failed -RestartPreviousApplication $false `
    -StopApplication (Get-Callback 'StopApplication') -ActivateCandidate {Copy-Item -LiteralPath $candidate -Destination $CanonicalDeployRoot -Recurse} `
    -StartApplication (Get-Callback 'StartApplication') `
    -ValidateApplication {throw 'must not validate'} } 'fresh-stop-required'
if((Get-HybridPayloadFingerprint $CanonicalDeployRoot) -cne $offlineCodeState.CandidateWebSha256 -or !(Test-Path -LiteralPath $previous) -or
    (Test-Path -LiteralPath $failed) -or (Read-SegmentedUpdateJson $ValidationHoldPath) -cne $releaseId){throw 'An ambiguous start rolled back payloads or released the hold.'};$checks++
Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'fresh-stop-required';$checks++
Assert-OfflineCodeMaintenance -CandidatePayloads;$checks++
$database.Projection='drift-before-release';Expect-Failure {Assert-OfflineCodeMaintenance -CandidatePayloads} 'database-changed';$checks++
$outer=$swap.Parent;while($outer -and $outer -isnot [Management.Automation.Language.TryStatementAst]){$outer=$outer.Parent}
$outerCatch=[scriptblock]::Create(($outer.CatchClauses[0].Body.Statements.Extent.Text -join "`n"))
$holdReleaseAttempted=$true;$holdReleased=$true;$releaseRoot=$root
Expect-Failure {try{throw 'synthetic post-release failure'}catch{& $outerCatch}} 'Deployment hold release was attempted';$checks++
@{Status='PASS';Checks=$checks;ProductionAccess=$false;Root=$root}|ConvertTo-Json
