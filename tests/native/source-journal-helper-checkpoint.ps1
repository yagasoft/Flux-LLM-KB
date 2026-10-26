[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper.psm1') -Force
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Expect-Refusal([scriptblock]$Action){$failed=$false;try{&$Action}catch{$failed=$true};Assert-True $failed 'Unsafe helper checkpoint was accepted.'}
$id=[guid]::NewGuid().ToString('N');$root='I:\FluxKnowledgeSourceAccess-Staging-'+$id
$task='FluxKnowledge.SourceAccess-Staging-'+$id
$area=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('JournalHelperCheckpoint-'+$id)
$null=New-Item -ItemType Directory -Path $area
$security=[Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
$descriptor=[byte[]]::new($security.BinaryLength);$security.GetBinaryForm($descriptor,0)
$lease=[FluxSourceAccessInstallFile]::CreatePrivateDirectory($root,$descriptor);$lease.Dispose()
$releaseId='20261025T190000Z-111111111111';$hold=Join-Path $area 'hold.json'
function Read-Recovery {Test-SourceJournalHeldHelperRecovery -HoldPath $hold -InstallRoot $root -TaskName $task}
try{
 Assert-True (!(Read-Recovery)) 'An absent hold invented recovery.'
 [IO.File]::WriteAllText($hold,($releaseId|ConvertTo-Json -Compress))
 Assert-True (!(Read-Recovery)) 'A missing helper release invented recovery.'
 $updates=Join-Path $root 'updates';$release=Join-Path $updates $releaseId
 $null=New-Item -ItemType Directory -Path $updates,$release
 Expect-Refusal {Read-Recovery}
 $path=Join-Path $release 'state.json'
 $checkpoint=[ordered]@{Version=1;ReleaseId=$releaseId;InstallRoot=$root;Phase='Prepared';
  OriginalPayloadSha256=('A'*64);CandidatePayloadSha256=('B'*64);RepairPolicySha256=('C'*64);RootReadPlanSha256=('D'*64);JournalPolicySha256=('E'*64);
  OriginalJournalPolicySha256='';ObservedAtUtc=[DateTimeOffset]::UtcNow;
  Task=@{TaskName=$task;PrincipalSid='S-1-5-18';Executable=(Join-Path $root 'payload/FluxKnowledge.Cli.exe');
   Arguments=('source-access serve --broker-root "'+$root+'"');WorkingDirectory=(Join-Path $root 'payload');
   Enabled=$true;Running=$true;DefinitionSha256=('F'*64);SecuritySha256=('A'*64)}}
 foreach($phase in @('Prepared','StopIntent','Stopped','StartIntent','Completed','RolledBack')){
  $checkpoint.Phase=$phase;[IO.File]::WriteAllText($path,($checkpoint|ConvertTo-Json -Depth 6))
  Assert-True ((Read-Recovery) -eq ($phase -cin @('StopIntent','Stopped','StartIntent'))) ('Incorrect recovery classification: '+$phase)
 }
 foreach($fault in @('release','root','task','principal','phase','hash','version','duplicate','truncated')){
  $value=$checkpoint|ConvertTo-Json -Depth 6|ConvertFrom-Json -AsHashtable;$value.Phase='StartIntent'
  switch($fault){
   'release'{$value.ReleaseId='different'}
   'root'{$value.InstallRoot=$area}
   'task'{$value.Task.TaskName='different'}
   'principal'{$value.Task.PrincipalSid='S-1-5-21-123'}
   'phase'{$value.Phase='unknown'}
   'hash'{$value.JournalPolicySha256='bad'}
   'version'{$value.Version='1'}
  }
  $text=$value|ConvertTo-Json -Depth 6
  if($fault -ceq 'duplicate'){$text=$text.Replace('"Phase": "StartIntent"','"Phase": "Prepared", "Phase": "StartIntent"')}
  if($fault -ceq 'truncated'){$text='{'}
  [IO.File]::WriteAllText($path,$text)
  Expect-Refusal {Read-Recovery}
 }
 $checkpoint.Phase='StartIntent';[IO.File]::WriteAllText($path,($checkpoint|ConvertTo-Json -Depth 6))
 [IO.File]::WriteAllText($hold,(@{version=1;releaseId=$releaseId;corpusRebuildOperationId=[guid]::NewGuid().ToString('D')}|ConvertTo-Json -Compress))
 Assert-True (Read-Recovery) 'Existing scoped hold shape lost interrupted helper recovery.'
 [IO.File]::WriteAllText($hold,($releaseId|ConvertTo-Json -Compress))
 $locked=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
 try{Expect-Refusal {Read-Recovery}}finally{$locked.Dispose()}
 $child=Join-Path $area 'fresh-process.ps1'
 [IO.File]::WriteAllText($child,@'
param($Module,$Hold,$Root,$TaskName)
$ErrorActionPreference='Stop'
Import-Module $Module -Force
if(!(Test-SourceJournalHeldHelperRecovery -HoldPath $Hold -InstallRoot $Root -TaskName $TaskName)){throw 'Fresh process lost interrupted helper protection.'}
'Fresh-process interrupted-helper recovery confirmed.'
'@)
 & pwsh -NoProfile -NonInteractive -File $child -Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper.psm1') -Hold $hold -Root $root -TaskName $task
 Assert-True ($LASTEXITCODE -eq 0) 'Fresh-process recovery proof failed.'
 [pscustomobject]@{Result='Passed';Phases=6;MalformedRefusals=9;LockedAndMissingRefused=$true;FreshProcess=$true;ProductionCalls=0}|ConvertTo-Json
}finally{
 foreach($owned in @($root,$area)){
  $resolved=[IO.Path]::GetFullPath($owned)
  if(!$resolved.StartsWith('I:\FluxKnowledgeSourceAccess-Staging-',[StringComparison]::OrdinalIgnoreCase) -and
   !$resolved.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\JournalHelperCheckpoint-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe helper checkpoint cleanup.'}
  Remove-Item -LiteralPath $resolved -Recurse -Force
 }
}
