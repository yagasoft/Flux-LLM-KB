[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-deletion-cleanup-migration.psm1') -Force
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count -ne 0) { throw 'The updater does not parse.' }
foreach ($name in @('Assert-NotReparsePoint','Write-SourceDeletionAcceptanceReceipt','Read-SourceDeletionAcceptanceReceipt',
    'Assert-SourceDeletionAcceptanceBinding','Assert-SourceDeletionAcceptanceMaintenance','Enter-SourceDeletionAcceptanceMaintenance',
    'Get-SourceDeletionCleanupOriginalTask')) {
    $definition=$ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name },$true)
    if ($null -eq $definition) { throw "Missing maintenance implementation: $name" }
    Invoke-Expression $definition.Extent.Text
}
function Assert-True([bool]$Value,[string]$Message) { if (!$Value) { throw $Message } }
function Assert-Rejected([scriptblock]$Action,[string]$Message) {
    $rejected=$false
    try { & $Action | Out-Null } catch { $rejected=$true }
    Assert-True $rejected $Message
}
function Get-SourceDeletionAcceptanceBinding { $actual }
function Assert-StoppedPoolRecoveryBaseline {
    if ($calls.Pool -cne 'Stopped' -or $calls.Workers -ne 0) { throw 'Workers remain.' }
}
function Assert-SourceDeletionAcceptanceCpuOwnerFree {
    if ($calls.CpuOwner) { throw 'Native owner remains.' }
}
function Get-RepositoryRecoveryTaskObservation { $actual.Task }
function Disable-ScheduledTask {
    param($TaskName,$TaskPath)
    Assert-True (Test-Path -LiteralPath ($receiptPath+'.prepared.json')) 'Task mutation preceded original policy receipt.'
    $calls.Disables++; $actual.Task.Enabled=$false; $actual.Task.State='Disabled'
}
function Wait-InteractiveHostStopped {
    param($TaskName,$TimeoutSeconds)
    if ($actual.Task.State -ceq 'Running') { throw 'Companion still running.' }
}
function Stop-HybridIisAfterGpuDrain {
    param([switch]$PassThru)
    $calls.Drains++
    Assert-True (!$actual.Task.Enabled) 'Drain preceded companion disable.'
    if ($calls.StopFailure) { throw 'Native exit not proven.' }
    $calls.Pool='Stopped'; $calls.Workers=0
    [pscustomobject]@{ ExitedIdentities=@(@{ ProcessId=123; Created='2026-10-15T01:00:00Z' }); VerifiedAtUtc='2026-10-15T05:00:00Z' }
}
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('flux-acceptance-maintenance-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$ValidationHoldPath=Join-Path $testRoot 'hold.json'
$InteractiveHostTaskName='FluxKnowledge.OutlookHost'; $ReadinessTimeoutSeconds=1
try {
    foreach ($enabled in @($true,$false)) {
        $receiptPath=Join-Path $testRoot ('stopped-'+$enabled+'.json')
        $original=@{
            IisSha256='iis'; WebPayloadSha256='web'; CompanionPayloadSha256='companion';
            ConfigurationSha256='config'; CompanionConfigurationSha256='companion-config';
            Task=@{ Enabled=$enabled; State='Ready'; Xml='<Task/>'; IdentityHash='task'; LauncherPath='launcher'; LauncherSha256='launcher-hash' };
            Database=[pscustomobject]@{ Identity='db'; History=@('20261004175522_AddRepositoryRecoveryAuthority');
                CanAlter=$true; ColumnCount=0; CheckCount=0; RunnableCount=0; LegacySha256='legacy' }
        }
        $actual=($original | ConvertTo-Json -Depth 12 | ConvertFrom-Json -AsHashtable)
        $calls=@{ Pool='Started'; Workers=1; CpuOwner=$false; Disables=0; Drains=0; StopFailure=$false }
        $result=Enter-SourceDeletionAcceptanceMaintenance -ReceiptPath $receiptPath -Original $original -ImplementationSha256 ('A'*64)
        $hash=(Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash
        Assert-True ($result.Sha256 -ceq $hash -and $calls.Pool -ceq 'Stopped' -and $calls.Drains -eq 1) 'Stop was not durably certified.'
        $saved=Assert-SourceDeletionAcceptanceMaintenance -ReceiptPath $receiptPath -ExpectedSha256 $hash
        $task=Get-SourceDeletionCleanupOriginalTask -Maintenance $saved
        Assert-True ($task.Enabled -eq $enabled -and $saved.ExitProof.ExitedIdentities[0].ProcessId -eq 123) 'Original task policy or exact process proof lost.'
        Assert-Rejected { Enter-SourceDeletionAcceptanceMaintenance -ReceiptPath $receiptPath -Original $original -ImplementationSha256 ('A'*64) } 'Repeated entry rewrote receipts.'
        Assert-True ($calls.Drains -eq 1) 'Repeated entry performed another stop.'
        Assert-Rejected { Assert-SourceDeletionAcceptanceMaintenance -ReceiptPath $receiptPath -ExpectedSha256 ('B'*64) } 'Changed receipt admitted.'
        foreach ($field in @('IisSha256','WebPayloadSha256','CompanionPayloadSha256','ConfigurationSha256','CompanionConfigurationSha256')) {
            $value=$actual[$field]; $actual[$field]='changed'
            Assert-Rejected { Assert-SourceDeletionAcceptanceMaintenance -ReceiptPath $receiptPath -ExpectedSha256 $hash } "Binding drift admitted: $field"
            $actual[$field]=$value
        }
        foreach ($field in @('IdentityHash','LauncherSha256')) {
            $value=$actual.Task[$field]; $actual.Task[$field]='changed'
            Assert-Rejected { Assert-SourceDeletionAcceptanceMaintenance -ReceiptPath $receiptPath -ExpectedSha256 $hash } "Task drift admitted: $field"
            $actual.Task[$field]=$value
        }
        foreach ($field in @('Identity','LegacySha256','History')) {
            $value=$actual.Database[$field]; $actual.Database[$field]='changed'
            Assert-Rejected { Assert-SourceDeletionAcceptanceMaintenance -ReceiptPath $receiptPath -ExpectedSha256 $hash } "Database drift admitted: $field"
            $actual.Database[$field]=$value
        }
        $actual.Database.RunnableCount=1
        Assert-Rejected { Assert-SourceDeletionAcceptanceMaintenance -ReceiptPath $receiptPath -ExpectedSha256 $hash } 'Queued deletion admitted.'
        $actual.Database.RunnableCount=0
        foreach ($state in @('pool','worker','enabled','running','owner','hold')) {
            switch ($state) {
                pool { $calls.Pool='Started' }
                worker { $calls.Workers=1 }
                enabled { $actual.Task.Enabled=$true }
                running { $actual.Task.State='Running' }
                owner { $calls.CpuOwner=$true }
                hold { [IO.File]::WriteAllText($ValidationHoldPath,'"foreign"') }
            }
            Assert-Rejected { Assert-SourceDeletionAcceptanceMaintenance -ReceiptPath $receiptPath -ExpectedSha256 $hash } "Execution fence drift admitted: $state"
            $calls.Pool='Stopped'; $calls.Workers=0; $calls.CpuOwner=$false; $actual.Task.Enabled=$false; $actual.Task.State='Disabled'
            if (Test-Path -LiteralPath $ValidationHoldPath) { Remove-Item -LiteralPath $ValidationHoldPath }
        }
    }
    $receiptPath=Join-Path $testRoot 'interrupted.json'
    $calls.Pool='Started'; $calls.Workers=1; $calls.StopFailure=$true
    Assert-Rejected { Enter-SourceDeletionAcceptanceMaintenance -ReceiptPath $receiptPath -Original $original -ImplementationSha256 ('A'*64) } 'Unproved native stop certified.'
    Assert-True (!(Test-Path -LiteralPath $receiptPath) -and (Test-Path -LiteralPath ($receiptPath+'.prepared.json')) -and !$actual.Task.Enabled) 'Interrupted maintenance lost evidence or re-enabled execution.'
    Assert-Rejected { Assert-SourceDeletionAcceptanceMaintenance -ReceiptPath $receiptPath -ExpectedSha256 ('A'*64) } 'Interrupted maintenance admitted.'
    $task=Get-SourceDeletionCleanupOriginalTask
    Assert-True (!$task.Enabled) 'Ordinary migration task policy changed.'

    # Execute the updater's policy assignment and success-path enable block.
    $policyAssignment=$ast.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -ceq '$interactiveHostTaskWasEnabled' -and $node.Right.Extent.Text -match 'sourceDeletionMaintenance' },$true)
    $enableBlock=$ast.Find({ param($node) $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -ceq '$interactiveHostTaskWasEnabled' },$true)
    Assert-True ($null -ne $policyAssignment -and $null -ne $enableBlock) 'Updater task-policy boundaries missing.'
    function Enable-ScheduledTask { param($TaskName); $calls.Enables++ }
    foreach ($policy in @($true,$false)) {
        $calls.Enables=0
        $interactiveHostTask=@{ Settings=@{ Enabled=$false } }
        $sourceDeletionMaintenance=@{ Original=@{ Task=@{ Enabled=$policy } } }
        Invoke-Expression $policyAssignment.Extent.Text
        Invoke-Expression $enableBlock.Extent.Text
        Assert-True ($calls.Enables -eq [int]$policy) 'Successful updater failed to restore the original enabled policy.'
    }

    # Use the real stop helper: exact parent/child identities escape only after
    # the drain's post-stop proof succeeds. No forced termination is mocked.
    foreach ($name in @('Stop-HybridIisAfterGpuDrain')) {
        $definition=$ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name },$true)
        Invoke-Expression $definition.Extent.Text
    }
    function Get-DeploymentSqlConnectionString { 'disposable-mock' }
    function Invoke-WithHybridGpuDrain {
        param($ConnectionString,$TimeoutSeconds,$StopApplication)
        & $StopApplication 42
        if ($calls.ProofLost) { throw 'GPU drain session proof lost.' }
    }
    function Get-HybridIisWorkerIds { param($AppCmdPath,$PoolName); if ($calls.Pool -ceq 'Started') { 123 } }
    function Get-CimInstance {
        param($ClassName,$Filter)
        if (!$Filter) {
            [pscustomobject]@{ ProcessId=123; ParentProcessId=0; CreationDate='parent-time' }
            [pscustomobject]@{ ProcessId=124; ParentProcessId=123; CreationDate='child-time' }
            [pscustomobject]@{ ProcessId=125; ParentProcessId=124; CreationDate='grandchild-time' }
        }
    }
    function Get-WebAppPoolState { param($Name); @{ Value=$calls.Pool } }
    function Stop-WebAppPool { param($Name); $calls.Pool='Stopped' }
    function Wait-IisAppPoolState { param($Name,$ExpectedState,$TimeoutSeconds); Assert-True ($calls.Pool -ceq $ExpectedState) 'Pool stop unproved.' }
    $RebuildTimeoutSeconds=1; $SiteName='disposable-pool'
    $calls.ProofLost=$false; $calls.Pool='Started'
    $proof=Stop-HybridIisAfterGpuDrain -PassThru
    Assert-True ($proof.ExitedIdentities.Count -eq 3 -and $proof.ExitedIdentities[2].ProcessId -eq 125 -and
        $proof.ExitedIdentities[2].Created -ceq 'grandchild-time') 'Exact native worker/descendant exit proof was lost.'
    $calls.ProofLost=$true; $calls.Pool='Started'
    Assert-Rejected { Stop-HybridIisAfterGpuDrain -PassThru } 'Lost GPU drain session certified the stop.'

    # IIS pool names are case-insensitive. Exercise the actual topology reader
    # so a second application cannot evade the exclusive-pool requirement.
    Add-Type -TypeDefinition @'
using System.Collections;
using System.Collections.Generic;
public sealed class SourceDeletionMaintenanceTestSites : IEnumerable {
    readonly IDictionary<string,object> sites;
    public SourceDeletionMaintenanceTestSites(IDictionary<string,object> values) { sites=values; }
    public object this[string name] { get { return sites[name]; } }
    public IEnumerator GetEnumerator() { return sites.Values.GetEnumerator(); }
}
'@
    foreach ($name in @('Get-SourceDeletionAcceptanceIisHash','Assert-CanonicalPath')) {
        $definition=$ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name },$true)
        Invoke-Expression $definition.Extent.Text
    }
    $SiteName='FluxKnowledge'; $CanonicalDeployRoot=Join-Path $testRoot 'App'
    $webApplication=[pscustomobject]@{ ApplicationPoolName='FluxKnowledge'; Path='/'; VirtualDirectories=@{ '/'=@{ PhysicalPath=$CanonicalDeployRoot } } }
    $webSite=[pscustomobject]@{ Name='FluxKnowledge'; State='Started'; Id=1; Applications=@($webApplication);
        Bindings=@([pscustomobject]@{ Protocol='http'; BindingInformation='127.0.0.1:5137:' }) }
    $sites=[Collections.Generic.Dictionary[string,object]]::new(); $sites.Add('FluxKnowledge',$webSite)
    $manager=[pscustomobject]@{ Sites=[SourceDeletionMaintenanceTestSites]::new($sites); ApplicationPools=@{ FluxKnowledge=@{} } }
    $manager | Add-Member -MemberType ScriptMethod -Name Dispose -Value { }
    function New-IncrementalIisServerManager { $manager }
    function Get-IncrementalIisHostingSettings { param($Manager); @{ StartMode='AlwaysRunning'; IdleTimeout='00:00:00'; PreloadEnabled=$true; ModuleEnabled=$true } }
    $singleHash=Get-SourceDeletionAcceptanceIisHash
    $otherApplication=[pscustomobject]@{ ApplicationPoolName='fluxknowledge'; Path='/'; VirtualDirectories=@{ '/'=@{ PhysicalPath='unrelated' } } }
    $sites.Add('Other',[pscustomobject]@{ Name='Other'; Applications=@($otherApplication) })
    Assert-Rejected { Get-SourceDeletionAcceptanceIisHash } 'Case-variant shared IIS pool admitted.'
    $sites.Remove('Other') | Out-Null
    $webSite.Bindings[0].BindingInformation='127.0.0.1:9999:'
    Assert-Rejected { Get-SourceDeletionAcceptanceIisHash } 'Wrong production endpoint admitted.'
} finally {
    if ((Resolve-Path -LiteralPath $testRoot).Path -cne $testRoot) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
Write-Output 'Source deletion acceptance maintenance passed: original policy, durable exact exit, drift rejection, interruption and repeated-entry refusal.'
