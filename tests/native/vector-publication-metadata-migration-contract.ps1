[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-vector-publication-metadata-migration.psm1') -Force
function Check([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Refuse([scriptblock]$Action,[string]$Expected='*'){
 $failure=$null;try{&$Action}catch{$failure=$_.Exception.Message}
 Check ($null -ne $failure -and $failure -like $Expected) ('Wrong metadata-migration refusal: '+$failure)
}
$contract=Get-VectorPublicationMetadataMigrationContract
$history=@(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations') -Filter '*.cs' -File|
 ForEach-Object{[regex]::Matches([IO.File]::ReadAllText($_.FullName),'\[Migration\("(\d{14}_[^"]+)"\)\]')|ForEach-Object{$_.Groups[1].Value}}|
 Where-Object{$_ -cle $contract.Baseline}|Sort-Object -Unique)
$old=[pscustomobject]@{Identity='owned-incarnation';History=$history;CanAlter=$true;ResolvesDbo=$true;SchemaSha256=$contract.JournalSchemaSha256;VectorPublicationIndexSha256=$contract.AbsentIndexSha256}
$new=[pscustomobject]@{Identity=$old.Identity;History=($history+$contract.Target);CanAlter=$false;ResolvesDbo=$true;SchemaSha256=$contract.JournalSchemaSha256;VectorPublicationIndexSha256=$contract.IndexSha256}
Check ((Get-VectorPublicationMetadataStateKind $old $old.Identity) -ceq 'Old') 'Old classification failed.'
Check ((Get-VectorPublicationMetadataStateKind $new $old.Identity) -ceq 'New') 'New classification failed.'
foreach($case in @(
 @{State=[pscustomobject]@{Identity='foreign';ResolvesDbo=$true;History=$history;SchemaSha256=$old.SchemaSha256;VectorPublicationIndexSha256=$old.VectorPublicationIndexSha256};Reason='vector-metadata-database-or-journal-schema-changed'},
 @{State=[pscustomobject]@{Identity=$old.Identity;ResolvesDbo=$true;History=($history+'20990101000000_Unexpected');SchemaSha256=$old.SchemaSha256;VectorPublicationIndexSha256=$contract.IndexSha256};Reason='vector-metadata-partial-or-unreviewed-state-retain-hold'},
 @{State=[pscustomobject]@{Identity=$old.Identity;ResolvesDbo=$true;History=$history;SchemaSha256=$old.SchemaSha256;VectorPublicationIndexSha256=$contract.IndexSha256};Reason='vector-metadata-partial-or-unreviewed-state-retain-hold'},
 @{State=[pscustomobject]@{Identity=$old.Identity;ResolvesDbo=$true;History=$new.History;SchemaSha256=$old.SchemaSha256;VectorPublicationIndexSha256=$contract.AbsentIndexSha256};Reason='vector-metadata-partial-or-unreviewed-state-retain-hold'},
 @{State=[pscustomobject]@{Identity=$old.Identity;ResolvesDbo=$true;History=$new.History;SchemaSha256=$old.SchemaSha256;VectorPublicationIndexSha256=('F'*64)};Reason='vector-metadata-partial-or-unreviewed-state-retain-hold'},
 @{State=[pscustomobject]@{Identity=$old.Identity;ResolvesDbo=$true;History=$new.History;SchemaSha256=('F'*64);VectorPublicationIndexSha256=$contract.IndexSha256};Reason='vector-metadata-database-or-journal-schema-changed'},
 @{State=[pscustomobject]@{Identity=$old.Identity;ResolvesDbo=$false;History=$new.History;SchemaSha256=$old.SchemaSha256;VectorPublicationIndexSha256=$contract.IndexSha256};Reason='vector-metadata-database-or-journal-schema-changed'},
 @{State=[pscustomobject]@{Identity=$old.Identity;ResolvesDbo=$true;History=($history[0..76]+$contract.Target);SchemaSha256=$old.SchemaSha256;VectorPublicationIndexSha256=$contract.IndexSha256};Reason='vector-metadata-predecessor-history-changed'}
)){$bad=$case.State;Refuse {Get-VectorPublicationMetadataStateKind $bad $old.Identity} $case.Reason}
$area=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('VectorMetadataContract-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $area
try {
 foreach($scenario in @('Success','AlreadyNew','RolledBack','LostAck','Drift')){
  $directory=Join-Path $area $scenario;$null=New-Item -ItemType Directory -Path $directory
  $initial=if($scenario -ceq 'AlreadyNew'){$new}else{$old}
  $case=[ordered]@{Current=$initial;Calls=0;IntentObserved=$false;Stopped=0}
  $state=New-VectorPublicationMetadataMigrationState -DatabaseState $initial -ReceiptDirectory $directory -ReleaseId 'owned-release' -Commit ('a'*40) -SqlSha256 $contract.UpSha256
  $run={
   $case.Calls++;$case.IntentObserved=Test-Path -LiteralPath $state.IntentPath
   if($scenario -ceq 'RolledBack'){throw 'owned SQL transaction rollback'}
   $case.Current=$new
   if($scenario -ceq 'LostAck'){throw 'owned lost commit acknowledgement'}
  }
  if($scenario -ceq 'Drift'){$case.Current=$bad}
  $attempt={Invoke-VectorPublicationMetadataMigrationAttempt -State $state -ReadState {$case.Current} -AssertStopped {$case.Stopped++} -RunUp $run}
  if($scenario -in @('RolledBack','LostAck','Drift')){Refuse $attempt}else{&$attempt}
  if($scenario -ceq 'Drift'){Check (!$state.Attempted -and $case.Calls -eq 0) 'Identity/schema drift reached intent or DDL.';continue}
  Check ($state.Attempted -and $case.Stopped -eq 1 -and (Test-Path -LiteralPath $state.IntentPath)) 'Stopped proof/durable intent missing.'
  Check ($case.Calls -eq $(if($scenario -ceq 'AlreadyNew'){0}else{1})) 'DDL count is incorrect.'
  if($case.Calls){Check $case.IntentObserved 'DDL preceded durable intent.'}
  Check ($state.SettledState -ceq $(if($scenario -ceq 'RolledBack'){'Old'}else{'New'})) 'Committed/rolled-back outcome was not reconciled.'
  Refuse $attempt
 }
 $badDirectory=Join-Path $area 'WrongHash';$null=New-Item -ItemType Directory -Path $badDirectory
 Refuse {New-VectorPublicationMetadataMigrationState -DatabaseState $old -ReceiptDirectory $badDirectory -ReleaseId 'owned-release' -Commit ('a'*40) -SqlSha256 ('F'*64)}
 Refuse {New-VectorPublicationMetadataMigrationState -DatabaseState $old -ReceiptDirectory $badDirectory -ReleaseId 'owned-release' -Commit ('a'*40) -SqlSha256 $contract.UpSha256 -ResumeReleaseBindingSha256 'wrong'} 'vector-metadata-resume-release-binding-hash-invalid'
 $resumedDirectory=Join-Path $area 'BeforeIntentResume';$null=New-Item -ItemType Directory -Path $resumedDirectory
 $resumed=New-VectorPublicationMetadataMigrationState -DatabaseState $new -ReceiptDirectory $resumedDirectory -ReleaseId 'owned-resume' -Commit ('a'*40) -SqlSha256 $contract.UpSha256 -ResumeReleaseBindingSha256 ('E'*64)
 Invoke-VectorPublicationMetadataMigrationAttempt -State $resumed -ReadState {$new} -AssertStopped {} -RunUp {throw 'New state must not execute DDL.'}
 $resumedIntent=[IO.File]::ReadAllText($resumed.IntentPath)|ConvertFrom-Json
 Check ($resumedIntent.ResumeIntentSha256 -ceq '' -and $resumedIntent.ResumeReleaseBindingSha256 -ceq ('E'*64)) 'Pre-intent resume invented or lost its immutable evidence link.'
 $badSql=Join-Path $badDirectory 'wrong.sql';[IO.File]::WriteAllText($badSql,'SELECT 1;')
 $errorText='';try{Invoke-VectorPublicationMetadataMigrationSql -ConnectionString 'not-a-connection-string' -SqlPath $badSql -ExpectedDatabaseIdentity $old.Identity}catch{$errorText=$_.Exception.Message}
 Check ($errorText -like '*vector-metadata-up-sql-changed*') 'SQL bytes were not rejected before connection parsing/open.'
 Write-Output 'Vector publication metadata migration state/intent/reconciliation contract passed.'
} finally {
 if([IO.Path]::GetFullPath($area).StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\VectorMetadataContract-',[StringComparison]::Ordinal)){
  Remove-Item -LiteralPath $area -Recurse -Force
 }
}
