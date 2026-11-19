[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
foreach($name in @('incremental-vector-publication-metadata-migration','incremental-source-deletion-pending-queue-index','incremental-passage-window-eligibility-migration')){
 Import-Module (Join-Path $SourceRoot ('scripts/deploy/'+$name+'.psm1')) -Force
}
function Check([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Refuse([scriptblock]$Action){$failure=$false;try{&$Action}catch{$failure=$true};Check $failure 'Expected refusal.'}
$contract=Get-PassageWindowEligibilityMigrationContract
$history=@(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations') -Filter '*.Designer.cs' -File|
 ForEach-Object{[regex]::Matches([IO.File]::ReadAllText($_.FullName),'\[Migration\("(\d{14}_[^"]+)"\)\]')|ForEach-Object{$_.Groups[1].Value}}|
 Where-Object{$_ -cle $contract.Baseline}|Sort-Object -Unique)
$old=@{Identity='owned';History=$history;CanAlter=$true;ResolvesDbo=$true;SchemaSha256=$contract.JournalSchemaSha256;
 FeatureSchemaSha256=$contract.OldSchemaSha256;MetadataIndexSha256=$contract.MetadataIndexSha256;QueueIndexSha256=$contract.QueueIndexSha256}
$new=$old.Clone();$new.History=$history+$contract.Target;$new.FeatureSchemaSha256=$contract.NewSchemaSha256
Check ((Get-PassageWindowEligibilityStateKind $old 'owned') -ceq 'Old') 'Old classification failed.'
Check ((Get-PassageWindowEligibilityStateKind $new 'owned') -ceq 'New') 'New classification failed.'
foreach($field in @('Identity','SchemaSha256','FeatureSchemaSha256','MetadataIndexSha256','QueueIndexSha256','History','ResolvesDbo')){
 $bad=$new.Clone();$bad[$field]=if($field -ceq 'History'){@('unexpected')}elseif($field -ceq 'ResolvesDbo'){$false}else{'changed'}
 Refuse {Get-PassageWindowEligibilityStateKind $bad 'owned'}
}
$area=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('WindowEligibilityContract-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $area
foreach($scenario in @('Success','AlreadyNew','RolledBack','LostAck','Drift')){
 $directory=Join-Path $area $scenario;$null=New-Item -ItemType Directory -Path $directory
 $initial=if($scenario -ceq 'AlreadyNew'){$new}else{$old}
 $case=@{Current=$initial;Calls=0;Stopped=0}
 $state=New-PassageWindowEligibilityMigrationState $initial $directory 'owned-release' ('a'*40) $contract.UpSha256
 $run={
  $case.Calls++;Check (Test-Path -LiteralPath $state.IntentPath) 'DDL preceded durable intent.'
  if($scenario -ceq 'RolledBack'){throw 'rolled back'}
  $case.Current=$new
  if($scenario -ceq 'LostAck'){throw 'lost acknowledgement'}
 }
 if($scenario -ceq 'Drift'){$case.Current=$bad}
 $attempt={Invoke-PassageWindowEligibilityMigrationAttempt $state {$case.Current} {$case.Stopped++} $run}
 if($scenario -in @('RolledBack','LostAck','Drift')){Refuse $attempt}else{&$attempt}
 if($scenario -ceq 'Drift'){Check (!$state.Attempted -and $case.Calls -eq 0) 'Drift reached DDL.';continue}
 Check ($state.Attempted -and $case.Stopped -eq 1) 'Stop/intent missing.'
 Check ($case.Calls -eq $(if($scenario -ceq 'AlreadyNew'){0}else{1})) 'DDL count changed.'
 Check ($state.SettledState -ceq $(if($scenario -ceq 'RolledBack'){'Old'}else{'New'})) 'Unknown commit was not reconciled.'
 Refuse $attempt
}
$badSql=Join-Path $area 'changed.sql';[IO.File]::WriteAllText($badSql,'SELECT 1;')
$message='';try{Invoke-PassageWindowEligibilityMigrationSql 'invalid connection' $badSql 'owned'}catch{$message=$_.Exception.Message}
Check ($message -like '*window-eligibility-up-sql-changed*') 'SQL hash was not checked before connecting.'
Write-Output 'Window eligibility state, durable intent, lost acknowledgement and no-replay contract passed.'
