[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-deletion-pending-queue-index.psm1') -Force
function Check([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Refuse([scriptblock]$Action){$failed=$false;try{&$Action}catch{$failed=$true};Check $failed 'Expected refusal.'}
$contract=Get-SourceDeletionPendingQueueContract
$history=@(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations') -Filter '*.cs' -File|
 ForEach-Object{[regex]::Matches([IO.File]::ReadAllText($_.FullName),'\[Migration\("(\d{14}_[^"]+)"\)\]')|ForEach-Object{$_.Groups[1].Value}}|Where-Object {$_ -cle '20261101195325_AddVectorPublicationMetadata'}|Sort-Object -Unique)
$old=[pscustomobject]@{Identity='owned';History=$history;CanAlter=$true;ResolvesDbo=$true;IndexSha256=$contract.AbsentIndexSha256}
$new=[pscustomobject]@{Identity='owned';History=$history;CanAlter=$false;ResolvesDbo=$true;IndexSha256=$contract.IndexSha256}
Check ((Get-SourceDeletionPendingQueueStateKind $old 'owned') -ceq 'Old') 'Absent index classification failed.'
Check ((Get-SourceDeletionPendingQueueStateKind $new 'owned') -ceq 'New') 'Installed index classification failed.'
foreach($mutation in @({$args[0].Identity='other'},{$args[0].History=$history[1..78]},{$args[0].History=$history+'20990101000000_Unexpected'},{$args[0].ResolvesDbo=$false},{$args[0].IndexSha256='F'*64})){
 $bad=$old.PSObject.Copy();&$mutation $bad;Refuse {Get-SourceDeletionPendingQueueStateKind $bad 'owned'}
}
$area=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('PendingQueueIndexContract-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $area
try {
 foreach($scenario in @('Success','AlreadyNew','RolledBack','LostAck','Unknown','Drift')){
  $directory=Join-Path $area $scenario;$null=New-Item -ItemType Directory -Path $directory
  $initial=if($scenario -ceq 'AlreadyNew'){$new}else{$old}
  $case=@{Current=$initial;Calls=0;Stopped=0;Reads=0}
  $state=New-SourceDeletionPendingQueueState -DatabaseState $initial -ReceiptDirectory $directory -ReleaseId 'owned-release' -Commit ('a'*40) -SqlSha256 $contract.UpSha256
  $read={$case.Reads++;if($scenario -ceq 'Unknown' -and $case.Calls){throw 'unavailable'};$case.Current}
  $run={
   $case.Calls++;Check (Test-Path -LiteralPath $state.IntentPath) 'DDL preceded durable intent.'
   if($scenario -ceq 'RolledBack' -or $scenario -ceq 'Unknown'){throw 'DDL failed'}
   $case.Current=$new;if($scenario -ceq 'LostAck'){throw 'lost acknowledgement'}
  }
  if($scenario -ceq 'Drift'){$case.Current=$bad}
  $attempt={Invoke-SourceDeletionPendingQueueAttempt -State $state -ReadState $read -AssertStopped {$case.Stopped++} -RunUp $run}
  if($scenario -in @('RolledBack','LostAck','Unknown','Drift')){Refuse $attempt}else{&$attempt}
  if($scenario -ceq 'Drift'){Check (!$state.Attempted -and $case.Calls -eq 0) 'Drift reached DDL.';continue}
  Check ($state.Attempted -and $case.Stopped -eq 1) 'Stopped proof/attempt missing.'
  Check ($case.Calls -eq $(if($scenario -ceq 'AlreadyNew'){0}else{1})) 'Unexpected DDL count.'
  $expected=if($scenario -ceq 'Unknown'){'Unknown'}elseif($scenario -ceq 'RolledBack'){'Old'}else{'New'}
  Check ($state.SettledState -ceq $expected) 'Commit outcome was not reconciled.'
  Refuse $attempt
 }
 $badSql=Join-Path $area 'wrong.sql';[IO.File]::WriteAllText($badSql,'SELECT 1;')
 Refuse {Invoke-SourceDeletionPendingQueueSql -ConnectionString 'not-a-connection' -SqlPath $badSql -ExpectedDatabaseIdentity 'owned'}
 Check ((Get-FileHash -LiteralPath (Join-Path $SourceRoot 'scripts/deploy/source-deletion-pending-queue-index.sql')).Hash -ceq $contract.UpSha256) 'Declaration bytes changed.'
 Write-Output 'Pending queue index exact-state, intent and unknown-outcome contract passed.'
} finally {
 if([IO.Path]::GetFullPath($area).StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\PendingQueueIndexContract-',[StringComparison]::Ordinal)){Remove-Item -LiteralPath $area -Recurse -Force}
}
