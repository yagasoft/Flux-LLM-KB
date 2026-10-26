[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-access-config.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-update.psm1') -Force
. (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-iis.ps1')
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Expect-Refusal([scriptblock]$Action){$failed=$false;try{&$Action}catch{$failed=$true};Assert-True $failed 'An unsafe release boundary was accepted.'}
$area=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('JournalIisContract-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $area
$CanonicalLiveRoot=$area;$SourceJournalHelperRoot=Join-Path $area 'helper';$InteractiveHostTaskName='isolated';$ReadinessTimeoutSeconds=10
$releaseRoot=Join-Path $area 'release';$releaseId='20261025T120000Z-111111111111';$ValidationHoldPath=Join-Path $area 'hold.json'
$sourceJournalHelperCandidateRoot=Join-Path $area 'candidate-helper';$loopbackOrigin=@{Origin='http://127.0.0.1:59999'}
$roots=@(@{Role='Web';Root=(Join-Path $area 'Web')},@{Role='Cli';Root=(Join-Path $SourceJournalHelperRoot 'payload')},@{Role='OutlookHost';Root=(Join-Path $area 'Outlook')})
$box=@{PoolStopped=$true;CompanionEnabled=$false;Processes=@();Sid='S-1-5-18';HttpCalls=0;Fault='';Task=$null}
function Get-DeploymentSourceJournalPayloads {$roots}
function Assert-StoppedPoolRecoveryBaseline {if(!$box.PoolStopped){throw 'owned pool active'}}
function Get-RepositoryRecoveryTaskObservation {@{Enabled=$box.CompanionEnabled;State='Ready'}}
function Wait-InteractiveHostStopped {param($TaskName,$TimeoutSeconds)}
function Get-CimInstance {param($ClassName,$Filter,$OperationTimeoutSec,$ErrorAction);$box.Processes}
function Invoke-CimMethod {param($InputObject,$MethodName,$OperationTimeoutSec,$ErrorAction);@{ReturnValue=0;Sid=$box.Sid}}
function Get-SourceJournalHelperNativeProcesses {param($Executable);[pscustomobject]@{ProcessId=123}}
function Get-SourceJournalHelperPayloadManifest {param($Path);@{Sha256=(Get-HybridPayloadFingerprint $Path)}}
function Invoke-RestMethod {
 param($Uri,$Method,$TimeoutSec)
 $box.HttpCalls++
 Assert-True ($Uri -ceq ($loopbackOrigin.Origin+'/health/source-journal-helper') -and $Method -ceq 'Get' -and $TimeoutSec -eq 12) 'Unexpected transport target.'
 $result=@{Version=1;VolumesChecked=1;RepairTransport=$sourceJournalRelease.RepairMode.ExpectedTransport;CallerSid='S-1-5-21-123'}
 switch($box.Fault){
  'sid'{$result.CallerSid='wrong'}
  'volume-count'{$result.VolumesChecked=2}
  'repair-outcome'{$result.RepairTransport='AlreadyReadable'}
  'late-hold'{[IO.File]::WriteAllText($ValidationHoldPath,'"another-release"')}
  'late-policy'{[IO.File]::AppendAllText($sourceJournalHelperState.Policy.Path,' ')}
  'late-repair-policy'{[IO.File]::AppendAllText((Join-Path $SourceJournalHelperRoot 'policy.json'),' ')}
  'late-task'{$box.Task.DefinitionSha256='changed'}
 }
 [pscustomobject]$result
}
try{
 $null=New-Item -ItemType Directory -Path $releaseRoot,$sourceJournalHelperCandidateRoot
 foreach($root in $roots){$null=New-Item -ItemType Directory -Path $root.Root;[IO.File]::WriteAllText((Join-Path $root.Root 'entry'),'owned '+$root.Role)}
 [IO.File]::WriteAllText((Join-Path $sourceJournalHelperCandidateRoot 'entry'),'candidate helper')
 $ReviewedSourceJournalPayloads=@($roots|ForEach-Object {$_.Role+'='+(Get-HybridPayloadFingerprint $_.Root)})
 Assert-DeploymentSourceJournalReleasePayloadSet $true
 $saved=@($ReviewedSourceJournalPayloads)
 $ReviewedSourceJournalPayloads=@($saved|ForEach-Object {if($_ -cmatch '^Cli='){'Cli='+(Get-HybridPayloadFingerprint $sourceJournalHelperCandidateRoot)}else{$_}})
 Assert-DeploymentSourceJournalReleasePayloadSet $false
 Expect-Refusal {Assert-DeploymentSourceJournalReleasePayloadSet $true}
 $ReviewedSourceJournalPayloads=$saved
 $config=Join-Path $area 'configuration.json'
 $configuration='{"SourceAccess":{"Enabled":true},"SourceJournal":{"ApprovedVolumes":["synthetic-volume"]}}'
 [IO.File]::WriteAllText($config,$configuration)
 $sourceJournalChange=@{Path=$config;After=[Text.Encoding]::UTF8.GetBytes($configuration);BeforeHash=(Get-FileHash -LiteralPath $config).Hash;
  AfterHash=(Get-FileHash -LiteralPath $config).Hash;Attempted=$true}
 $policyPath=Join-Path $SourceJournalHelperRoot 'policy.json';$planPath=Join-Path $SourceJournalHelperRoot 'root-read-plan.json'
 [IO.File]::WriteAllText($policyPath,'{"IndexerSid":"S-1-5-21-123"}');[IO.File]::WriteAllText($planPath,'{}')
 $journalPath=Join-Path $SourceJournalHelperRoot 'journal-policy.json'
 [IO.File]::WriteAllText($journalPath,'{"RepairEnabled":true}')
 $exe=Join-Path $SourceJournalHelperRoot 'payload/FluxKnowledge.Cli.exe'
 $box.Task=@{Enabled=$true;Running=$true;TaskName='isolated';PrincipalSid='S-1-5-18';Executable=$exe;Arguments=('source-access serve --broker-root "'+$SourceJournalHelperRoot+'"');
  WorkingDirectory=(Split-Path -Parent $exe);DefinitionSha256=('A'*64);SecuritySha256=('B'*64)}
 $sourceJournalHelperState=@{OriginalTask=$box.Task.Clone();PayloadRoot=(Join-Path $SourceJournalHelperRoot 'payload');
  CandidatePayloadSha256=(Get-HybridPayloadFingerprint (Join-Path $SourceJournalHelperRoot 'payload'));
  OriginalPayloadSha256=(Get-HybridPayloadFingerprint (Join-Path $SourceJournalHelperRoot 'payload'));
  RepairPolicySha256=(Get-FileHash -LiteralPath $policyPath).Hash;RootReadPlanSha256=(Get-FileHash -LiteralPath $planPath).Hash;
  Policy=@{Path=$journalPath;AfterHash=(Get-FileHash -LiteralPath $journalPath).Hash}}
 $sourceJournalNativePorts=@{ReadTask={$box.Task}}
 $sourceJournalRelease=@{TransportConfirmed=$false;TransportDeferred=$false;RepairMode=(Get-DeploymentSourceJournalRepairMode)}
 [IO.File]::WriteAllText($ValidationHoldPath,($releaseId|ConvertTo-Json -Compress))
 $allowed=[pscustomobject]@{Name='FluxKnowledge.Cli.exe';ExecutablePath=$exe;CommandLine=('"'+$exe+'" '+$box.Task.Arguments)}
 $box.Processes=@($allowed)
 Assert-DeploymentSourceJournalReleaseStopped
 foreach($mode in @('active-pool','enabled-companion','sql-cli-at-helper-path','dotnet-cli','duplicate-helper','wrong-owner','unknown-command')){
  $box.PoolStopped=$true;$box.CompanionEnabled=$false;$box.Processes=@($allowed);$box.Sid='S-1-5-18'
  switch($mode){
   'active-pool'{$box.PoolStopped=$false}
   'enabled-companion'{$box.CompanionEnabled=$true}
   'sql-cli-at-helper-path'{$box.Processes=@([pscustomobject]@{Name='FluxKnowledge.Cli.exe';ExecutablePath=$exe;CommandLine=('"'+$exe+'" source sync')})}
   'dotnet-cli'{$box.Processes=@([pscustomobject]@{Name='dotnet.exe';ExecutablePath='I:\synthetic\dotnet.exe';CommandLine='dotnet exec "I:\synthetic path\FluxKnowledge.Cli.dll" source sync'})}
   'duplicate-helper'{$box.Processes=@($allowed,$allowed)}
   'wrong-owner'{$box.Sid='S-1-5-21-123'}
   'unknown-command'{$box.Processes=@([pscustomobject]@{Name='FluxKnowledge.Cli.exe';ExecutablePath=$exe;CommandLine=$null})}
  }
  Expect-Refusal {Assert-DeploymentSourceJournalReleaseStopped}
 }
 $box.PoolStopped=$true;$box.CompanionEnabled=$false;$box.Processes=@($allowed);$box.Sid='S-1-5-18'
 Confirm-DeploymentSourceJournalHelperTransport
 Assert-True ($sourceJournalRelease.TransportConfirmed -and (Test-Path -LiteralPath (Join-Path $releaseRoot 'source-journal-transport.json'))) 'Valid held transport did not retain its proof.'
 foreach($fault in @('sid','volume-count','repair-outcome','late-hold','late-policy','late-repair-policy','late-task')){
  $sourceJournalRelease.TransportConfirmed=$false;$box.Fault=$fault
  Expect-Refusal {Confirm-DeploymentSourceJournalHelperTransport}
  Assert-True (!$sourceJournalRelease.TransportConfirmed) 'Unconfirmed held transport acquired a release proof.'
  [IO.File]::WriteAllText($ValidationHoldPath,($releaseId|ConvertTo-Json -Compress));[IO.File]::WriteAllText($journalPath,'{"RepairEnabled":true}')
  [IO.File]::WriteAllText($policyPath,'{"IndexerSid":"S-1-5-21-123"}');$box.Task.DefinitionSha256=('A'*64)
 }
 # Explicitly disabled repair is preserved; enabled-app/disabled-helper fails
 # preflight instead of discovering the mismatch after SQL intent.
 [IO.File]::WriteAllText($journalPath,'{"RepairEnabled":false}')
 Expect-Refusal {Get-DeploymentSourceJournalRepairMode}
 $disabled=$configuration.Replace('"Enabled":true','"Enabled":false')
 [IO.File]::WriteAllText($config,$disabled);$sourceJournalChange.After=[Text.Encoding]::UTF8.GetBytes($disabled)
 $sourceJournalChange.AfterHash=(Get-FileHash -LiteralPath $config).Hash
 $sourceJournalHelperState.Policy.AfterHash=(Get-FileHash -LiteralPath $journalPath).Hash
 $sourceJournalRelease.RepairMode=Get-DeploymentSourceJournalRepairMode;$box.Fault=''
 Confirm-DeploymentSourceJournalHelperTransport
 Assert-True (!$sourceJournalRelease.RepairMode.AppEnabled -and !$sourceJournalRelease.RepairMode.HelperEnabled -and
  $sourceJournalRelease.RepairMode.ExpectedTransport -ceq 'Disabled') 'Disabled repair was silently enabled.'
 [pscustomobject]@{Result='Passed';StopRefusals=7;TransportRefusals=7;RepairModes=2;ActualFiles=$true;ProductionCalls=0;ActualNativeOrSqlCalls=0}|ConvertTo-Json
}finally{
 $resolved=[IO.Path]::GetFullPath($area)
 if(!$resolved.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\JournalIisContract-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe IIS contract cleanup.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
