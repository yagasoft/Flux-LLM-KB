[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-migration.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-access-config.psm1') -Force
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Expect-Refusal([scriptblock]$Action){$refused=$false;try{&$Action}catch{$refused=$true};Assert-True $refused 'An unreviewed installed set reached an action.'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Canonical updater parse failed.'}
foreach($name in @('Invoke-DeploymentIndexAdmission','Get-DeploymentSourceJournalBoundary','Get-DeploymentSourceJournalPayloads',
 'Invoke-DeploymentSourceJournalAdmission','Start-IndexCompatibleWebAppPool','Enable-IndexCompatibleOutlookTask','Remove-DeploymentValidationHold')){
 $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$false))
 if($nodes.Count -ne 1){throw ('Missing canonical boundary: '+$name)}
 Invoke-Expression $nodes[0].Extent.Text
}
$contract=Get-SourceJournalMigrationContract
$base=@(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations') -Filter '*.cs' -File |
 ForEach-Object {[regex]::Matches([IO.File]::ReadAllText($_.FullName),'\[Migration\("(\d{14}_[^"]+)"\)\]')|ForEach-Object {$_.Groups[1].Value}}|
 Where-Object {$_ -cle $contract.Baseline}|Sort-Object)
$baseline=[pscustomobject]@{Identity='owned-incarnation';History=$base;SchemaSha256=$contract.BaselineSchemaSha256}
$complete=[pscustomobject]@{Identity=$baseline.Identity;History=($base+@($contract.Migrations));SchemaSha256=$contract.SchemaSha256}
$area=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('JournalStartAdmission-'+[guid]::NewGuid().ToString('N'))
$CanonicalLiveRoot=$area;$CanonicalDeployRoot=Join-Path $area 'Web';$InteractiveHostRoot=Join-Path $area 'OutlookHost'
$SourceJournalHelperRoot=Join-Path $area 'helper';$IncrementalRecoveryRoot=Join-Path $area 'Recovery';$ValidationHoldPath=Join-Path $area 'hold.json'
$sourceJournalMigrationState=$null;$sourceJournalHelperState=$null;$ExpectedSourceJournalDatabaseIdentity=$baseline.Identity;$ReviewedLegacyIndexPayloads=@()
$DisableIncrementalSourceScanning=$false;$sourceJournalRelease=@{RetirementConfirmed=$false};$sourceJournalChange=$null
$box=@{Database=$baseline;Actions=0;IndexFences=0;Intent='';Fingerprint=@{};Reads=0;DriftAt=0;HeldHelperRecovery=$false}
function Get-DeploymentSqlConnectionString {'synthetic-no-connection'}
function Get-SourceJournalDatabaseState {param($ConnectionString);$box.Reads++;if($box.DriftAt -eq $box.Reads){return [pscustomobject]@{Identity='changed-incarnation';History=$complete.History;SchemaSha256=$complete.SchemaSha256}};$box.Database}
function Get-SourceJournalHeldMigrationIntent {param($HoldPath,$RecoveryRoot,$ExpectedDatabaseIdentity);$box.Intent}
function Test-SourceJournalHeldHelperRecovery {param($HoldPath,$InstallRoot);$box.HeldHelperRecovery}
function Get-HybridPayloadFingerprint {param($Path);$box.Fingerprint[$Path]}
function Invoke-IndexFormatAdmission {param($ConnectionString,$Payloads,$ReadPayloadFingerprint,$ReviewedLegacyPayloads,$WebIndexMode,$Action);$box.IndexFences++;&$Action}
function Start-WebAppPool {param($Name);Assert-True ($Name -ceq 'isolated') 'Wrong pool';$box.Actions++}
function Enable-ScheduledTask {param($TaskName,$TaskPath,$ErrorAction);Assert-True ($TaskName -ceq 'isolated') 'Wrong task';$box.Actions++}
try{
 $null=New-Item -ItemType Directory -Path (Join-Path $area 'Config')
 [IO.File]::WriteAllText((Join-Path $area 'Config/appsettings.Production.json'),'{}')
 $ReviewedSourceJournalPayloads=@(foreach($payload in @(Get-DeploymentSourceJournalPayloads)){
  $null=New-Item -ItemType Directory -Path $payload.Root
  $entry=Join-Path $payload.Root ('FluxKnowledge.'+$payload.Role+'.dll');[IO.File]::WriteAllText($entry,'owned '+$payload.Role)
  $sha=(Get-FileHash -LiteralPath $entry).Hash;$box.Fingerprint[$payload.Root]=$sha
  $payload.Role+'='+$sha
 })
 foreach($feature in @($false,$true)){
  $box.Database=if($feature){$complete}else{$baseline};$before=$box.Actions
  Start-IndexCompatibleWebAppPool -Name isolated
  Enable-IndexCompatibleOutlookTask -TaskName isolated
  [IO.File]::WriteAllText($ValidationHoldPath,'"owned-release"')
  Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId owned-release
  Assert-True ($box.Actions-$before -eq 2 -and !(Test-Path -LiteralPath $ValidationHoldPath)) 'A valid installed set did not traverse every action boundary.'
 }
 $box.Database=$complete;$before=$box.Actions
 foreach($case in @('no-review','wrong-cli','wrong-database','schema-drift','late-database-drift')){
  $saved=@($ReviewedSourceJournalPayloads);$savedIdentity=$ExpectedSourceJournalDatabaseIdentity;$box.Reads=0;$box.DriftAt=0
  switch($case){
   'no-review'{$ReviewedSourceJournalPayloads=@()}
   'wrong-cli'{$ReviewedSourceJournalPayloads=@($saved|ForEach-Object {if($_ -cmatch '^Cli='){'Cli='+('F'*64)}else{$_}})}
   'wrong-database'{$ExpectedSourceJournalDatabaseIdentity='unreviewed'}
   'schema-drift'{$box.Database=[pscustomobject]@{Identity=$complete.Identity;History=$complete.History;SchemaSha256=('F'*64)}}
   'late-database-drift'{$box.DriftAt=3}
  }
  Expect-Refusal {Start-IndexCompatibleWebAppPool -Name isolated}
  $ReviewedSourceJournalPayloads=$saved;$ExpectedSourceJournalDatabaseIdentity=$savedIdentity;$box.Database=$complete;$box.DriftAt=0
 }
 $box.Database=$baseline;$box.Intent=Join-Path $area 'durable-intent.json';[IO.File]::WriteAllText($box.Intent,'{}')
 Expect-Refusal {Enable-IndexCompatibleOutlookTask -TaskName isolated}
 [IO.File]::WriteAllText($ValidationHoldPath,'"owned-release"')
 Expect-Refusal {Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId owned-release}
 Assert-True (Test-Path -LiteralPath $ValidationHoldPath) 'A recovered incomplete migration released its hold.'
 $box.Intent=''
 [IO.File]::WriteAllText((Join-Path $area 'Config/appsettings.Production.json'),'{"SourceJournal":{"Enabled":true,"SealingEnabled":false}}')
 Expect-Refusal {Start-IndexCompatibleWebAppPool -Name isolated}
 [IO.File]::WriteAllText((Join-Path $area 'Config/appsettings.Production.json'),'{"SourceJournal":{"Enabled":"true"}}')
 Expect-Refusal {Start-IndexCompatibleWebAppPool -Name isolated}
 [IO.File]::WriteAllText((Join-Path $area 'Config/appsettings.Production.json'),'{}')
 $sourceJournalHelperState=@{Attempted=$true;Completed=$false;RollbackVerified=$false}
 Expect-Refusal {Start-IndexCompatibleWebAppPool -Name isolated}
 $sourceJournalHelperState=$null
 $box.HeldHelperRecovery=$true
 Expect-Refusal {Start-IndexCompatibleWebAppPool -Name isolated}
 $box.HeldHelperRecovery=$false
 Assert-True ($box.Actions -eq $before) 'A refused boundary performed a start/enable action.'
 # Complete schema and exact candidate hashes must not conceal helper recovery.
 $box.Database=$complete;$recoveryChecks=0
 $actions=@({Start-IndexCompatibleWebAppPool -Name isolated},{Enable-IndexCompatibleOutlookTask -TaskName isolated},
  {Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId owned-release})
 foreach($mode in @('in-memory','persisted')){
  $sourceJournalHelperState=if($mode -ceq 'in-memory'){@{Attempted=$true;Completed=$false;RollbackVerified=$false}}else{$null}
  $box.HeldHelperRecovery=$mode -ceq 'persisted'
  foreach($action in $actions){
   [IO.File]::WriteAllText($ValidationHoldPath,'"owned-release"')
   Expect-Refusal $action
   Assert-True ($box.Actions -eq $before -and (Test-Path -LiteralPath $ValidationHoldPath)) 'Complete schema allowed unresolved helper recovery to activate or release.'
   $recoveryChecks++
  }
 }
 $sourceJournalHelperState=$null;$box.HeldHelperRecovery=$true;$DisableIncrementalSourceScanning=$true
 $config=Join-Path $area 'Config/appsettings.Production.json'
 $sourceJournalChange=New-SourceJournalConfigurationChange -Path $config -Disable
 Set-SourceAccessConfigurationChange $sourceJournalChange
 foreach($action in $actions){Expect-Refusal $action;$recoveryChecks++}
 $sourceJournalRelease.RetirementConfirmed=$true
 [IO.File]::WriteAllText($config,'{"SourceJournal":{"Enabled":false,"SealingEnabled":true}}')
 Expect-Refusal $actions[0];$recoveryChecks++
 [IO.File]::WriteAllBytes($config,$sourceJournalChange.After);[IO.File]::AppendAllText($config,' ')
 Expect-Refusal $actions[0];$recoveryChecks++
 [IO.File]::WriteAllBytes($config,$sourceJournalChange.After)
 $ExpectedSourceJournalDatabaseIdentity='unreviewed'
 Expect-Refusal $actions[0];$recoveryChecks++;$ExpectedSourceJournalDatabaseIdentity=$complete.Identity
 $saved=@($ReviewedSourceJournalPayloads)
 $ReviewedSourceJournalPayloads=@($saved|ForEach-Object {if($_ -cmatch '^Cli='){'Cli='+('F'*64)}else{$_}})
 Expect-Refusal $actions[0];$recoveryChecks++;$ReviewedSourceJournalPayloads=$saved
 Assert-True ($box.Actions -eq $before -and (Test-Path -LiteralPath $ValidationHoldPath)) 'Incomplete feature-off proof reached an action.'
 foreach($action in $actions){&$action}
 Assert-True ($box.Actions-$before -eq 2 -and !(Test-Path -LiteralPath $ValidationHoldPath)) 'Bound completed retirement could not release an exact feature-off candidate.'
 $recoveryChecks++
 [pscustomobject]@{Passed=(13+$recoveryChecks);HelperRecoveryChecks=$recoveryChecks;IndexFenceCalls=$box.IndexFences;ActualFiles=$true;ProductionCalls=0;SqlCalls=0}|ConvertTo-Json
}finally{
 $resolved=[IO.Path]::GetFullPath($area)
 if(!$resolved.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\JournalStartAdmission-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe admission cleanup.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
