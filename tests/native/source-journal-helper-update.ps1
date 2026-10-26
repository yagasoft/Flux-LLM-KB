[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper.psm1') -Force
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Expect-Refusal([scriptblock]$Action){$refused=$false;try{&$Action}catch{$refused=$true};Assert-True $refused ('Expected refusal: '+$Action.ToString())}
$security=[Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
$descriptor=[byte[]]::new($security.BinaryLength);$security.GetBinaryForm($descriptor,0)
$passed=0
$failures=[Collections.Generic.List[object]]::new()
foreach($scenario in @('success','repair-off','repair-default-off','disabled','probe-failure','start-failure','exit-unproved','candidate-drift','source-policy-drift','task-drift',
 'journal-policy-drift','duplicate-repair','untrusted-policy','disabled-running','post-start-policy-drift','rollback-probe-failure','rollback-start-failure','task-security-drift')){
 $id=[guid]::NewGuid().ToString('N');$root='I:\FluxKnowledgeSourceAccess-Staging-'+$id
 $taskName='FluxKnowledge.SourceAccess-Staging-'+$id;$state=$null
 $created=[FluxSourceAccessInstallFile]::CreatePrivateDirectory($root,$descriptor);$created.Dispose()
 try{
  $live=Join-Path $root 'payload';$input=Join-Path $root 'input';$null=New-Item -ItemType Directory -Path $live,$input,(Join-Path $root 'journal')
  foreach($folder in @($live,$input)){
   foreach($name in @('FluxKnowledge.Cli.exe','FluxKnowledge.Cli.dll','FluxKnowledge.Integrations.dll','FluxKnowledge.Cli.deps.json')){
    [IO.File]::WriteAllText((Join-Path $folder $name),($folder -ceq $live ? 'old' : 'candidate'))
   }
   [IO.File]::WriteAllText((Join-Path $folder 'FluxKnowledge.Cli.runtimeconfig.json'),'{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
  }
  [IO.File]::WriteAllText((Join-Path $root 'policy.json'),'{"Version":1,"IndexerSid":"S-1-5-21-1-2-3-1001","PipeName":"FluxKnowledge.SourceAccess.isolated"}')
  [IO.File]::WriteAllText((Join-Path $root 'root-read-plan.json'),'{"Version":1,"fixture":"preserve-root-grant"}')
  [IO.File]::WriteAllText((Join-Path $root 'journal\owned-intent.json'),'{"fixture":"preserve-repair-intent"}')
  if($scenario -cin @('repair-off','repair-default-off','journal-policy-drift')){
   $policy=[ordered]@{Version=1;PipeName='FluxKnowledge.SourceJournal.previous';ApprovedVolumes=@('\\?\Volume{11111111-1111-1111-1111-111111111111}\')}
   if($scenario -cne 'repair-default-off'){$policy.RepairEnabled=$false}
   [IO.File]::WriteAllText((Join-Path $root 'journal-policy.json'),($policy|ConvertTo-Json -Depth 4))
  }
  if($scenario -ceq 'duplicate-repair'){
   [IO.File]::WriteAllText((Join-Path $root 'journal-policy.json'),'{"Version":1,"PipeName":"FluxKnowledge.SourceJournal.previous","ApprovedVolumes":["\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\"],"RepairEnabled":false,"RepairEnabled":true}')
  }
  if($scenario -ceq 'untrusted-policy'){
   $file=Join-Path $root 'policy.json';$acl=Get-Acl -LiteralPath $file
   $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),
     [Security.AccessControl.FileSystemRights]::WriteData,[Security.AccessControl.AccessControlType]::Allow))
   Set-Acl -LiteralPath $file -AclObject $acl
  }
  $task=@{TaskName=$taskName;PrincipalSid='S-1-5-18';Executable=(Join-Path $live 'FluxKnowledge.Cli.exe');
   Arguments=('source-access serve --broker-root "'+$root+'"');WorkingDirectory=$live;
   DefinitionSha256=('A'*64);SecuritySha256=('B'*64);Enabled=($scenario -cnotin @('disabled','disabled-running'));Running=($scenario -cne 'disabled')}
  $sim=@{Disables=0;Stops=0;Starts=0;Enables=0;Probes=0;StartFault=$false;Task=$task;Scenario=$scenario}
  $arguments=@{InstallRoot=$root;TaskName=$taskName;ReleaseId='20261025T170000Z-6fbd224186ab';CandidatePath=$input;
   ExpectedCandidateSha256=(Get-SourceJournalHelperPayloadManifest $input).Sha256;TaskObservation=$task;
   PipeName='FluxKnowledge.SourceJournal.isolated';ApprovedVolumes=@('\\?\Volume{11111111-1111-1111-1111-111111111111}\')}
  if($scenario -cin @('duplicate-repair','untrusted-policy','disabled-running')){
   $box=@{State=$null}
   try{Expect-Refusal {$box.State=New-SourceJournalHelperUpdate @arguments}}finally{Close-SourceJournalHelperUpdate $box.State}
   Assert-True (!(Test-Path -LiteralPath (Join-Path $root 'updates'))) 'Unreviewed policy/task state reached protected preparation writes.'
   $passed++;continue
  }
  $state=New-SourceJournalHelperUpdate @arguments
  $ports=@{
   ReadTask={$sim.Task.Clone()}
   AssertApplicationStopped={}
   DisableTask={$sim.Disables++;$sim.Task.Enabled=$false}
   StopTask={$sim.Stops++;if($sim.Scenario -cne 'exit-unproved'){$sim.Task.Running=$false}
    if($sim.Scenario -ceq 'journal-policy-drift' -and $sim.Stops -eq 1){
     [IO.File]::WriteAllText((Join-Path $root 'journal-policy.json'),'{"Version":1,"PipeName":"FluxKnowledge.SourceJournal.newer-policy-sentinel","ApprovedVolumes":[],"RepairEnabled":true}')
    }
   }
   AssertExited={if($sim.Scenario -ceq 'exit-unproved'){throw 'owned process still alive'}}
   EnableTask={$sim.Enables++;$sim.Task.Enabled=$true}
   StartTask={
    $sim.Starts++;$sim.Task.Running=$true
    if($sim.Scenario -ceq 'start-failure' -and !$sim.StartFault){$sim.StartFault=$true;throw 'owned ambiguous start'}
    if($sim.Scenario -ceq 'rollback-start-failure' -and $sim.Starts -eq 2){throw 'owned ambiguous predecessor start'}
   }
   Probe={param($journal);$sim.Probes++
    if($journal -and $sim.Scenario -ceq 'post-start-policy-drift'){
     [IO.File]::WriteAllText((Join-Path $root 'journal-policy.json'),'{"Version":1,"PipeName":"FluxKnowledge.SourceJournal.newer-policy-sentinel","ApprovedVolumes":[],"RepairEnabled":true}')
     throw 'owned post-start policy drift'
    }
    if($journal -and $sim.Scenario -cin @('probe-failure','rollback-probe-failure','rollback-start-failure')){throw 'owned journal transport refusal'}
    if(!$journal -and $sim.Scenario -ceq 'rollback-probe-failure'){throw 'owned predecessor probe refusal'}
    Assert-True ($sim.Task.Running) 'Probe ran without a live task.'
    Assert-True ([IO.File]::ReadAllText((Join-Path $live 'FluxKnowledge.Cli.dll')) -ceq ($journal ? 'candidate' : 'old')) 'Probe observed the wrong payload.'
   }
  }
  switch($scenario){
   'candidate-drift'{[IO.File]::AppendAllText((Join-Path $state.CandidateRoot 'FluxKnowledge.Cli.dll'),'changed')}
   'source-policy-drift'{[IO.File]::AppendAllText((Join-Path $root 'policy.json'),'changed')}
   'task-drift'{$sim.Task.DefinitionSha256='F'*64}
   'task-security-drift'{$sim.Task.SecuritySha256='F'*64}
  }
  if($scenario -cin @('success','repair-off','repair-default-off','disabled')){
   Invoke-SourceJournalHelperUpdate -State $state -Ports $ports
   Assert-True $state.Completed 'Qualified replacement did not complete.'
   $policy=Get-Content -LiteralPath (Join-Path $root 'journal-policy.json') -Raw|ConvertFrom-Json
   Assert-True ($policy.RepairEnabled -eq ($scenario -cnotin @('repair-off','repair-default-off'))) 'Repair availability changed.'
   Assert-True ($sim.Task.Enabled -eq ($scenario -cne 'disabled') -and $sim.Task.Running -eq ($scenario -cne 'disabled')) 'Original enabled/running state changed.'
   Assert-True ([IO.File]::ReadAllText((Join-Path $live 'FluxKnowledge.Cli.dll')) -ceq 'candidate') 'The new payload was not activated.'
   Expect-Refusal {Invoke-SourceJournalHelperUpdate -State $state -Ports $ports}
  }else{
   Expect-Refusal {Invoke-SourceJournalHelperUpdate -State $state -Ports $ports}
   if($scenario -cin @('candidate-drift','source-policy-drift','task-drift','task-security-drift')){
    Assert-True ($sim.Disables -eq 0 -and $sim.Starts -eq 0 -and !$state.Attempted) 'Preflight drift reached task mutation.'
   }elseif($scenario -ceq 'post-start-policy-drift'){
    Assert-True (!$state.RollbackVerified -and $sim.Starts -eq 1 -and !$sim.Task.Enabled -and !$sim.Task.Running) 'Post-start drift left a SYSTEM helper running or restarted the predecessor.'
    Assert-True ([IO.File]::ReadAllText((Join-Path $root 'journal-policy.json')).Contains('newer-policy-sentinel')) 'Post-start policy drift was overwritten.'
   }elseif($scenario -cin @('rollback-probe-failure','rollback-start-failure')){
    Assert-True (!$state.RollbackVerified -and $sim.Starts -eq 2 -and !$sim.Task.Enabled -and !$sim.Task.Running) 'Unconfirmed predecessor recovery left a running/enabled helper.'
   }elseif($scenario -ceq 'journal-policy-drift'){
    Assert-True (!$state.RollbackVerified -and $sim.Starts -eq 0 -and !$sim.Task.Enabled) 'Unknown journal policy reached a predecessor restart.'
    Assert-True ([IO.File]::ReadAllText((Join-Path $root 'journal-policy.json')).Contains('newer-policy-sentinel')) 'Newer journal policy was overwritten.'
   }elseif($scenario -ceq 'exit-unproved'){
    Assert-True (!$state.OldPayloadMoved -and $sim.Starts -eq 0 -and !$sim.Task.Enabled) 'Unproved native exit reached payload replacement or restart.'
   }else{
    Assert-True ($state.RollbackVerified -and $sim.Task.Enabled -and $sim.Task.Running) 'Proved recovery failed to restore the original task state.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $root 'journal-policy.json'))) 'A newly introduced journal policy survived verified rollback.'
   }
   if($scenario -ceq 'post-start-policy-drift'){
    Assert-True ([IO.File]::ReadAllText((Join-Path $state.PreviousRoot 'FluxKnowledge.Cli.dll')) -ceq 'old') 'Unconfirmed recovery lost the inspection-only predecessor.'
   }else{Assert-True ([IO.File]::ReadAllText((Join-Path $live 'FluxKnowledge.Cli.dll')) -ceq 'old') 'A failed update lost the predecessor payload.'}
  }
  Assert-True ([IO.File]::ReadAllText((Join-Path $root 'root-read-plan.json')) -ceq '{"Version":1,"fixture":"preserve-root-grant"}') 'Root read grants were rewritten.'
  Assert-True ([IO.File]::ReadAllText((Join-Path $root 'journal\owned-intent.json')) -ceq '{"fixture":"preserve-repair-intent"}') 'Repair journal intents were changed.'
  $passed++
 }catch{$failures.Add([pscustomobject]@{Scenario=$scenario;Message=$_.Exception.Message})}finally{
  Close-SourceJournalHelperUpdate $state
  $resolved=[IO.Path]::GetFullPath($root)
  if(!$resolved.StartsWith('I:\FluxKnowledgeSourceAccess-Staging-',[StringComparison]::OrdinalIgnoreCase) -or
   ([IO.File]::GetAttributes($resolved) -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe helper fixture cleanup.'}
  Remove-Item -LiteralPath $resolved -Recurse -Force
 }
}
if($failures.Count){$failures|ConvertTo-Json -Depth 4;throw ('Helper scenarios failed: '+$failures.Count+'; passed: '+$passed)}
[pscustomobject]@{Passed=$passed;ActualNamespaceAndPayloadOperations=$true;TaskPorts='synthetic; no task registered or process launched';ProductionCalls=0}|ConvertTo-Json
