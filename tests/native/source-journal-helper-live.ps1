[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$LegacyPayloadPath,
 [Parameter(Mandatory)][string]$LegacyManifestPath,[Parameter(Mandatory)][string]$ExpectedLegacyManifestSha256,
 [Parameter(Mandatory)][string]$CandidatePayloadPath,[Parameter(Mandatory)][string]$CandidateManifestPath,
 [Parameter(Mandatory)][string]$ExpectedCandidateManifestSha256,[Parameter(Mandatory)][string]$EvidenceRoot,
 [ValidateSet('success','post-start-policy-drift','rollback-probe-failure')][string[]]$Scenarios=@('success','post-start-policy-drift','rollback-probe-failure'),
 [switch]$ApplyIsolated)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper-task.psm1') -Force
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Read-FrozenManifest([string]$Root,[string]$Path,[string]$Sha256){
 $Root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
 if(!$Root.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\',[StringComparison]::OrdinalIgnoreCase)){throw 'Qualification payload must use isolated I: staging.'}
 for($part=$Root;$part;$part=[IO.Path]::GetDirectoryName($part)){
  if([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint){throw 'Qualification payload root is redirected.'}
 }
 if($Sha256 -cnotmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $Path).Hash -ine $Sha256){throw 'Qualification manifest drift.'}
 $items=@(Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json)
 $files=@(Get-ChildItem -LiteralPath $Root -Recurse -File -Force)
 if($items.Count -ne $files.Count){throw 'Qualification payload membership drift.'}
 foreach($item in $items){
  $file=[IO.Path]::GetFullPath((Join-Path $Root $item.Path))
  if(!$file.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase) -or
   ([int][IO.File]::GetAttributes($file) -band 0x00441400) -or
   (Get-Item -LiteralPath $file).Length -ne $item.Bytes -or (Get-FileHash -LiteralPath $file).Hash -cne $item.Sha256){throw 'Qualification payload drift.'}
 }
 return ,$items
}
$legacy=Read-FrozenManifest $LegacyPayloadPath $LegacyManifestPath $ExpectedLegacyManifestSha256
$candidate=Read-FrozenManifest $CandidatePayloadPath $CandidateManifestPath $ExpectedCandidateManifestSha256
$evidence=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($EvidenceRoot))
if(!$evidence.StartsWith('I:\Codex Workspaces\Operational Evidence\FluxKnowledge\',[StringComparison]::OrdinalIgnoreCase)){
 throw 'Isolated qualification evidence must remain on the owned I: ground.'
}
if(!$ApplyIsolated){[pscustomobject]@{Mode='PlanOnly';Scenarios=$Scenarios;Sources='synthetic I: roots';Tasks='new staging-prefixed SYSTEM tasks only';
 ProductionCalls=0;Models='no loading or acquisition';LegacyManifestSha256=$ExpectedLegacyManifestSha256;CandidateManifestSha256=$ExpectedCandidateManifestSha256}|ConvertTo-Json;return}
# Do not contaminate controlled measurements with task/payload qualification.
if(@(Get-NetTCPConnection -LocalPort 5149,5150 -State Listen -ErrorAction SilentlyContinue).Count){throw 'Wait for controlled source trials to finish.'}
$null=New-Item -ItemType Directory -Path $evidence -Force
Add-Type -Path (Join-Path $SourceRoot 'tests/native/SourceJournalPipeQualification.cs')
$null=[Reflection.Assembly]::LoadFrom((Join-Path $CandidatePayloadPath 'FluxKnowledge.Application.dll'))
$null=[Reflection.Assembly]::LoadFrom((Join-Path $CandidatePayloadPath 'FluxKnowledge.Integrations.dll'))
$volume=[FluxKnowledge.Integrations.Files.WindowsSourceChangeJournalReader]::CanonicalVolume('I:\')
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$security=[Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
$descriptor=[byte[]]::new($security.BinaryLength);$security.GetBinaryForm($descriptor,0)
foreach($scenario in $Scenarios){
 $id=[guid]::NewGuid().ToString('N');$taskName='FluxKnowledge.SourceAccess-Staging-'+$id
 $root='I:\FluxKnowledgeSourceAccess-Staging-'+$id;$input='I:\FluxKnowledgeSourceAccess-Staging-input-'+$id
 $sourceArea=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('JournalHelperSource-'+$id)
 $source=Join-Path $sourceArea 'Source';$packet=Join-Path $evidence ($scenario+'-'+$id)
 $null=New-Item -ItemType Directory -Path $packet,$source
 $state=$null;$ports=$null;$qualified=$false;$installed=$false
 try{
  $readable=Join-Path $source 'qualification-readable.txt';[IO.File]::WriteAllText($readable,'Synthetic qualification content.')
  $readableAcl=Get-Acl -LiteralPath $readable
  $readableAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),
   [Security.AccessControl.FileSystemRights]0x120089,[Security.AccessControl.AccessControlType]::Allow))
  Set-Acl -LiteralPath $readable -AclObject $readableAcl
  $lease=[FluxSourceAccessInstallFile]::CreatePrivateDirectory($input,$descriptor);$lease.Dispose()
  foreach($item in $candidate){
   $destination=Join-Path $input $item.Path;$null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
   [IO.File]::Copy((Join-Path $CandidatePayloadPath $item.Path),$destination,$false)
  }
  $request=Join-Path $packet 'root-request.json'
  @{RootPath=$source;IndexerSid=$sid;ProtectedRoots=@($root,$input,$packet,$SourceRoot)}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $request -Encoding utf8
  $planText=& (Join-Path $LegacyPayloadPath 'FluxKnowledge.Cli.exe') source-access plan-root-read --request $request
  if($LASTEXITCODE -ne 0){throw 'Synthetic root plan failed.'}
  $rootPlan=Join-Path $packet 'root-read-plan.json';[IO.File]::WriteAllText($rootPlan,($planText -join "`n"))
  $planned=Get-Content -LiteralPath $rootPlan -Raw|ConvertFrom-Json
  $policy=Join-Path $packet 'policy.json';$repairPipe='FluxKnowledge.SourceAccess.staging-'+$id
  @{Version=1;IndexerSid=$sid;PipeName=$repairPipe;Roots=@(@{Path=$source;IdentityFingerprint=$planned.RootIdentityFingerprint;InheritableRead=$true});
   ProtectedRoots=@($planned.ProtectedRoots)}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $policy -Encoding utf8
  $manifest=Join-Path $packet 'payload-manifest.json'
  @{version=1;commit=('a'*40);files=@($legacy|ForEach-Object {@{relativePath=$_.Path;byteLength=$_.Bytes;sha256=$_.Sha256}})}|
   ConvertTo-Json -Depth 6|Set-Content -LiteralPath $manifest -Encoding utf8
  $install=@{PayloadPath=$LegacyPayloadPath;ManifestPath=$manifest;PolicyPath=$policy;RootReadPlanPath=$rootPlan;
   ExpectedManifestSha256=(Get-FileHash -LiteralPath $manifest).Hash;ExpectedPolicySha256=(Get-FileHash -LiteralPath $policy).Hash;
   ExpectedRootReadPlanSha256=(Get-FileHash -LiteralPath $rootPlan).Hash;InstallRoot=$root;TaskName=$taskName}
  & (Join-Path $SourceRoot 'scripts/deploy/install-source-access-broker.ps1') @install -PlanOnly > (Join-Path $packet 'install-plan.json')
  $installed=$true
  & (Join-Path $SourceRoot 'scripts/deploy/install-source-access-broker.ps1') @install -Apply -ConfirmPrivilegedHelper -ConfirmSourceRootReadGrant > (Join-Path $packet 'install-receipt.json')
  $repair=[FluxKnowledge.Integrations.Files.WindowsSourceAccessRepairClient]::new($repairPipe)
  $repairRequest=[FluxKnowledge.Application.Ports.SourceAccessRepairRequest]::new($source,$planned.RootIdentityFingerprint,'qualification-readable.txt',$false)
  $watch=[Diagnostics.Stopwatch]::StartNew()
  do{$outcome=[SourceJournalPipeQualification]::Repair($repair,$repairRequest);if([string]$outcome -ceq 'AlreadyReadable'){break};Start-Sleep -Milliseconds 250}while($watch.Elapsed.TotalSeconds -lt 20)
  Assert-True ([string]$outcome -ceq 'AlreadyReadable') 'Legacy SYSTEM repair transport did not confirm the approved caller/readable file.'
  $original=Get-SourceJournalHelperTaskObservation -InstallRoot $root -TaskName $taskName
  Assert-True ($original.Running -and $original.Enabled) 'The first installed staging task is not running.'
  $nativeRows=@(Get-SourceJournalHelperNativeProcesses $original.Executable)
  Assert-True ($nativeRows.Count -eq 1) 'The staging helper process is ambiguous.'
  $nativeOwner=Invoke-CimMethod -InputObject $nativeRows[0] -MethodName GetOwnerSid -OperationTimeoutSec 5 -ErrorAction Stop
  Assert-True ($nativeOwner.ReturnValue -eq 0 -and $nativeOwner.Sid -ceq 'S-1-5-18' -and
   $nativeRows[0].CommandLine -cin @(('"'+$original.Executable+'" '+$original.Arguments),($original.Executable+' '+$original.Arguments))) 'Native helper command line/SYSTEM identity does not satisfy the canonical writer exemption.'
  [IO.File]::WriteAllText((Join-Path $root 'journal/qualification-sentinel.txt'),'Preserve this synthetic journal sentinel.')
  $options=[FluxKnowledge.Application.Ports.SourceJournalOptions]::new();$options.PipeName='FluxKnowledge.SourceJournal.staging-'+$id;$options.ApprovedVolumes=@($volume)
  $client=[FluxKnowledge.Integrations.Files.SourceJournalPipeClient]::new($options)
  $sim=@{Scenario=$scenario;Probes=0;StoppedChecks=0;NativeQueries=0;NativeReads=0;CandidateTransportConfirmations=0;RollbackTransportConfirmations=0}
  $nativeContext=@{Ports=$null}
  $state=New-SourceJournalHelperUpdate -InstallRoot $root -TaskName $taskName -ReleaseId ('{0:yyyyMMddTHHmmssZ}-60f83b2b958d' -f [DateTime]::UtcNow) `
   -CandidatePath $input -ExpectedCandidateSha256 (Get-SourceJournalHelperPayloadManifest $input).Sha256 -TaskObservation $original `
   -PipeName $options.PipeName -ApprovedVolumes @($volume)
  $stopped={
   $sim.StoppedChecks++
   if(@(Get-NetTCPConnection -LocalPort 5149,5150 -State Listen -ErrorAction SilentlyContinue).Count){throw 'Owned scale staging is running.'}
  }.GetNewClosure()
  $probe={
   param($journal)
   $sim.Probes++;$watch=[Diagnostics.Stopwatch]::StartNew();$last=$null
   do{
    try{
     if($journal){
      $capability=[SourceJournalPipeQualification]::Journal($client,$volume,$null).GetAwaiter().GetResult();$sim.NativeQueries++
      $cursor=[FluxKnowledge.Application.Ports.JournalCursor]::new($volume,$capability.JournalId,$capability.NextUsn)
      $page=[SourceJournalPipeQualification]::Journal($client,$volume,$cursor).GetAwaiter().GetResult();$sim.NativeReads++
      if($page.Start -ne $cursor -or $page.Next.NextUsn -lt $cursor.NextUsn){throw 'SYSTEM journal response changed the requested cursor.'}
     }
     $outcome=[SourceJournalPipeQualification]::Repair($repair,$repairRequest)
     if([string]$outcome -cne 'AlreadyReadable'){throw 'SYSTEM repair transport did not confirm the approved caller/readable file.'}
     $last=$null;break
    }catch{$last=$_;Start-Sleep -Milliseconds 250}
   }while($watch.Elapsed.TotalSeconds -lt 20)
   if($last){throw $last}
   if($journal){$sim.CandidateTransportConfirmations++}else{$sim.RollbackTransportConfirmations++}
   if($journal -and $sim.Scenario -ceq 'post-start-policy-drift'){
    # The running helper pins its policy read-only. Model an external proved
    # stop before an administrator introduces unknown policy; never bypass it.
    &$nativeContext.Ports.DisableTask;&$nativeContext.Ports.StopTask;&$nativeContext.Ports.AssertExited
    [IO.File]::WriteAllText((Join-Path $root 'journal-policy.json'),'{"Version":1,"PipeName":"FluxKnowledge.SourceJournal.newer-policy-sentinel","ApprovedVolumes":[],"RepairEnabled":true}')
    if(![IO.File]::ReadAllText((Join-Path $root 'journal-policy.json')).Contains('newer-policy-sentinel')){throw 'Synthetic policy drift did not persist.'}
    throw 'Synthetic post-start policy drift.'
   }
   if($sim.Scenario -ceq 'rollback-probe-failure'){throw 'Synthetic probe failure after actual transport confirmation.'}
  }.GetNewClosure()
  $ports=New-SourceJournalHelperNativeTaskPorts -State $state -AssertApplicationStopped $stopped -Probe $probe
  $nativeContext.Ports=$ports
  $failure=$null
  try{Invoke-SourceJournalHelperUpdate -State $state -Ports $ports}catch{$failure=$_}
  $after=Get-SourceJournalHelperTaskObservation -InstallRoot $root -TaskName $taskName
  # Retain observations before assertions, including an unexpected recovery.
  [ordered]@{Scenario=$scenario;OriginalTask=$original;FinalTask=$after;Callbacks=$sim;Completed=$state.Completed;
   RollbackVerified=$state.RollbackVerified;Phase=$state.Phase;Failure=$(if($failure){$failure.Exception.Message}else{$null});
   FailureDetails=$(if($failure){$failure.ToString()}else{$null});ObservedAtUtc=[DateTimeOffset]::UtcNow}|
   ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $packet 'observation.json') -Encoding utf8
  Assert-True ($after.DefinitionSha256 -ceq $original.DefinitionSha256 -and $after.SecuritySha256 -ceq $original.SecuritySha256) 'Native task definition/security changed.'
  Assert-True ($sim.NativeQueries -ge 1 -and $sim.NativeReads -ge 1 -and $sim.StoppedChecks -gt 0) 'Native callbacks did not execute after their factory returned.'
  Assert-True ([IO.File]::ReadAllText((Join-Path $root 'journal/qualification-sentinel.txt')) -ceq 'Preserve this synthetic journal sentinel.') 'Repair journal sentinel changed.'
  Assert-True ((Get-FileHash -LiteralPath (Join-Path $root 'policy.json')).Hash -ceq $install.ExpectedPolicySha256 -and
   (Get-FileHash -LiteralPath (Join-Path $root 'root-read-plan.json')).Hash -ceq $install.ExpectedRootReadPlanSha256) 'Source policy or root read plan changed.'
  if($scenario -ceq 'success'){
   Assert-True ($null -eq $failure -and $state.Completed -and $after.Enabled -and $after.Running -and
    $sim.CandidateTransportConfirmations -eq 1 -and $sim.RollbackTransportConfirmations -eq 0) 'Native helper update did not complete.'
  }else{
   Assert-True ($null -ne $failure -and !$state.RollbackVerified -and !$after.Enabled -and !$after.Running) 'Unconfirmed recovery left the helper active.'
   &$ports.AssertExited
   if($scenario -ceq 'post-start-policy-drift'){
    Assert-True (!$state.Completed -and (Get-SourceJournalHelperPayloadManifest $state.PayloadRoot).Sha256 -ceq $state.CandidatePayloadSha256) 'Policy drift did not retain the candidate payload.'
    Assert-True ([IO.File]::ReadAllText((Join-Path $root 'journal-policy.json')).Contains('newer-policy-sentinel')) 'Unknown policy was overwritten.'
    Assert-True ($sim.CandidateTransportConfirmations -eq 1 -and $sim.RollbackTransportConfirmations -eq 0 -and $sim.Probes -eq 1) 'Policy drift reached a predecessor probe.'
    Assert-True ((Get-SourceJournalHelperPayloadManifest $state.PreviousRoot).Sha256 -ceq $state.OriginalPayloadSha256) 'Policy drift lost the inspection-only predecessor.'
    $hold=Join-Path $packet 'hold.json';[IO.File]::WriteAllText($hold,($state.ReleaseId|ConvertTo-Json -Compress))
    $child=Join-Path $packet 'verify-interrupted-recovery.ps1'
    [IO.File]::WriteAllText($child,@'
param($Module,$Hold,$Root,$TaskName)
$ErrorActionPreference='Stop'
Import-Module $Module -Force
if(!(Test-SourceJournalHeldHelperRecovery -HoldPath $Hold -InstallRoot $Root -TaskName $TaskName)){throw 'Fresh process lost the interrupted native-helper boundary.'}
'Fresh-process interrupted native-helper boundary confirmed.'
'@)
    & pwsh -NoProfile -NonInteractive -File $child -Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper.psm1') -Hold $hold -Root $root -TaskName $taskName > (Join-Path $packet 'fresh-process-recovery.log')
    Assert-True ($LASTEXITCODE -eq 0) 'Fresh-process interrupted helper protection failed.'
   }else{
    Assert-True ($sim.CandidateTransportConfirmations -eq 1 -and $sim.RollbackTransportConfirmations -eq 1 -and $sim.Probes -eq 2) 'The injected rollback-probe fault did not follow actual predecessor transport confirmation.'
    Assert-True ((Get-SourceJournalHelperPayloadManifest $state.PayloadRoot).Sha256 -ceq $state.OriginalPayloadSha256 -and
     !(Test-Path -LiteralPath (Join-Path $root 'journal-policy.json'))) 'Rollback-probe failure did not restore the original payload/policy before terminal stop.'
   }
  }
  $qualified=$true
  [ordered]@{Scenario=$scenario;Qualified=$true;OriginalTask=$original;FinalTask=$after;Callbacks=$sim;Completed=$state.Completed;
   FaultScope=$(if($scenario -ceq 'post-start-policy-drift'){'Policy drift after candidate confirmation and external proved stop.'}else{$scenario});
   RollbackVerified=$state.RollbackVerified;ExpectedFailure=$(if($failure){$failure.Exception.Message}else{$null});ProductionCalls=0;ObservedAtUtc=[DateTimeOffset]::UtcNow}|
   ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $packet 'qualification.json') -Encoding utf8
 }finally{
  try{
   if($installed){
    if($null -ne $ports){&$ports.DisableTask;&$ports.StopTask;&$ports.AssertExited}
    else{
     $task=Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
     if($null -ne $task){
      if($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -ine (Join-Path $root 'payload/FluxKnowledge.Cli.exe') -or
       $task.Actions[0].Arguments -cne ('source-access serve --broker-root "'+$root+'"') -or
       $task.Principal.UserId -cnotin @('SYSTEM','S-1-5-18','NT AUTHORITY\SYSTEM')){throw 'Preserve staging task; incomplete installation ownership changed.'}
      $null=Disable-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop
      Stop-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop
      $exitWatch=[Diagnostics.Stopwatch]::StartNew()
      do{
       $remaining=@(Get-SourceJournalHelperNativeProcesses (Join-Path $root 'payload/FluxKnowledge.Cli.exe'))
       $stoppedTask=Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop
       if(!$remaining.Count -and !$stoppedTask.Settings.Enabled -and [string]$stoppedTask.State -cnotin @('Running','Queued')){break};Start-Sleep -Milliseconds 200
      }while($exitWatch.Elapsed.TotalSeconds -lt 15)
      if($remaining.Count -or $stoppedTask.Settings.Enabled -or [string]$stoppedTask.State -cin @('Running','Queued')){throw 'Preserve staging task; native exit unconfirmed.'}
     }
    }
    if($qualified){Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false -ErrorAction Stop}
   }
  }finally{Close-SourceJournalHelperNativeTaskPorts $ports;Close-SourceJournalHelperUpdate $state}
 }
 [pscustomobject]@{Scenario=$scenario;Qualified=$qualified;Evidence=$packet;TaskRemoved=$qualified;Payloads='preserved for inspection';ProductionCalls=0}|ConvertTo-Json
}
