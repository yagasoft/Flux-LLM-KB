[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-migration.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Canonical updater parse failed.'}
foreach($name in @('Assert-DeploymentSourceJournalLegacyMigrationAllowed','Assert-DeploymentSourceJournalPredecessorRestorationAllowed','Invoke-GeneratedSqlScript')){
 $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$false))
 if($nodes.Count -ne 1){throw ('Missing actual updater boundary: '+$name)}
 Invoke-Expression $nodes[0].Extent.Text
}
function Get-DeploymentSourceJournalBoundary {
 if($mode -ceq 'unavailable-boundary' -and !$case.BoundaryRecovered){throw 'owned unavailable database observation'}
 @{Protected=($case.Current.SchemaSha256 -cne $contract.BaselineSchemaSha256 -or ($state -and $state.Attempted))}
}
$contract=Get-SourceJournalMigrationContract
$base=@(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations') -Filter '*.cs' -File|
 ForEach-Object {[regex]::Matches([IO.File]::ReadAllText($_.FullName),'\[Migration\("(\d{14}_[^"]+)"\)\]')|ForEach-Object {$_.Groups[1].Value}}|
 Where-Object {$_ -cle $contract.Baseline}|Sort-Object)
$baseline=[pscustomobject]@{Identity='disposable-incarnation';History=$base;SchemaSha256=$contract.BaselineSchemaSha256;CanAlter=$true}
$complete=[pscustomobject]@{Identity=$baseline.Identity;History=($base+@($contract.Migrations));SchemaSha256=$contract.SchemaSha256;CanAlter=$true}
$area=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('JournalMigrationRecovery-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $area
try{
 foreach($mode in @('after-migration','partial-migration','ordinary-feature-update','unavailable-boundary','pre-intent')){
  $root=Join-Path $area $mode;$null=New-Item -ItemType Directory -Path $root
  $live=Join-Path $root 'live';$candidate=Join-Path $root 'candidate';$prior=Join-Path $root 'prior';$failed=Join-Path $root 'failed'
  $null=New-Item -ItemType Directory -Path $live,$candidate
  [IO.File]::WriteAllText((Join-Path $live 'version'),'old');[IO.File]::WriteAllText((Join-Path $candidate 'version'),'new')
  $state=if($mode -cin @('after-migration','partial-migration')){New-SourceJournalMigrationState -DatabaseState $baseline -ReceiptDirectory $root -ReleaseId $mode -SqlSha256 $contract.UpSha256}else{$null}
  $case=@{Current=$(if($mode -ceq 'ordinary-feature-update'){$complete}else{$baseline});CandidateStarts=0;OldStarts=0;TaskEnables=0;UpCalls=0;Stops=0;RollbackCallbacks=0;BoundaryRecovered=$false}
  $deploymentValidation=@{JournalRecoveryRequired=$false}
  $rejected=$false
  try{
   $null=Invoke-IncrementalApplicationPayloadSwap -ApplicationRoot $live -CandidateRoot $candidate -PreviousRoot $prior -FailedRoot $failed `
    -StopApplication {$case.Stops++} -ActivateCandidate {
     $null=New-Item -ItemType Directory -Path $live;[IO.File]::WriteAllText((Join-Path $live 'version'),'new')
     if($mode -ceq 'pre-intent'){throw 'owned pre-migration activation failure'}
     if($state -and !$state.Attempted){
      Invoke-SourceJournalMigrationAttempt -State $state -ReadState {$case.Current} -AssertStopped {} -RunUp {
       $case.UpCalls++
       if($mode -ceq 'partial-migration'){
        $case.Current=[pscustomobject]@{Identity=$baseline.Identity;History=($base+@($contract.Migrations[0]));SchemaSha256=('A'*64)}
        throw 'owned first committed migration failure'
       }
       $case.Current=$complete
      }
     }
    } -StartApplication {
     if([IO.File]::ReadAllText((Join-Path $live 'version')) -ceq 'old'){$case.OldStarts++;$case.TaskEnables++}else{$case.CandidateStarts++}
    } -ValidateApplication {throw 'owned candidate readiness failure'} `
    -AssertRollbackAllowed {Assert-DeploymentSourceJournalPredecessorRestorationAllowed} `
    -PrepareRollbackApplication {$case.RollbackCallbacks++} -ValidateRollbackApplication {}
  }catch{$rejected=$true}
  Assert-True $rejected 'The injected deployment failure was hidden.'
  if($mode -ceq 'pre-intent'){
   Assert-True ([IO.File]::ReadAllText((Join-Path $live 'version')) -ceq 'old') 'An intact baseline did not restore its predecessor.'
   Assert-True ($case.OldStarts -eq 1 -and $case.UpCalls -eq 0) 'An intact pre-intent baseline lost its safe rollback.'
   Assert-True (!$deploymentValidation.JournalRecoveryRequired -and $case.RollbackCallbacks -eq 1) 'A safe pre-intent rollback was marked forward-only.'
   Assert-DeploymentSourceJournalLegacyMigrationAllowed
  }else{
   Assert-True ([IO.File]::ReadAllText((Join-Path $live 'version')) -ceq 'new' -and
    [IO.File]::ReadAllText((Join-Path $prior 'version')) -ceq 'old' -and !(Test-Path -LiteralPath $failed)) 'Guard ran after candidate/predecessor file restoration.'
   Assert-True ($case.OldStarts -eq 0 -and $case.TaskEnables -eq 0) 'Feature schema reached a predecessor start/task-enable callback.'
   Assert-True ($case.Stops -eq 2 -and $case.RollbackCallbacks -eq 0) 'The candidate was not stopped before refusing file restoration.'
   Assert-True ($case.CandidateStarts -eq $(if($mode -ceq 'partial-migration'){0}else{1})) 'The candidate failure did not execute through the actual swap.'
   Assert-True ($case.UpCalls -eq $(if($state){1}else{0})) 'Migration replayed during recovery.'
   Assert-True $deploymentValidation.JournalRecoveryRequired 'Forward recovery did not retain its sticky hold obligation.'
   $sqlRefusal=''
   try{Invoke-GeneratedSqlScript -Path (Join-Path $root 'must-not-open.sql')}catch{$sqlRefusal=$_.Exception.Message}
   Assert-True ($sqlRefusal -like 'journal-schema-legacy-migration-or-down-forbidden*' -or
    ($mode -ceq 'unavailable-boundary' -and $sqlRefusal -ceq 'owned unavailable database observation')) 'Generic migration/Down reached its input path or SQL connection.'
   if($mode -ceq 'unavailable-boundary'){
    $case.BoundaryRecovered=$true
    $secondRefusal=''
    try{Assert-DeploymentSourceJournalPredecessorRestorationAllowed}catch{$secondRefusal=$_.Exception.Message}
    Assert-True ($secondRefusal -ceq 'journal-schema-predecessor-restoration-forbidden-retain-candidate-set-and-hold' -and
     $deploymentValidation.JournalRecoveryRequired) 'A recovered baseline cleared an earlier refusal and authorised a mixed payload set.'
   }
  }
 }
 Write-Output 'Five canonical journal rollback/generic-SQL boundaries passed through real payload files; zero production operations.'
}finally{
 $resolved=[IO.Path]::GetFullPath($area)
 if(!$resolved.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\JournalMigrationRecovery-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe recovery fixture cleanup'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
