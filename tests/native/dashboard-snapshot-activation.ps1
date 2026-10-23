param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-dashboard-snapshots.psm1') -Force
function Expect-Failure([scriptblock]$Action,[string]$Message){try{& $Action}catch{if($_.Exception.Message -match $Message){return};throw};throw ('Expected refusal: '+$Message)}
$fixture=[IO.Directory]::CreateTempSubdirectory('FluxDashboardSnapshot_').FullName
$path=Join-Path $fixture 'settings.json'
$original=[Text.Encoding]::UTF8.GetBytes('{"Dashboard":{"Other":42},"Worker":{"Enabled":true},"ConnectionStrings":{"Unchanged":"private fixture"}}')
[IO.File]::WriteAllBytes($path,$original)
$acl=(Get-Acl -LiteralPath $path).Sddl
$change=New-DashboardSnapshotConfigurationChange -Path $path
if((Get-FileHash -LiteralPath $path).Hash -cne $change.BeforeHash){throw 'Preparation mutated configuration.'}
Set-DashboardSnapshotConfigurationChange $change
Assert-DashboardSnapshotConfigurationChange $change
$active=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
if($active.Dashboard.ReadConsistency -cne 'Snapshot' -or $active.Dashboard.Other -ne 42 -or !$active.Worker.Enabled -or
    $active.ConnectionStrings.Unchanged -cne 'private fixture' -or (Get-Acl -LiteralPath $path).Sddl -cne $acl){throw 'Unrelated configuration/security changed.'}
Restore-DashboardSnapshotConfigurationChange $change
if([Convert]::ToHexString([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToHexString($original)){throw 'Original configuration bytes not restored.'}
[IO.File]::AppendAllText($path,' ')
Expect-Failure {Set-DashboardSnapshotConfigurationChange $change} 'drift'
[IO.File]::WriteAllBytes($path,$original)
Set-DashboardSnapshotConfigurationChange $change
[IO.File]::AppendAllText($path,' ')
Expect-Failure {Restore-DashboardSnapshotConfigurationChange $change} 'drift'
$checks=5
foreach($initial in @(0,1)) {
    $binding=@{DatabaseIdentity='fixture|database|incarnation';InitialSnapshotState=$initial;IntendedSnapshotState=1;RcsiEnabled=$false;AdrEnabled=$false}
    $observed=@{Identity=$binding.DatabaseIdentity;SnapshotState=$initial;RcsiEnabled=$false;AdrEnabled=$false}
    $state=@{};$journal=[Collections.Generic.List[string]]::new();$calls=@{Enable=0}
    Invoke-DashboardSnapshotActivation -State $state -Binding $binding -ReadState {$observed.Clone()} -Enable {
        if(!$state.DashboardSnapshotActivation.IntentUtc -or $journal.Count -eq 0){throw 'Option changed before durable intent.'}
        $calls.Enable++;$observed.SnapshotState=1
    } -WriteState {$journal.Add(($state|ConvertTo-Json -Depth 10 -Compress))}
    if($state.DashboardSnapshotActivation.Status -cne 'ConfirmedOn' -or $calls.Enable -ne $(if($initial -eq 0){1}else{0}) -or $journal.Count -lt 2){throw 'Initial ON/OFF activation failed.'}
    Assert-DashboardSnapshotActivation -State $state -Binding $binding -ReadState {$observed.Clone()}
    Expect-Failure {Invoke-DashboardSnapshotActivation -State $state -Binding $binding -ReadState {$observed.Clone()} -Enable {throw 'must not replay'} -WriteState {}} 'reconciliation'
    $observed.SnapshotState=3
    Expect-Failure {Assert-DashboardSnapshotActivation -State $state -Binding $binding -ReadState {$observed.Clone()}} 'fully-on'
    $checks+=4
}
foreach($failure in @('interrupted-transition','interrupted-on','unknown-after','intent-write','wrong-database','rcsi-drift','adr-drift')) {
    $binding=@{DatabaseIdentity='fixture|database|incarnation';InitialSnapshotState=0;IntendedSnapshotState=1;RcsiEnabled=$false;AdrEnabled=$false}
    $observed=@{Identity=$binding.DatabaseIdentity;SnapshotState=0;RcsiEnabled=$false;AdrEnabled=$false}
    $state=@{};$calls=@{Enable=0;Read=0;Writes=0}
    if($failure -eq 'wrong-database'){$observed.Identity='another-database'}
    if($failure -eq 'rcsi-drift'){$observed.RcsiEnabled=$true}
    if($failure -eq 'adr-drift'){$observed.AdrEnabled=$true}
    $refused=$false
    try {
        Invoke-DashboardSnapshotActivation -State $state -Binding $binding -ReadState {
            $calls.Read++;if($failure -eq 'unknown-after' -and $calls.Read -gt 1){throw 'synthetic observation failure'};$observed.Clone()
        } -Enable {
            $calls.Enable++;$observed.SnapshotState=$(if($failure -eq 'interrupted-on'){1}else{3});throw 'synthetic option interruption'
        } -WriteState {$calls.Writes++;if($failure -eq 'intent-write'){throw 'synthetic durable intent failure'}}
    } catch {$refused=$true}
    if(!$refused){throw ('Activation accepted '+$failure)}
    if($failure -in @('wrong-database','rcsi-drift','adr-drift','intent-write') -and $calls.Enable){throw 'Activation continued past a binding/intent failure.'}
    if($failure -like 'interrupted-*' -or $failure -eq 'unknown-after') {
        if($calls.Read -lt 2 -or $calls.Writes -lt 2 -or !$state.DashboardSnapshotActivation.IntentUtc -or
            $state.DashboardSnapshotActivation.Status -cne 'ReconciliationRequired'){throw 'Interrupted activation lost durable intent or resulting state.'}
        Expect-Failure {Assert-DashboardSnapshotActivation -State $state -Binding $binding -ReadState {$observed.Clone()}} 'reconciliation'
    }
    $checks++
}
# No mutation or option reversal is performed by the startup guard.
@{Status='PASS';Checks=$checks;ProductionAccess=$false;Fixture=$fixture}|ConvertTo-Json
