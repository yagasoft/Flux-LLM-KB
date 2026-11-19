[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Updater does not parse.'}
function Check([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Refuse([scriptblock]$Action){$failed=$false;try{&$Action}catch{$failed=$true};Check $failed 'Expected refusal.'}
function Load([string]$Name){
 $function=$ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name},$true)
 Check ($function.Count -eq 1) ('Ambiguous function: '+$Name)
 . ([scriptblock]::Create('function script:'+$Name+' '+$function[0].Body.Extent.Text))
}
Load 'Assert-SourceDeletionPendingQueueUpdateOptions'
$good=@{EnsureSourceDeletionPendingQueueIndex=$true;ExpectedSourceJournalDatabaseIdentity='owned'}
Assert-SourceDeletionPendingQueueUpdateOptions $good
foreach($flag in @('ApplyMigrations','ApplyVectorPublicationMetadataMigration','RecoverStoppedPool','EnableDashboardSnapshots','DisableSourceAccessRecovery','ApplyIncrementalSourceScanning')){
 $bad=$good.Clone();$bad[$flag]=$true;Refuse {Assert-SourceDeletionPendingQueueUpdateOptions $bad}
}
$bad=@{EnsureSourceDeletionPendingQueueIndex=$true};Refuse {Assert-SourceDeletionPendingQueueUpdateOptions $bad}
$bad=$good.Clone();$bad.Apply=$true;Refuse {Assert-SourceDeletionPendingQueueUpdateOptions $bad}
Load 'Invoke-SourceDeletionPendingQueueStoppedAttempt'
$script:attempts=0;$script:reads=0
function Invoke-SourceDeletionPendingQueueAttempt {param($State,$ReadState,$AssertStopped,$RunUp);$script:attempts++;$State.Attempted=$true;throw 'lost-result'}
function Confirm-SourceDeletionPendingQueueOutcome {param($State,$ReadState);$script:reads++;'New'}
$state=@{Attempted=$false}
Refuse {Invoke-SourceDeletionPendingQueueStoppedAttempt $state @{} 'I:\owned\index.sql'}
Invoke-SourceDeletionPendingQueueStoppedAttempt $state @{} 'I:\owned\index.sql'
Check ($script:attempts -eq 1 -and $script:reads -eq 1) 'A second stop replayed unknown-outcome DDL.'
Load 'Invoke-SourceDeletionPendingQueuePreparation'
$script:events=[Collections.Generic.List[string]]::new();$script:injected=''
function Record([string]$Event){$script:events.Add($Event);if($script:injected -ceq $Event){throw ('injected-'+$Event)}}
function Get-ScheduledTask {param($TaskName,$ErrorAction);@{Settings=@{Enabled=$true}}}
function Disable-ScheduledTask {param($TaskName,$ErrorAction);Record 'disable'}
function Wait-InteractiveHostStopped {param($TaskName,$TimeoutSeconds);Record 'companion-exited'}
function Stop-HybridIisAfterGpuDrain {Record 'drained-stopped'}
function New-DeploymentValidationHold {param($Path,$ReleaseId);Record 'hold'}
function Get-RetainedPipelineStateBaseline {Record 'baseline';@{Captured=$true}}
function Invoke-SourceDeletionPendingQueueStoppedAttempt {param($State,$Binding,$SqlPath);Record 'intent-and-index'}
$InteractiveHostTaskName='fixture';$ReadinessTimeoutSeconds=300;$ValidationHoldPath='I:\owned\hold.json'
foreach($failure in @('disable','companion-exited','drained-stopped','hold','baseline','intent-and-index','')){
 $script:events.Clear();$script:injected=$failure;$deploymentValidation=@{HoldCreated=$false;Baseline=$null}
 if($failure){Refuse {Invoke-SourceDeletionPendingQueuePreparation @{ReleaseId='owned'} @{} 'I:\owned\index.sql'}}
 else {Invoke-SourceDeletionPendingQueuePreparation @{ReleaseId='owned'} @{} 'I:\owned\index.sql';Record 'first-payload-copy'}
 $expected=@('disable','companion-exited','drained-stopped','hold','baseline','intent-and-index','first-payload-copy')
 $count=if($failure){[array]::IndexOf($expected,$failure)+1}else{$expected.Count}
 Check (($script:events -join '|') -ceq ($expected[0..($count-1)] -join '|')) 'Preparation crossed a failed boundary.'
}
$text=$ast.Extent.Text
Check ($text.IndexOf('Invoke-SourceDeletionPendingQueuePreparation $pendingQueueIndexState') -lt $text.IndexOf('Copy-InteractiveHostPayload -Source $interactiveHostCandidateRoot')) 'Companion mutation preceded held index preparation.'
$swaps=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.AssignmentStatementAst] -and
 $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and $node.Left.VariablePath.UserPath -ceq 'swap'},$true))
Check ($swaps.Count -eq 1) 'Ambiguous main payload swap.'
$swap=$swaps[0].Extent.Text
Check ($swap.Contains("pending-queue-index-forward-recovery-required-retain-hold") -and $swap.Contains("pending-queue-index-start-requires-exact-successor")) 'Payload swap lost forward recovery or activation guard.'
Check (!$text.Contains('$applyAnyMigration = $EnsureSourceDeletionPendingQueueIndex')) 'Operational index widened the EF migration mode.'
$program=[IO.File]::ReadAllText((Join-Path $SourceRoot 'src/FluxKnowledge.Web/Program.cs'))
$bootstrapStart=$program.IndexOf('if (applyNativeGoLiveMigrations)')
$bootstrapEnd=$program.IndexOf('if (!WebHostComposition.IsIsolatedTestComposition)')
$bootstrap=$program.Substring($bootstrapStart,$bootstrapEnd-$bootstrapStart)
Check ($bootstrap.Contains('await SqlSourceDeletionPendingQueueIndex.EnsureAsync(context, CancellationToken.None);')) 'Actual published fresh-native migration entry point omits the operational index.'
Check (($program.Split('SqlSourceDeletionPendingQueueIndex.EnsureAsync').Length-1) -eq 1) 'Index DDL escaped the explicit bootstrap branch.'
Write-Output 'Pending queue index updater mode, stop/hold/DDL ordering and no-replay contract passed.'
