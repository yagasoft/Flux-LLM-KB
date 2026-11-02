[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$tokens=$null;$errors=$null
$path=Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Updater does not parse.'}
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Refuses([scriptblock]$Action){$failed=$false;try{&$Action}catch{$failed=$true};Assert-True $failed 'Expected refusal.'}
# Evaluate the real final success report with the ordinary hosting mode. A
# reporting exception after hold release must not disguise a committed update.
$hostingInitialiser=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.AssignmentStatementAst] -and
 $n.Left -is [Management.Automation.Language.VariableExpressionAst] -and $n.Left.VariablePath.UserPath -ceq 'hostingChange'},$true))
$successReport=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.HashtableAst] -and
 @($n.KeyValuePairs|Where-Object {$_.Item1.Extent.Text -ceq 'unattended_discovery_startup'}).Count -eq 1},$true))
Assert-True ($hostingInitialiser.Count -eq 1 -and $successReport.Count -eq 1) 'Canonical success report or hosting initialiser is ambiguous.'
& {
 . ([scriptblock]::Create($hostingInitialiser[0].Extent.Text))
 $EnableUnattendedDiscovery=$false;$sourceAccessChange=$null;$dashboardSnapshotChange=$null;$sourceJournalMode=$false
 $sourceJournalRelease=@{TransportConfirmed=$false;RetirementConfirmed=$false}
 $ApplyIncrementalSourceScanning=$false;$applyAnyMigration=$true;$DeferReadinessForScopedRemediation=$false
 $commit='a'*40;$releaseRoot='I:\owned\release';$swap=@{PreviousPayload='I:\owned\previous-web'}
 $InteractiveHostRoot='I:\owned\outlook';$InteractiveHostTaskName='owned-task';$interactiveHostPreviousRoot='I:\owned\previous-outlook'
 $migrationPlan=@{Up=@{To='20261101195325_AddVectorPublicationMetadata';Sha256='B'*64}}
 $report=&([scriptblock]::Create($successReport[0].Parent.Extent.Text))
 Assert-True ($report.ok -and $report.mode -ceq 'applied' -and $null -eq $report.unattended_discovery_startup) 'Ordinary hosting success report is incorrect.'
 Assert-True ($report.migration.target -ceq $migrationPlan.Up.To -and $report.migration.script_sha256 -ceq $migrationPlan.Up.Sha256) 'Success report lost the metadata migration binding.'
}
# Exercise the canonical entry point's actual top-level module composition in
# a fresh runspace. Mocks and previously imported modules must not hide exports
# removed by a nested forced reload.
$imports=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.CommandAst] -and
 $n.GetCommandName() -ceq 'Import-Module' -and $n.Extent.StartColumnNumber -eq 1 -and
 $n.Extent.Text.Contains('$PSScriptRoot')},$false))
Assert-True ($imports.Count -ge 10) 'Canonical updater module composition was not found.'
$probe=[PowerShell]::Create()
try {
 $code='param($DeploymentScriptDirectory,$SourceRoot)' + "`n" + '$ErrorActionPreference="Stop"' + "`n" +
  (($imports|ForEach-Object {$_.Extent.Text.Replace('$PSScriptRoot','$DeploymentScriptDirectory')}) -join "`n") + "`n" + @'
foreach($name in @('Get-VectorPublicationMetadataDatabaseState','Invoke-VectorPublicationMetadataMigrationSql','New-VectorPublicationMetadataMigrationState')){
 if(-not(Get-Command $name -ErrorAction SilentlyContinue)){throw ('Canonical metadata export missing: '+$name)}
}
$contract=Get-VectorPublicationMetadataMigrationContract
$history=@(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations') -Filter '*.cs' -File|
 ForEach-Object{[regex]::Matches([IO.File]::ReadAllText($_.FullName),'\[Migration\("(\d{14}_[^"]+)"\)\]')|ForEach-Object{$_.Groups[1].Value}}|
 Where-Object{$_ -cle $contract.Baseline}|Sort-Object -Unique)
Assert-SourceJournalSchema ([pscustomobject]@{Identity='isolated-composition';History=($history+$contract.Target);
 SchemaSha256=$contract.JournalSchemaSha256;VectorPublicationIndexSha256=$contract.IndexSha256})
'@
 $null=$probe.AddScript($code).AddArgument((Join-Path $SourceRoot 'scripts/deploy')).AddArgument($SourceRoot)
 $null=$probe.Invoke()
 Assert-True (!$probe.HadErrors) ('Canonical module composition failed: '+($probe.Streams.Error -join "`n"))
}finally{$probe.Dispose()}
function Import-Guard([string]$Name){
    $definitions=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $Name},$false))
    Assert-True ($definitions.Count -eq 1) ('Required updater guard missing: '+$Name)
    Set-Item -LiteralPath ('Function:script:'+ $Name) -Value (&([scriptblock]::Create($definitions[0].Extent.Text+"`n(Get-Item Function:$Name).ScriptBlock")))
}
Assert-True (@($ast.ParamBlock.Parameters.Name.VariablePath.UserPath) -ccontains 'ApplyVectorPublicationMetadataMigration') 'Explicit vector metadata migration switch is missing.'
Import-Guard 'Assert-VectorPublicationMetadataUpdateOptions'
$normal=@{ApplyVectorPublicationMetadataMigration=$true;SourceRoot=$SourceRoot;PlanOnly=$true;ExpectedSourceJournalDatabaseIdentity='owned-db'}
Assert-VectorPublicationMetadataUpdateOptions $normal
$resume=$normal.Clone();$resume.RecoverStoppedPool=$true;$resume.StoppedRecoveryBindingPath='I:\owned\binding.json';$resume.ExpectedStoppedRecoveryBindingSha256='A'*64
Assert-VectorPublicationMetadataUpdateOptions $resume
foreach($forbidden in @('ApplyMigrations','ApplyCodeDisclosureProofMigration','ApplyIncrementalSourceScanning','ApplyHybridPassageRebuild','EnableUnattendedDiscovery','DeferReadinessForScopedRemediation','OfflineCodeMaintenanceReceipt')){
    $bad=$normal.Clone();$bad[$forbidden]=$true;Refuses {Assert-VectorPublicationMetadataUpdateOptions $bad}
}
$bad=$normal.Clone();$bad.RecoverStoppedPool=$true;Refuses {Assert-VectorPublicationMetadataUpdateOptions $bad}
$bad=$resume.Clone();$bad.ExpectedStoppedRecoveryBindingSha256='wrong';Refuses {Assert-VectorPublicationMetadataUpdateOptions $bad}
$bad=$normal.Clone();$bad.StoppedRecoveryBindingPath='I:\owned\binding.json';Refuses {Assert-VectorPublicationMetadataUpdateOptions $bad}
Import-Guard 'Get-VectorPublicationMetadataUpdatePlan'
$contract=[pscustomobject]@{Baseline='prior';Target='target';UpSha256='B'*64;PriorHistoryCount=78;PriorHistorySha256='C'*64;TimeoutSeconds=120}
function Get-VectorPublicationMetadataMigrationContract {$contract}
function Get-VectorPublicationMetadataStateKind {param($DatabaseState,$ExpectedDatabaseIdentity);if($DatabaseState.Identity -cne $ExpectedDatabaseIdentity){throw 'identity'};$DatabaseState.Kind}
$state=[pscustomobject]@{Identity='owned-db';History=@('prior');Kind='Old';CanAlter=$true;ResolvesDbo=$true}
$sql=[pscustomobject]@{Path='I:\owned\up.sql';Sha256='B'*64}
$plan=Get-VectorPublicationMetadataUpdatePlan -DatabaseState $state -ExpectedDatabaseIdentity 'owned-db' -Sql $sql
Assert-True ($plan.state -ceq 'Old' -and !$plan.already_applied -and $plan.migration_timeout_seconds -eq 120) 'Old state plan is incorrect.'
$state.Kind='New';$state.CanAlter=$false
$plan=Get-VectorPublicationMetadataUpdatePlan -DatabaseState $state -ExpectedDatabaseIdentity 'owned-db' -Sql $sql
Assert-True ($plan.already_applied -and $plan.ddl -ceq 'none') 'New state must require zero DDL.'
$state.Kind='Old';Refuses {Get-VectorPublicationMetadataUpdatePlan -DatabaseState $state -ExpectedDatabaseIdentity 'owned-db' -Sql $sql}
$state.CanAlter=$true;$state.ResolvesDbo=$false;Refuses {Get-VectorPublicationMetadataUpdatePlan -DatabaseState $state -ExpectedDatabaseIdentity 'owned-db' -Sql $sql}
$state.ResolvesDbo=$true;Refuses {Get-VectorPublicationMetadataUpdatePlan -DatabaseState $state -ExpectedDatabaseIdentity 'different' -Sql $sql}
Import-Guard 'Invoke-VectorPublicationMetadataStoppedAttempt'
$script:attempts=0;$script:observations=0;$script:failAttempt=$false
function Invoke-VectorPublicationMetadataMigrationAttempt {param($State,$ReadState,$AssertStopped,$RunUp);$script:attempts++;$State.Attempted=$true;if($script:failAttempt){throw 'injected-lost-result'};$State.Applied=$true}
function Confirm-VectorPublicationMetadataMigrationOutcome {param($State,$ReadState);$script:observations++;if($State.Applied){'New'}else{'Old'}}
$attempt=@{Attempted=$false;Applied=$false;Identity='owned-db'}
Invoke-VectorPublicationMetadataStoppedAttempt $attempt @{} 'I:\owned\up.sql'
Invoke-VectorPublicationMetadataStoppedAttempt $attempt @{} 'I:\owned\up.sql'
Assert-True ($script:attempts -eq 1 -and $script:observations -eq 1) 'Repeated stop replayed migration.'
$script:failAttempt=$true;$attempt=@{Attempted=$false;Applied=$false;Identity='owned-db'}
Refuses {Invoke-VectorPublicationMetadataStoppedAttempt $attempt @{} 'I:\owned\up.sql'}
Invoke-VectorPublicationMetadataStoppedAttempt $attempt @{} 'I:\owned\up.sql'
Assert-True ($script:attempts -eq 2 -and $script:observations -eq 2 -and !$attempt.Applied) 'Failed attempt was replayed by recovery stop.'
Import-Guard 'Invoke-VectorPublicationMetadataPreparation'
$script:events=[Collections.Generic.List[string]]::new();$script:injected=''
function Record([string]$Event){$script:events.Add($Event);if($script:injected -ceq $Event){throw ('injected-'+$Event)}}
function Get-ScheduledTask {param($TaskName,$ErrorAction);@{Settings=@{Enabled=$true}}}
function Disable-ScheduledTask {param($TaskName,$ErrorAction);Record 'disable'}
function Wait-InteractiveHostStopped {param($TaskName,$TimeoutSeconds);Record 'companion-exited'}
function Stop-HybridIisAfterGpuDrain {Record 'drained-stopped'}
function New-DeploymentValidationHold {param($Path,$ReleaseId);Record 'hold'}
function Get-RetainedPipelineStateBaseline {Record 'baseline';@{Captured=$true}}
function Invoke-VectorPublicationMetadataStoppedAttempt {param($State,$Binding,$SqlPath);Record 'intent-and-migration'}
$InteractiveHostTaskName='fixture';$ReadinessTimeoutSeconds=300;$ValidationHoldPath='I:\owned\hold.json'
foreach($failure in @('disable','companion-exited','drained-stopped','hold','baseline','intent-and-migration','')){
    $script:events.Clear();$script:injected=$failure;$deploymentValidation=@{HoldCreated=$false;Baseline=$null}
    $prepare=@{ReleaseId='owned'}
    if($failure){Refuses {Invoke-VectorPublicationMetadataPreparation $prepare @{} 'I:\owned\up.sql'}}
    else {Invoke-VectorPublicationMetadataPreparation $prepare @{} 'I:\owned\up.sql';Record 'first-payload-copy'}
    $expected=@('disable','companion-exited','drained-stopped','hold','baseline','intent-and-migration','first-payload-copy')
    $count=if($failure){[array]::IndexOf($expected,$failure)+1}else{$expected.Count}
    Assert-True (($script:events -join '|') -ceq ($expected[0..($count-1)] -join '|')) 'Preparation crossed a failed stop, hold or migration boundary.'
}
# Compose the actual attempt/receipt module, updater preparation and payload swap
# with disposable files. Only SQL and native process observations are injected.
foreach($name in @('Get-VectorPublicationMetadataMigrationContract','Get-VectorPublicationMetadataStateKind','Invoke-VectorPublicationMetadataMigrationAttempt','Confirm-VectorPublicationMetadataMigrationOutcome')){
    Remove-Item -LiteralPath ('Function:'+ $name)
}
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-vector-publication-metadata-migration.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force
Import-Guard 'Invoke-VectorPublicationMetadataStoppedAttempt'
$contract=Get-VectorPublicationMetadataMigrationContract
$history=@(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations') -Filter '*.cs' -File|
    ForEach-Object{[regex]::Matches([IO.File]::ReadAllText($_.FullName),'\[Migration\("(\d{14}_[^"]+)"\)\]')|ForEach-Object{$_.Groups[1].Value}}|
    Where-Object{$_ -cle $contract.Baseline}|Sort-Object -Unique)
$old=[pscustomobject]@{Identity='owned';History=$history;CanAlter=$true;ResolvesDbo=$true;SchemaSha256=$contract.JournalSchemaSha256;VectorPublicationIndexSha256=$contract.AbsentIndexSha256}
$new=[pscustomobject]@{Identity='owned';History=($history+$contract.Target);CanAlter=$false;ResolvesDbo=$true;SchemaSha256=$contract.JournalSchemaSha256;VectorPublicationIndexSha256=$contract.IndexSha256}
function Get-DeploymentSqlConnectionString {'owned-fake-only'}
function Get-VectorPublicationMetadataDatabaseState {param($ConnectionString);$script:CurrentDatabase}
function Assert-VectorPublicationMetadataStopped {param($State,$Binding);if($script:scenario -ceq 'before-intent'){throw 'injected-before-intent'}}
function Invoke-VectorPublicationMetadataMigrationSql {
    param($ConnectionString,$SqlPath,$ExpectedDatabaseIdentity)
    $script:ddlCalls++
    Assert-True (Test-Path -LiteralPath $script:flowState.IntentPath) 'DDL ran before immutable intent.'
    if($script:scenario -ceq 'after-intent'){throw 'injected-after-intent'}
    $script:CurrentDatabase=$new
    if($script:scenario -ceq 'after-ddl'){throw 'injected-lost-acknowledgement'}
}
function New-DeploymentValidationHold {param($Path,$ReleaseId);[IO.File]::WriteAllText($Path,$ReleaseId)}
$area=Join-Path 'I:\Codex Workspaces\TestTemp\FluxKnowledge' ('VectorUpdateFlow-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($area)
foreach($scenario in @('before-intent','after-intent','after-ddl','after-outlook-copy','after-web-copy','validation','success','already-new')){
    $script:scenario=$scenario;$script:injected='';$script:ddlCalls=0;$script:CurrentDatabase=if($scenario -ceq 'already-new'){$new}else{$old}
    $directory=Join-Path $area $scenario;[void][IO.Directory]::CreateDirectory($directory)
    $web=Join-Path $directory 'web';$candidate=Join-Path $directory 'candidate';$outlook=Join-Path $directory 'outlook'
    foreach($p in @($web,$candidate,$outlook)){[void][IO.Directory]::CreateDirectory($p)}
    [IO.File]::WriteAllText((Join-Path $web 'marker'),'original-web');[IO.File]::WriteAllText((Join-Path $candidate 'marker'),'candidate-web')
    [IO.File]::WriteAllText((Join-Path $outlook 'marker'),'original-outlook')
    $ValidationHoldPath=Join-Path $directory 'hold';$deploymentValidation=@{HoldCreated=$false;Baseline=$null}
    $script:flowState=New-VectorPublicationMetadataMigrationState -DatabaseState $script:CurrentDatabase -ReceiptDirectory $directory -ReleaseId 'owned' -Commit ('a'*40) -SqlSha256 $contract.UpSha256
    $starts=@{Count=0};$failed=$false
    try {
        Invoke-VectorPublicationMetadataPreparation $script:flowState @{} 'I:\owned\up.sql'
        [IO.File]::WriteAllText((Join-Path $outlook 'marker'),'candidate-outlook')
        if($scenario -ceq 'after-outlook-copy'){throw 'injected-after-outlook-copy'}
        $null=Invoke-IncrementalApplicationPayloadSwap -ApplicationRoot $web -CandidateRoot $candidate -PreviousRoot (Join-Path $directory 'previous') -FailedRoot (Join-Path $directory 'failed') `
            -StopApplication {Invoke-VectorPublicationMetadataStoppedAttempt $script:flowState @{} 'I:\owned\up.sql'} `
            -ActivateCandidate {Copy-Item -LiteralPath $candidate -Destination $web -Recurse;if($scenario -ceq 'after-web-copy'){throw 'injected-after-web-copy'}} `
            -StartApplication {$starts.Count++} -ValidateApplication {if($scenario -ceq 'validation'){throw 'injected-validation'}} `
            -AssertRollbackAllowed {throw 'vector-metadata-forward-recovery-required-retain-hold'} -RestartPreviousApplication:$false
    } catch {$failed=$true}
    Assert-True ($failed -eq ($scenario -cnotin @('success','already-new'))) ('Wrong composed outcome: '+$scenario)
    Assert-True (([IO.File]::ReadAllText($ValidationHoldPath)) -ceq 'owned') 'Composed failure lost hold.'
    $expectedCalls=if($scenario -cin @('before-intent','already-new')){0}else{1}
    Assert-True ($script:ddlCalls -eq $expectedCalls) 'Repeated swap stop replayed DDL.'
    Assert-True ($starts.Count -le 1) 'Failure restarted the predecessor.'
    $expectedKind=if($scenario -cin @('before-intent','after-intent')){'Old'}else{'New'}
    Assert-True ((Get-VectorPublicationMetadataStateKind $script:CurrentDatabase 'owned') -ceq $expectedKind) 'Failure changed or downgraded schema.'
    if($scenario -cin @('before-intent','after-intent','after-ddl')){
        Assert-True ([IO.File]::ReadAllText((Join-Path $web 'marker')) -ceq 'original-web' -and [IO.File]::ReadAllText((Join-Path $outlook 'marker')) -ceq 'original-outlook') 'Preparation failure copied a payload.'
    }
}
Write-Output 'Vector metadata updater passed: option/state guards, six preparation boundaries and eight composed intent/DDL/payload-swap outcomes.'
