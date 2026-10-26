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
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-access-config.psm1') -Force
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
$offlineCodeState=$null;$dashboardSnapshotChange=$null;$checks=0
Assert-OfflineCodeMaintenance -RequireStopped -OriginalPayloads;$checks++
$sourceAccessChange=New-SourceAccessConfigurationChange -Path (Join-Path $root 'Config/appsettings.Production.json') -PipeName 'FluxKnowledge.SourceAccess.isolated-offline'
Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'source-access.*version2';$checks++
$helperRoot=Join-Path ([IO.Path]::GetPathRoot($root)) ('FluxKnowledgeSourceAccess-Staging-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($helperRoot)
$helper=@{InstallRoot=$helperRoot;TaskName='FluxKnowledge.SourceAccess-Staging-Fixture';SourceRoot=$root;
    RootIdentityFingerprint=('f'*64);PipeName=$sourceAccessChange.PipeName;IndexerSid='S-1-5-18';
    RootReadRequestPath=(Join-Path $root 'root-read-request.json')}
Write-SegmentedUpdateJson (Join-Path $helperRoot 'policy.json') @{PipeName=$helper.PipeName;IndexerSid=$helper.IndexerSid;
    Roots=@(@{Path=$root;IdentityFingerprint=$helper.RootIdentityFingerprint})}
Write-SegmentedUpdateJson $helper.RootReadRequestPath @{RootPath=$root;IndexerSid=$helper.IndexerSid}
$helper.RootReadRequestSha256=(Get-FileHash -LiteralPath $helper.RootReadRequestPath).Hash
$helper.PolicySha256=(Get-FileHash -LiteralPath (Join-Path $helperRoot 'policy.json')).Hash
foreach($key in @('PayloadSha256','InstallationReceiptSha256','FileSecuritySha256','TaskDefinitionSha256','TaskSecuritySha256')){$helper[$key]='D'*64}
$helperProof=@{Valid=$true;Checks=0;Observation=$helper.Clone();Identity=$helper.RootIdentityFingerprint}
function Get-OfflineSourceAccessHelperObservation {param($Binding)
    $helperProof.Checks++
    $value=$helperProof.Observation.Clone()
    if(!$helperProof.Valid){$value.PayloadSha256='E'*64}
    return $value
}
function Get-OfflineSourceAccessRootInspection {param($Binding) @{RootIdentityFingerprint=$helperProof.Identity}}
$receipt.Version=2
$receipt.SourceAccessConfiguration=@{BeforeSha256=$sourceAccessChange.BeforeHash;AfterSha256=$sourceAccessChange.AfterHash;
    Enabled=$true;PipeName=$sourceAccessChange.PipeName;Helper=$helper}
function Save-FixtureReceipt {
    Write-SegmentedUpdateJson $OfflineCodeMaintenanceReceipt $receipt
    $script:ExpectedOfflineCodeMaintenanceReceiptSha256=(Get-FileHash -LiteralPath $OfflineCodeMaintenanceReceipt).Hash
}
Save-FixtureReceipt
Assert-OfflineCodeMaintenance -RequireStopped -OriginalPayloads;$checks++
$receipt.SourceAccessConfiguration.AfterSha256='e'*64;Save-FixtureReceipt
Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'source-access.*binding';$checks++
$receipt.SourceAccessConfiguration.AfterSha256=$sourceAccessChange.AfterHash
$receipt.SourceAccessConfiguration.PipeName='FluxKnowledge.SourceAccess.unapproved';Save-FixtureReceipt
Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'source-access.*binding';$checks++
$receipt.SourceAccessConfiguration.PipeName=$sourceAccessChange.PipeName;Save-FixtureReceipt
$helperProof.Valid=$false
Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'helper-changed';$helperProof.Valid=$true;$checks++
$helperProof.Identity='changed-root'
Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'root-identity-changed';$helperProof.Identity=$helper.RootIdentityFingerprint;$checks++
foreach($key in @('PolicySha256','InstallationReceiptSha256','FileSecuritySha256','TaskDefinitionSha256','TaskSecuritySha256')) {
    $saved=$helperProof.Observation[$key];$helperProof.Observation[$key]='E'*64
    Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'helper-changed';$helperProof.Observation[$key]=$saved;$checks++
}
Set-SourceAccessConfigurationChange $sourceAccessChange
Assert-OfflineCodeMaintenance -RequireStopped;$checks++
$candidateConfig=[IO.File]::ReadAllBytes($sourceAccessChange.Path)
[IO.File]::WriteAllText($sourceAccessChange.Path,'{"partial":true}')
Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'configuration-changed';$checks++
[IO.File]::WriteAllBytes($sourceAccessChange.Path,$candidateConfig)
Restore-SourceAccessConfigurationChange $sourceAccessChange
Assert-OfflineCodeMaintenance -RequireStopped;$checks++
if($helperProof.Checks -lt 4){throw 'Helper binding was not rechecked across configuration phases.'}
$sourceAccessChange=$null
Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'source-access.*binding';$checks++
$receipt.Remove('SourceAccessConfiguration');$receipt.Version=1;Save-FixtureReceipt
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
$sourceAccessChange=New-SourceAccessConfigurationChange -Path $configurationPath -PipeName $helper.PipeName
$receipt.Version=2
$receipt.SourceAccessConfiguration=@{BeforeSha256=$sourceAccessChange.BeforeHash;AfterSha256=$sourceAccessChange.AfterHash;
    Enabled=$true;PipeName=$sourceAccessChange.PipeName;Helper=$helper}
Save-FixtureReceipt
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
$journalGuard=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Assert-DeploymentSourceJournalPredecessorRestorationAllowed'},$true)
Invoke-Expression $journalGuard.Extent.Text
# Explicit legacy-baseline observation for this offline-code fixture.
function Get-DeploymentSourceJournalBoundary { @{Protected=$false} }
$sourceJournalMode=$false
$ApplyResumableSourceDeletionCleanupMigration=$false;$SiteName='DisposableOfflineCode';$ReadinessTimeoutSeconds=10
foreach($parameter in $ast.ParamBlock.Parameters){$name=$parameter.Name.VariablePath.UserPath;if(!(Get-Variable -Name $name -ErrorAction SilentlyContinue)){Set-Variable -Name $name -Value $null}}
$hostingChange=@{Attempted=$false};$deploymentValidation=@{HoldCreated=$true;Baseline='fixture'}
function New-DeploymentValidationHold {param($Path,$ReleaseId);if((Read-SegmentedUpdateJson $Path) -cne $ReleaseId){throw 'Wrong held owner'}}
function Invoke-CandidatePayloadActivation {param($CandidateRoot,$ApplicationRoot)}
# A failure after the real setter but before start can restore the exact old
# config under the same stopped proof. No start intent or hold release occurs.
Expect-Failure {& (Get-Callback 'ActivateCandidate');throw 'synthetic between-config-and-start'} 'between-config-and-start'
& (Get-Callback 'StopApplication')
& (Get-Callback 'PrepareRollbackApplication')
Assert-SourceAccessConfigurationChange $sourceAccessChange -Original
if($offlineCodeState.StartIntentUtc -or !$proof.Stopped -or (Read-SegmentedUpdateJson $ValidationHoldPath) -cne $releaseId){throw 'Pre-start recovery lost its stopped hold.'};$checks++
$sourceAccessChange=New-SourceAccessConfigurationChange -Path $configurationPath -PipeName $helper.PipeName
function Invoke-CandidatePayloadActivation {param($CandidateRoot,$ApplicationRoot) Copy-Item -LiteralPath $CandidateRoot -Destination $ApplicationRoot -Recurse}
function Start-IndexCompatibleWebAppPool {param($Name);if($Name -cne $SiteName){throw 'Wrong pool'};$proof.Stopped=$false;throw 'synthetic ambiguous start'}
Expect-Failure {Invoke-IncrementalApplicationPayloadSwap -ApplicationRoot $CanonicalDeployRoot -CandidateRoot $candidate -PreviousRoot $previous -FailedRoot $failed -RestartPreviousApplication $false `
    -StopApplication (Get-Callback 'StopApplication') -ActivateCandidate (Get-Callback 'ActivateCandidate') `
    -StartApplication (Get-Callback 'StartApplication') `
    -ValidateApplication {throw 'must not validate'} } 'fresh-stop-required'
if((Get-HybridPayloadFingerprint $CanonicalDeployRoot) -cne $offlineCodeState.CandidateWebSha256 -or !(Test-Path -LiteralPath $previous) -or
    (Test-Path -LiteralPath $failed) -or (Read-SegmentedUpdateJson $ValidationHoldPath) -cne $releaseId){throw 'An ambiguous start rolled back payloads or released the hold.'};$checks++
Assert-SourceAccessConfigurationChange $sourceAccessChange
Expect-Failure {Assert-OfflineCodeMaintenance -RequireStopped} 'fresh-stop-required';$checks++
Assert-OfflineCodeMaintenance -CandidatePayloads;$checks++
$database.Projection='drift-before-release';Expect-Failure {Assert-OfflineCodeMaintenance -CandidatePayloads} 'database-changed';$checks++
$outer=$swap.Parent;while($outer -and $outer -isnot [Management.Automation.Language.TryStatementAst]){$outer=$outer.Parent}
$outerCatch=[scriptblock]::Create(($outer.CatchClauses[0].Body.Statements.Extent.Text -join "`n"))
$holdReleaseAttempted=$true;$holdReleased=$true;$releaseRoot=$root
Expect-Failure {try{throw 'synthetic post-release failure'}catch{& $outerCatch}} 'Deployment hold release was attempted';$checks++
# Only remove this exact disposable helper fixture; no production helper is used.
if([IO.Path]::GetFileName($helperRoot) -cnotmatch '^FluxKnowledgeSourceAccess-Staging-[a-f0-9]{32}$'){throw 'Unsafe fixture cleanup.'}
Remove-Item -LiteralPath $helperRoot -Recurse -Force
@{Status='PASS';Checks=$checks;ProductionAccess=$false;Root=$root}|ConvertTo-Json
