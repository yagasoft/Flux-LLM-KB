[CmdletBinding()]
param([string]$SourceRoot='')
$ErrorActionPreference='Stop'
if(-not $SourceRoot) { $SourceRoot=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) }
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) { throw 'Updater parse failed.' }
foreach($name in @('Get-HybridIisWorkerIds','Stop-HybridIisAfterGpuDrain')) {
 $definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
 if($definition.Count -ne 1) { throw "Required worker proof function missing: $name" }
 . ([scriptblock]::Create($definition[0].Extent.Text))
}
$allSwapCommands=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Invoke-IncrementalApplicationPayloadSwap'},$true))
function Get-EnclosingFunctionName($node) {
 $parent=$node.Parent
 while($null -ne $parent -and $parent -isnot [Management.Automation.Language.FunctionDefinitionAst]) { $parent=$parent.Parent }
 if($null -ne $parent) { return $parent.Name }
}
$swapCommand=@($allSwapCommands | Where-Object { !(Get-EnclosingFunctionName $_) })
if($swapCommand.Count -ne 1) { throw 'Expected one routine incremental payload swap.' }
$recoverySwap=@($allSwapCommands | Where-Object { (Get-EnclosingFunctionName $_) -ceq 'Invoke-RepositoryRecoveryIisUpdate' })
if($allSwapCommands.Count -ne 2 -or $recoverySwap.Count -ne 1 -or
   $recoverySwap[0].Extent.Text -cnotmatch '-RestartPreviousApplication:\$false' -or
   $recoverySwap[0].Extent.Text -cnotmatch '-StopApplication \$ports\.Stop') {
 throw 'Repository recovery must use the canonical swap/drain with predecessor restart refused.'
}
$stopArgument=$null;$rollbackValidationArgument=$null
for($i=0;$i -lt $swapCommand[0].CommandElements.Count-1;$i++) {
 if($swapCommand[0].CommandElements[$i] -is [Management.Automation.Language.CommandParameterAst] -and
    $swapCommand[0].CommandElements[$i].ParameterName -ceq 'StopApplication') {
  $stopArgument=$swapCommand[0].CommandElements[$i+1].Extent.Text
 }
 if($swapCommand[0].CommandElements[$i] -is [Management.Automation.Language.CommandParameterAst] -and
    $swapCommand[0].CommandElements[$i].ParameterName -ceq 'ValidateRollbackApplication') {
  $rollbackValidationArgument=$swapCommand[0].CommandElements[$i+1].Extent.Text
 }
}
if(-not $stopArgument -or $stopArgument -notmatch 'Stop-HybridIisAfterGpuDrain' -or
   $stopArgument -match 'Stop-WebAppPool') { throw 'Routine payload swap bypasses the canonical GPU drain and worker-exit proof.' }
if(-not $rollbackValidationArgument -or
   $rollbackValidationArgument.IndexOf('Assert-RetainedPipelineStateUnchanged', [StringComparison]::Ordinal) -lt 0 -or
   $rollbackValidationArgument.LastIndexOf('PayloadRollbackVerified = $true', [StringComparison]::Ordinal) -lt
       $rollbackValidationArgument.LastIndexOf('Assert-RetainedPipelineStateUnchanged', [StringComparison]::Ordinal)) {
 throw 'Payload rollback is marked verified before restoration probes and retained-state comparison.'
}
$releaseGuardCalls=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Test-IncrementalRollbackHoldRelease'},$true))
if($releaseGuardCalls.Count -ne 1) { throw 'Rollback hold-release guard is not wired into the updater.' }
$eligibility=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Test-IncrementalRollbackHoldRelease'},$true))
if($eligibility.Count -ne 1) { throw 'Routine rollback hold-release guard is missing.' }
. ([scriptblock]::Create($eligibility[0].Extent.Text))
$validation=@{HoldCreated=$true;PayloadRollbackVerified=$false;RollbackVerified=$true}
$arguments=@{Validation=$validation;ApplyMigrations=$false;ApplyCorpusChunkFullTextMigration=$false;CorpusRollbackVerified=$true;InteractiveHostMutationStarted=$true;InteractiveHostRollbackVerified=$true}
if(Test-IncrementalRollbackHoldRelease @arguments) { throw 'Unverified payload rollback released the validation hold.' }
$validation.PayloadRollbackVerified=$true
$arguments.InteractiveHostRollbackVerified=$false
if(Test-IncrementalRollbackHoldRelease @arguments) { throw 'Unverified interactive-host rollback released the validation hold.' }
$arguments.InteractiveHostRollbackVerified=$true
if(-not (Test-IncrementalRollbackHoldRelease @arguments)) { throw 'Verified schema-neutral rollback retained the validation hold.' }
$arguments.ApplyMigrations=$true;$validation.RollbackVerified=$false
if(Test-IncrementalRollbackHoldRelease @arguments) { throw 'Unverified schema rollback released the validation hold.' }
$validation.RollbackVerified=$true;$arguments.ApplyMigrations=$false
$arguments.ApplyCorpusChunkFullTextMigration=$true;$arguments.CorpusRollbackVerified=$false
if(Test-IncrementalRollbackHoldRelease @arguments) { throw 'Unverified Full-Text rollback released the validation hold.' }
$arguments.CorpusRollbackVerified=$true;$arguments.ApplyCorpusChunkFullTextMigration=$false
$validation.HoldCreated=$false
if(Test-IncrementalRollbackHoldRelease @arguments) { throw 'Absent hold was eligible for release.' }
function SyntheticAppCmd {
 $evidence.Commands.Add(($args -join ' '))
 $global:LASTEXITCODE=$evidence.ExitCode
 if($evidence.AfterStop -and $evidence.Mode -eq 'remaining') { '<appcmd><WP WP.NAME="404" APPPOOL.NAME="FluxKnowledge" /></appcmd>' }
 elseif($evidence.AfterStop -and $evidence.Mode -eq 'inventory-failure') { $global:LASTEXITCODE=1; '<appcmd />' }
 elseif($evidence.AfterStop) { '<appcmd><WP WP.NAME="202" APPPOOL.NAME="ForeignPool" /></appcmd>' }
 else { $evidence.Xml }
}
$evidence=@{Commands=[Collections.Generic.List[string]]::new();ExitCode=0;AfterStop=$false;Mode='';Xml='<appcmd />'}
if(@(Get-HybridIisWorkerIds -AppCmdPath SyntheticAppCmd -PoolName FluxKnowledge).Count -ne 0) { throw 'Empty successful inventory did not prove absence.' }
$evidence.Xml='<appcmd><WP WP.NAME="101" APPPOOL.NAME="FluxKnowledge" /><WP WP.NAME="202" APPPOOL.NAME="ForeignPool" /></appcmd>'
$ids=@(Get-HybridIisWorkerIds -AppCmdPath SyntheticAppCmd -PoolName FluxKnowledge)
if($ids.Count -ne 1 -or $ids[0] -ne 101) { throw 'Exact target pool selection failed.' }
foreach($case in @(
 @{Exit=1;Xml='<appcmd />'},@{Exit=0;Xml='<unexpected />'},
 @{Exit=0;Xml='<appcmd><WP WP.NAME="101" /></appcmd>'},
 @{Exit=0;Xml='<appcmd><WP WP.NAME="0" APPPOOL.NAME="FluxKnowledge" /></appcmd>'},
 @{Exit=0;Xml='<appcmd><ERROR /></appcmd>'})) {
 $evidence.ExitCode=$case.Exit; $evidence.Xml=$case.Xml
 try { Get-HybridIisWorkerIds -AppCmdPath SyntheticAppCmd -PoolName FluxKnowledge | Out-Null; throw 'Invalid inventory accepted.' }
 catch { if($_.Exception.Message -cne 'hybrid-iis-worker-inventory-unavailable') { throw } }
}
if(@($evidence.Commands | Where-Object { $_ -cne 'list wp /xml' }).Count) { throw 'Worker proof still invokes filtered appcmd inventory.' }

# Exercise the actual stop/proof function with a stopped pool and an unrelated worker.
$originalGetWorkers=${function:Get-HybridIisWorkerIds}
function Get-HybridIisWorkerIds { param($AppCmdPath,$PoolName) & $originalGetWorkers -AppCmdPath SyntheticAppCmd -PoolName $PoolName }
function Get-DeploymentSqlConnectionString { 'synthetic-no-connection' }
function Invoke-WithHybridGpuDrain { param($ConnectionString,$TimeoutSeconds,$StopApplication) & $StopApplication 1 }
function Get-WebAppPoolState { param($Name) @{Value=$evidence.PoolState} }
function Stop-WebAppPool { param($Name) if(-not $evidence.OwnsStop) { throw 'Stop ownership not recorded before mutation.' }; $evidence.PoolState='Stopped'; $evidence.AfterStop=$true }
function Wait-IisAppPoolState { param($Name,$ExpectedState,$TimeoutSeconds) if($evidence.PoolState -cne $ExpectedState) { throw 'Unexpected pool state.' } }
function Get-CimInstance {
 param($ClassName,$Filter,$ErrorAction)
 if($Filter) { return @() }
 @([pscustomobject]@{ProcessId=101;ParentProcessId=0;CreationDate=[DateTime]'2026-01-01'},[pscustomobject]@{ProcessId=303;ParentProcessId=101;CreationDate=[DateTime]'2026-01-01'})
}
$SiteName='FluxKnowledge';$RebuildTimeoutSeconds=30;$ReadinessTimeoutSeconds=0
foreach($mode in @('exited','remaining','inventory-failure')) {
 $evidence.ExitCode=0;$evidence.AfterStop=$false;$evidence.Mode=$mode;$evidence.PoolState='Started';$evidence.OwnsStop=$false
 $evidence.Xml='<appcmd><WP WP.NAME="101" APPPOOL.NAME="FluxKnowledge" /><WP WP.NAME="202" APPPOOL.NAME="ForeignPool" /></appcmd>'
 try {
  Stop-HybridIisAfterGpuDrain -OnStopRequested { $evidence.OwnsStop=$true }
  if($mode -ne 'exited') { throw 'Unproven target worker exit accepted.' }
 } catch {
  $expected=if($mode -eq 'remaining') {'hybrid-iis-worker-exit-not-proven'} elseif($mode -eq 'inventory-failure') {'hybrid-iis-worker-inventory-unavailable'} else {''}
  if(-not $expected -or $_.Exception.Message -cne $expected) { throw }
 }
 if(-not $evidence.OwnsStop -or $evidence.PoolState -cne 'Stopped') { throw 'Actual owned stop was not tracked through exit proof.' }
}
$evidence.ExitCode=0;$evidence.AfterStop=$true;$evidence.Mode='exited';$evidence.PoolState='Stopped';$evidence.OwnsStop=$false
Stop-HybridIisAfterGpuDrain -OnStopRequested { $evidence.OwnsStop=$true }
if($evidence.OwnsStop) { throw 'Already stopped pool falsely acquired stop ownership.' }
Write-Output 'Hybrid IIS worker proof contract passed: unfiltered exact-pool inventory, empty/foreign workers, failed/invalid inventory, remaining workers and pre-proof stop ownership.'
