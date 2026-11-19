[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),[switch]$Outlook,[switch]$CodexWorkspace)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$ApplyOutlookWatchedSourceMigration=$Outlook
$ApplyCodexWorkspaceNotesMigration=$CodexWorkspace
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper.psm1') -Force
foreach($name in @('Read-SourceJournalHelperFile','Assert-SourceJournalHelperBindings')){
 if(!(Get-Command $name -ErrorAction SilentlyContinue)){throw ('Typed updater helper API is not exported: '+$name)}
}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Updater does not parse.'}
function Check([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Refuse([scriptblock]$Action){$failed=$false;try{&$Action}catch{$failed=$true};Check $failed 'Expected refusal.'}
function Load([string]$Name){
 $node=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $Name},$true))
 Check ($node.Count -eq 1) ('Missing function: '+$Name)
 . ([scriptblock]::Create('function script:'+$Name+' '+$node[0].Body.Extent.Text))
}
Load 'Assert-PassageWindowEligibilityUpdateOptions'
$mode = if ($CodexWorkspace) { 'ApplyCodexWorkspaceNotesMigration' } elseif ($Outlook) { 'ApplyOutlookWatchedSourceMigration' } else { 'ApplyPassageWindowEligibilityMigration' }
$good=@{$mode=$true;ExpectedSourceJournalDatabaseIdentity='owned'}
Assert-PassageWindowEligibilityUpdateOptions $good
foreach($otherMode in @('ApplyCodexWorkspaceNotesMigration','ApplyOutlookWatchedSourceMigration','ApplyPassageWindowEligibilityMigration') | Where-Object { $_ -ne $mode }){
 $bad=$good.Clone();$bad[$otherMode]=$true;Refuse {Assert-PassageWindowEligibilityUpdateOptions $bad}
}
foreach($flag in @('ApplyMigrations','ApplyVectorPublicationMetadataMigration','RecoverStoppedPool','EnableDashboardSnapshots','DisableSourceAccessRecovery','ApplyIncrementalSourceScanning')){
 $bad=$good.Clone();$bad[$flag]=$true;Refuse {Assert-PassageWindowEligibilityUpdateOptions $bad}
}
Load 'Invoke-PassageWindowEligibilityStoppedAttempt'
$script:attempts=0;$script:reads=0;$sourceJournalHelperState=@{Completed=$false}
function Invoke-PassageWindowEligibilityMigrationAttempt {param($State,$ReadState,$AssertStopped,$RunUp);$script:attempts++;$State.Attempted=$true;throw 'lost-result'}
function Confirm-PassageWindowEligibilityMigrationOutcome {param($State,$ReadState);$script:reads++;'New'}
$state=@{Attempted=$false}
Refuse {Invoke-PassageWindowEligibilityStoppedAttempt $state @{} 'I:\owned\up.sql'}
Check ($script:attempts -eq 0) 'DDL preceded completed helper.'
$sourceJournalHelperState.Completed=$true
Refuse {Invoke-PassageWindowEligibilityStoppedAttempt $state @{} 'I:\owned\up.sql'}
Invoke-PassageWindowEligibilityStoppedAttempt $state @{} 'I:\owned\up.sql'
Check ($script:attempts -eq 1 -and $script:reads -eq 1) 'Unknown-outcome DDL replayed.'
Load 'Invoke-PassageWindowEligibilityPreparation'
$events=[Collections.Generic.List[string]]::new();$injected=''
function Record([string]$Event){$events.Add($Event);if($injected -ceq $Event){throw ('injected-'+$Event)}}
function Get-ScheduledTask {param($TaskName,$ErrorAction);@{Settings=@{Enabled=$true}}}
function Disable-ScheduledTask {param($TaskName,$ErrorAction);Record 'disable'}
function Wait-InteractiveHostStopped {param($TaskName,$TimeoutSeconds);Record 'companion-exited'}
function Stop-HybridIisAfterGpuDrain {Record 'drained-stopped'}
function New-DeploymentValidationHold {param($Path,$ReleaseId);Record 'hold'}
function Get-RetainedPipelineStateBaseline {Record 'baseline';@{Captured=$true}}
function Invoke-SourceJournalHelperUpdate {param($State,$Ports);Record 'helper-complete'}
function Assert-PassageWindowEligibilityStopped {param($State,$Binding);Record 'exact-stop'}
function Invoke-PassageWindowEligibilityStoppedAttempt {param($State,$Binding,$SqlPath);Record 'ddl-intent'}
$InteractiveHostTaskName='fixture';$ReadinessTimeoutSeconds=300;$ValidationHoldPath='I:\owned\hold.json';$sourceJournalNativePorts=@{}
$expected=@('disable','companion-exited','drained-stopped','hold','baseline','helper-complete','exact-stop','ddl-intent','first-payload-copy')
foreach($failure in ($expected[0..7]+'')){
 $events.Clear();$injected=$failure;$deploymentValidation=@{HoldCreated=$false;Baseline=$null}
 if($failure){Refuse {Invoke-PassageWindowEligibilityPreparation @{ReleaseId='owned'} @{} 'I:\owned\up.sql'}}
 else{Invoke-PassageWindowEligibilityPreparation @{ReleaseId='owned'} @{} 'I:\owned\up.sql';Record 'first-payload-copy'}
 $count=if($failure){[array]::IndexOf($expected,$failure)+1}else{$expected.Count}
 Check (($events -join '|') -ceq ($expected[0..($count-1)] -join '|')) 'Preparation crossed a failed boundary.'
}
Load 'Assert-PassageWindowEligibilityActivation'
function Get-DeploymentSqlConnectionString {'owned-web'}
function Get-RepositoryRecoveryCompanionConnectionString {'owned-companion'}
function Get-PassageWindowEligibilityDatabaseState {
 param($ConnectionString,[switch]$Outlook,[switch]$CodexWorkspace)
 Check ($Outlook -eq $ApplyOutlookWatchedSourceMigration -and $CodexWorkspace -eq $ApplyCodexWorkspaceNotesMigration) 'Database observation used the wrong migration mode.'
 $ConnectionString
}
function Get-PassageWindowEligibilityStateKind {
 param($DatabaseState,$ExpectedDatabaseIdentity,[switch]$Outlook,[switch]$CodexWorkspace)
 Check ($Outlook -eq $ApplyOutlookWatchedSourceMigration -and $CodexWorkspace -eq $ApplyCodexWorkspaceNotesMigration -and $DatabaseState -ceq 'owned-companion') 'Companion validation used the wrong observation or migration mode.'
 Record 'exact-successor';'New'
}
function Confirm-PassageWindowEligibilityMigrationOutcome {
 param($State,$ReadState)
 Check ((&$ReadState) -ceq 'owned-web') 'Successor reconciliation did not inspect the web database.'
 Record 'successor';'New'
}
function Assert-DeploymentSourceJournalReleasePayloadSet {param($Candidate);Record 'three-pins'}
function Assert-SourceAccessConfigurationChange {param($Change);Record 'policy'}
function Assert-SourceJournalHelperBindings {param($State,$Ports);Record 'helper-bindings'}
$windowEligibilityMigrationState=@{Applied=$true;Identity='owned'}
$sourceJournalHelperState=@{Completed=$true;Policy=@{};OriginalTask=@{Enabled=$true;Running=$true}}
$sourceJournalNativePorts=@{ReadTask={@{Enabled=$true;Running=$true}}}
foreach($failure in @('successor','exact-successor','three-pins','policy','helper-bindings','')){
 $events.Clear();$injected=$failure
 if($failure){
  Refuse {Assert-PassageWindowEligibilityActivation}
  Check ($events.Count -gt 0 -and $events[-1] -ceq $failure) 'Activation failed before reaching the injected boundary.'
 }else{Assert-PassageWindowEligibilityActivation}
}
$sourceJournalNativePorts.ReadTask={@{Enabled=$false;Running=$false}}
Refuse {Assert-PassageWindowEligibilityActivation}
$text=$ast.Extent.Text
Check (($text.Split('if($ApplyTypedSchemaMigration){Assert-PassageWindowEligibilityActivation}').Length-1) -eq 2) 'Start and hold-release must both revalidate the exact successor, pins and helper.'
Check ($text.Contains('window-eligibility-forward-recovery-required-retain-hold')) 'Mixed payload failure lost forward recovery.'
Write-Output 'Window eligibility helper-before-DDL, interruption, no replay and activation boundaries passed.'
