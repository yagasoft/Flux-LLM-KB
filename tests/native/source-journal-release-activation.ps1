[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-release.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-migration.psm1') -Force
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
$contract=Get-SourceJournalMigrationContract
$base=@(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations') -Filter '*.cs' -File|
 ForEach-Object {[regex]::Matches([IO.File]::ReadAllText($_.FullName),'\[Migration\("(\d{14}_[^"]+)"\)\]')|ForEach-Object {$_.Groups[1].Value}}|
 Where-Object {$_ -cle $contract.Baseline}|Sort-Object)
$baseline=[pscustomobject]@{Identity='isolated-incarnation';History=$base;SchemaSha256=$contract.BaselineSchemaSha256;CanAlter=$true}
$complete=[pscustomobject]@{Identity=$baseline.Identity;History=($base+@($contract.Migrations));SchemaSha256=$contract.SchemaSha256;CanAlter=$true}
$area=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('JournalReleaseActivation-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $area
try{
 foreach($mode in @('initial','already-applied','helper-failure','late-payload-drift','partial-sql','stop-lost-after-intent','disable','disable-reader-busy','changed-database')){
  $path=Join-Path $area $mode;$null=New-Item -ItemType Directory -Path $path
  $box=@{Database=$(if($mode -cin @('already-applied','disable','disable-reader-busy')){$complete}else{$baseline});
   Events=[Collections.Generic.List[string]]::new();Sql=0;Configured=$false;Helper=0;Retired=0;Stops=0;PayloadChecks=0}
  $state=if($mode.StartsWith('disable')){$null}else{New-SourceJournalMigrationState -DatabaseState $box.Database -ReceiptDirectory $path -ReleaseId $mode -SqlSha256 $contract.UpSha256}
  $ports=@{
   AssertStopped={
    $box.Stops++;$box.Events.Add('stopped')
    if($mode -ceq 'stop-lost-after-intent' -and $state.Attempted){throw 'owned native exit unproved'}
   }
   AssertPayloadSet={
    $box.PayloadChecks++;$box.Events.Add('payloads')
    if($mode -ceq 'late-payload-drift' -and $box.Helper){throw 'owned installed payload drift'}
   }
   UpdateHelper={
    $box.Helper++;$box.Events.Add('helper')
    Assert-True (!$box.Configured -and !$state.Attempted) 'Helper replacement followed configuration or SQL intent.'
    if($mode -ceq 'helper-failure'){throw 'owned helper upgrade failure'}
   }
   ApplyConfiguration={$box.Configured=$true;$box.Events.Add('configuration')}
   ReadDatabaseState={$box.Database}
   RunMigration={
    Assert-True ($box.Configured -and $box.Helper -eq 1 -and $state.Attempted -and (Test-Path -LiteralPath $state.IntentPath)) 'DDL preceded the complete set, configuration or durable intent.'
    $box.Sql++;$box.Events.Add('sql')
    if($mode -ceq 'partial-sql'){throw 'owned first committed migration failure'}
    $box.Database=if($mode -ceq 'changed-database'){
     [pscustomobject]@{Identity='different-incarnation';History=$complete.History;SchemaSha256=$complete.SchemaSha256;CanAlter=$true}
    }else{$complete}
   }
   RetireVerification={
    Assert-True $box.Configured 'Retirement preceded feature-off configuration.'
    $box.Retired++;$box.Events.Add('retire')
    if($mode -ceq 'disable-reader-busy'){throw 'owned physical reader slot busy'}
   }
  }
  $failure=''
  try{Invoke-SourceJournalReleaseActivation -MigrationState $state -ExpectedDatabaseIdentity $baseline.Identity -Ports $ports -Disable:($mode.StartsWith('disable'))}
  catch{$failure=$_.Exception.Message}
  if($mode -cin @('initial','already-applied','disable')){Assert-True (!$failure) ('Valid release failed: '+$failure)}
  else{Assert-True (!!$failure) ('Injected failure hidden: '+$mode)}
  switch($mode){
   'initial'{Assert-True ($box.Sql -eq 1 -and $state.Applied) 'Initial DDL did not reach the exact feature state.'}
   'already-applied'{Assert-True ($box.Sql -eq 0 -and $state.Applied) 'An applied feature replayed its SQL.'}
   'helper-failure'{Assert-True (!$box.Configured -and !$state.Attempted -and !$box.Sql) 'Helper failure crossed SQL/config boundary.'}
   'late-payload-drift'{Assert-True (!$box.Configured -and !$state.Attempted -and !$box.Sql) 'Late payload drift reached configuration/SQL.'}
   'partial-sql'{Assert-True ($box.Sql -eq 1 -and $state.Attempted -and !$state.Applied -and (Test-Path -LiteralPath $state.IntentPath)) 'Partial migration lost its no-replay boundary.'}
   'stop-lost-after-intent'{Assert-True ($state.Attempted -and !$box.Sql) 'Unproved stop reached DDL or lost its intent.'}
   'disable'{Assert-True ($box.Retired -eq 1 -and !$box.Sql -and !$box.Helper) 'Feature-off changed helper/schema or missed retirement.'}
   'disable-reader-busy'{Assert-True ($box.Configured -and $box.Retired -eq 1 -and !$box.Sql) 'Busy retirement did not retain feature-off state.'}
   'changed-database'{Assert-True (!$state.Applied -and $state.Attempted) 'Changed database was admitted.'}
  }
 }
 [pscustomobject]@{Passed=9;DurableIntentFiles=$true;ProductionCalls=0;ActualSqlCalls=0}|ConvertTo-Json
}finally{
 $resolved=[IO.Path]::GetFullPath($area)
 if(!$resolved.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\JournalReleaseActivation-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe release activation cleanup.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
