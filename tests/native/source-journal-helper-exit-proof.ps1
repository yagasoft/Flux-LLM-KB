[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-access-config.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper.psm1') -Force
$nativeModule=Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper-task.psm1') -Force -PassThru
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-update.psm1') -Force
. (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-iis.ps1')
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Expect-Refusal([scriptblock]$Action){$failed=$false;try{&$Action}catch{$failed=$true};Assert-True $failed 'An unproved process was accepted.'}
function Start-FixtureHelper {
 $f=$global:JournalExitProofFixture
 $info=[Diagnostics.ProcessStartInfo]::new('pwsh');$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true
 foreach($argument in @('-NoProfile','-Command','[Console]::ReadLine() | Out-Null')){$info.ArgumentList.Add($argument)}
 $process=[Diagnostics.Process]::Start($info);$null=$process.SafeHandle
 $created=$process.StartTime.ToUniversalTime();$created=$created.AddTicks(-($created.Ticks%10))
 $f.Processes.Add(@{Process=$process;Created=$created;Stopped=$false})
 $f.Task.Running=$true
}
function global:Get-CimInstance {
 param($ClassName,$Filter,$OperationTimeoutSec,$ErrorAction)
 if($ClassName -cne 'Win32_Process' -or $Filter -cnotin @("Name='FluxKnowledge.Cli.exe'","Name='FluxKnowledge.Cli.exe' OR Name='dotnet.exe'")){throw 'Unexpected fixture process query.'}
 $f=$global:JournalExitProofFixture
 if($null -ne $f.OverrideRows){return $f.OverrideRows}
 foreach($item in $f.Processes){
  if($item.Stopped -and $f.OmitStoppedRows){continue}
  [pscustomobject]@{Name='FluxKnowledge.Cli.exe';ProcessId=$item.Process.Id;CreationDate=$item.Created;
   ExecutablePath=$(if(!$item.Stopped){$f.Task.Executable}else{$null});
   CommandLine=$(if(!$item.Stopped){'"'+$f.Task.Executable+'" '+$f.Task.Arguments}else{$null})}
 }
}
function global:Disable-ScheduledTask {
 param($TaskName,$TaskPath,$ErrorAction)
 if($TaskName -cne $global:JournalExitProofFixture.Task.TaskName -or $TaskPath -cne '\'){throw 'Unexpected fixture task disable.'}
 $global:JournalExitProofFixture.Task.Enabled=$false
}
function global:Stop-ScheduledTask {
 param($TaskName,$TaskPath,$ErrorAction)
 $f=$global:JournalExitProofFixture
 if($TaskName -cne $f.Task.TaskName -or $TaskPath -cne '\'){throw 'Unexpected fixture task stop.'}
 $f.Stops++
 foreach($item in $f.Processes){
  if(!$item.Process.HasExited -and !$f.KeepLive){
   $item.Process.StandardInput.WriteLine('exit');$item.Process.StandardInput.Flush()
   if(!$item.Process.WaitForExit(5000)){throw 'Owned synthetic helper did not exit.'}
  }
  $item.Stopped=$true
 }
 $f.Task.Running=$false
}
function global:Enable-ScheduledTask {
 param($TaskName,$TaskPath,$ErrorAction)
 if($TaskName -cne $global:JournalExitProofFixture.Task.TaskName -or $TaskPath -cne '\'){throw 'Unexpected fixture task enable.'}
 $global:JournalExitProofFixture.Task.Enabled=$true
}
function global:Start-ScheduledTask {
 param($TaskName,$TaskPath,$ErrorAction)
 $f=$global:JournalExitProofFixture
 if($TaskName -cne $f.Task.TaskName -or $TaskPath -cne '\'){throw 'Unexpected fixture task start.'}
 $f.Starts.Add([IO.File]::ReadAllText((Join-Path $f.Root 'payload/FluxKnowledge.Cli.dll')))
 Start-FixtureHelper
}
function Invoke-CimMethod {param($InputObject,$MethodName,$OperationTimeoutSec,$ErrorAction);@{ReturnValue=0;Sid='S-1-5-18'}}
function Invoke-RestMethod {
 param($Uri,$Method,$TimeoutSec)
 Assert-True ($Uri -ceq 'http://127.0.0.1:59999/health/source-journal-helper' -and $Method -ceq 'Get' -and $TimeoutSec -eq 12) 'Unexpected fixture transport target.'
 $global:JournalExitProofFixture.Transports++
 [pscustomobject]@{Version=1;VolumesChecked=1;RepairTransport='NotApproved';CallerSid='S-1-5-21-123'}
}
# Replace only the external Task Scheduler observation. All capture, native
# handle/exit checks, update/rollback, IIS process guards and transport flow run
# unchanged. No scheduled task, production process, SQL or HTTP call is made.
&$nativeModule {
 Set-Item Function:script:Get-SourceJournalHelperTaskObservation -Value {
  param($InstallRoot,$TaskName)
  $f=$global:JournalExitProofFixture
  if($InstallRoot -cne $f.Root -or $TaskName -cne $f.Task.TaskName){throw 'Unexpected fixture task observation.'}
  $f.Task.Clone()
 }
}
$security=[Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
$descriptor=[byte[]]::new($security.BinaryLength);$security.GetBinaryForm($descriptor,0)
$results=[Collections.Generic.List[object]]::new()
foreach($scenario in @('success','rollback')){
 $id=[guid]::NewGuid().ToString('N');$root='I:\FluxKnowledgeSourceAccess-Staging-'+$id
 $state=$null;$ports=$null;$sourceJournalNativePorts=$null
 $lease=[FluxSourceAccessInstallFile]::CreatePrivateDirectory($root,$descriptor);$lease.Dispose()
 $fixture=@{Root=$root;Task=$null;Processes=[Collections.Generic.List[object]]::new();OverrideRows=$null;
  KeepLive=$false;OmitStoppedRows=($scenario -ceq 'success');Stops=0;Starts=[Collections.Generic.List[string]]::new();Transports=0}
 $global:JournalExitProofFixture=$fixture
 try{
  $payload=Join-Path $root 'payload';$input=Join-Path $root 'input';$releaseRoot=Join-Path $root 'release'
  $null=New-Item -ItemType Directory -Path $payload,$input,$releaseRoot
  foreach($directory in @($payload,$input)){
   foreach($name in @('FluxKnowledge.Cli.exe','FluxKnowledge.Cli.dll','FluxKnowledge.Integrations.dll','FluxKnowledge.Cli.deps.json')){
    [IO.File]::WriteAllText((Join-Path $directory $name),($directory -ceq $payload ? 'old' : 'candidate'))
   }
   [IO.File]::WriteAllText((Join-Path $directory 'FluxKnowledge.Cli.runtimeconfig.json'),'{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
  }
  [IO.File]::WriteAllText((Join-Path $root 'policy.json'),'{"Version":1,"IndexerSid":"S-1-5-21-123","PipeName":"FluxKnowledge.SourceAccess.fixture"}')
  [IO.File]::WriteAllText((Join-Path $root 'root-read-plan.json'),'{"Version":1,"fixture":"preserve"}')
  $task=@{TaskName=('FluxKnowledge.SourceAccess-Staging-'+$id);PrincipalSid='S-1-5-18';Executable=(Join-Path $payload 'FluxKnowledge.Cli.exe');
   Arguments=('source-access serve --broker-root "'+$root+'"');WorkingDirectory=$payload;DefinitionSha256=('A'*64);SecuritySha256=('B'*64);Enabled=$true;Running=$true}
  $fixture.Task=$task;Start-FixtureHelper
  $state=New-SourceJournalHelperUpdate -InstallRoot $root -TaskName $task.TaskName -ReleaseId '20261102T160000Z-111111111111' `
   -CandidatePath $input -ExpectedCandidateSha256 (Get-SourceJournalHelperPayloadManifest $input).Sha256 -TaskObservation $task `
   -PipeName 'FluxKnowledge.SourceJournal.fixture' -ApprovedVolumes @('\\?\Volume{11111111-1111-1111-1111-111111111111}\')
  $SourceJournalHelperRoot=$root;$sourceJournalHelperState=$state;$loopbackOrigin=@{Origin='http://127.0.0.1:59999'}
  $releaseId=$state.ReleaseId;$ValidationHoldPath=Join-Path $root 'hold.json'
  [IO.File]::WriteAllText($ValidationHoldPath,($releaseId|ConvertTo-Json -Compress))
  $configuration='{"SourceAccess":{"Enabled":true},"SourceJournal":{"ApprovedVolumes":["synthetic-volume"]}}'
  $config=Join-Path $root 'configuration.json';[IO.File]::WriteAllText($config,$configuration)
  $sourceJournalChange=@{Path=$config;After=[Text.Encoding]::UTF8.GetBytes($configuration);AfterHash=(Get-FileHash -LiteralPath $config).Hash}
  $sourceJournalRelease=@{TransportConfirmed=$false;TransportDeferred=$false;RepairMode=@{ExpectedTransport='NotApproved'}}
  $probe={
   param($candidate)
   # The successful stop saw no stale CIM row. Reintroduce it only after
   # restart to prove that the exit receipt survives disposed native handles.
   $global:JournalExitProofFixture.OmitStoppedRows=$false
   Test-DeploymentSourceJournalHelperNativeState $candidate
   if($candidate){
    Confirm-DeploymentSourceJournalHelperTransport
    if($global:JournalExitProofFixture.Scenario -ceq 'rollback'){throw 'Synthetic post-start probe failure.'}
   }
  }
  $fixture.Scenario=$scenario
  $ports=New-SourceJournalHelperNativeTaskPorts -State $state -AssertApplicationStopped {Assert-DeploymentSourceJournalCliWritersStopped} -Probe $probe
  $sourceJournalNativePorts=$ports
  $failure=$null;try{Invoke-SourceJournalHelperUpdate -State $state -Ports $ports}catch{$failure=$_}
  if($scenario -ceq 'success'){
   if($failure){throw $failure}
   Assert-True ($state.Completed -and $sourceJournalRelease.TransportConfirmed -and $fixture.Transports -eq 1 -and $fixture.Starts.Count -eq 1 -and $fixture.Starts[0] -ceq 'candidate') 'Stale CIM evidence prevented successful held activation.'
  }else{
   Assert-True ($null -ne $failure -and $failure.Exception.Message -ceq 'Synthetic post-start probe failure.' -and $state.RollbackVerified -and
    $fixture.Stops -eq 2 -and $fixture.Starts.Count -eq 2 -and $fixture.Starts[0] -ceq 'candidate' -and $fixture.Starts[1] -ceq 'old') 'Repeated stop lost exit evidence or failed to restore the predecessor.'
  }
  $stale=@(Get-CimInstance Win32_Process -Filter "Name='FluxKnowledge.Cli.exe'")[0]
  Assert-True (&$ports.IsExitedProcess $stale) 'A proved exited incarnation was not retained through restart.'
  $live=@(Get-CimInstance Win32_Process -Filter "Name='FluxKnowledge.Cli.exe'")[-1]
  $invalid=@(
   @{Name='unknown';Row=[pscustomobject]@{Name='FluxKnowledge.Cli.exe';ProcessId=2147480000;CreationDate=$stale.CreationDate;ExecutablePath=$null;CommandLine=$null}},
   @{Name='reused-pid';Row=[pscustomobject]@{Name='FluxKnowledge.Cli.exe';ProcessId=$stale.ProcessId;CreationDate=$stale.CreationDate.AddTicks(10);ExecutablePath=$null;CommandLine=$null}},
   @{Name='missing-creation';Row=[pscustomobject]@{Name='FluxKnowledge.Cli.exe';ProcessId=$stale.ProcessId;CreationDate=$null;ExecutablePath=$null;CommandLine=$null}},
   @{Name='wrong-name';Row=[pscustomobject]@{Name='dotnet.exe';ProcessId=$stale.ProcessId;CreationDate=$stale.CreationDate;ExecutablePath=$null;CommandLine=$null}}
  )
  foreach($invalidCase in $invalid){
   $fixture.OverrideRows=@($invalidCase.Row)
   Expect-Refusal {Get-SourceJournalHelperNativeProcesses -Executable $task.Executable -IsExitedProcess $ports.IsExitedProcess}
   Expect-Refusal {Assert-DeploymentSourceJournalCliWritersStopped}
  }
  $fixture.OverrideRows=@($stale)
  Expect-Refusal {Get-SourceJournalHelperNativeProcesses -Executable $task.Executable}
  $fixture.OverrideRows=$null;$fixture.KeepLive=$true
  &$ports.DisableTask;&$ports.StopTask
  $liveMissing=@(Get-CimInstance Win32_Process -Filter "Name='FluxKnowledge.Cli.exe'")[-1]
  Assert-True (!(&$ports.IsExitedProcess $liveMissing)) 'A captured live native handle manufactured exit proof.'
  Expect-Refusal {Get-SourceJournalHelperNativeProcesses -Executable $task.Executable -IsExitedProcess $ports.IsExitedProcess}
  Expect-Refusal {Assert-DeploymentSourceJournalCliWritersStopped}
  $fixture.KeepLive=$false
  # Cleanup exits only this fixture's pwsh child through its standard input.
  Stop-ScheduledTask -TaskName $task.TaskName -TaskPath '\' -ErrorAction Stop
  &$ports.AssertExited
  Close-SourceJournalHelperNativeTaskPorts $ports
  Assert-True (!(&$ports.IsExitedProcess $stale)) 'Exit proof outlived its update operation.'
  Assert-True ([IO.File]::ReadAllText((Join-Path $root 'root-read-plan.json')) -ceq '{"Version":1,"fixture":"preserve"}') 'Source read grants changed.'
  $results.Add([pscustomobject]@{Scenario=$scenario;Passed=$true;NativeHandleProof=$true;StaleRowsThroughRestart=$true;
   DelayedStaleReappearance=($scenario -ceq 'success');SafetyRefusals=11;ProductionCalls=0})
 }finally{
  if($ports){Close-SourceJournalHelperNativeTaskPorts $ports}
  Close-SourceJournalHelperUpdate $state
  foreach($item in $fixture.Processes){
   if(!$item.Process.HasExited){$item.Process.StandardInput.WriteLine('exit');$item.Process.StandardInput.Flush();$null=$item.Process.WaitForExit(5000)}
   Assert-True $item.Process.HasExited 'Owned fixture process remains live.';$item.Process.Dispose()
  }
  $resolved=[IO.Path]::GetFullPath($root)
  if(!$resolved.StartsWith('I:\FluxKnowledgeSourceAccess-Staging-',[StringComparison]::OrdinalIgnoreCase) -or
   ([IO.File]::GetAttributes($resolved) -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe exit-proof fixture cleanup.'}
  Remove-Item -LiteralPath $resolved -Recurse -Force
 }
}
Remove-Variable JournalExitProofFixture -Scope Global
Remove-Item Function:Get-CimInstance,Function:Disable-ScheduledTask,Function:Stop-ScheduledTask,Function:Enable-ScheduledTask,Function:Start-ScheduledTask
$results|ConvertTo-Json -Depth 4
