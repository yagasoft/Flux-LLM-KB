[CmdletBinding()]
param([string]$SourceRoot = '')

$ErrorActionPreference = 'Stop'
if (!$SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) }
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'The incremental updater does not parse.' }
foreach ($name in @('Assert-CanonicalPath', 'Get-IncrementalIisHostingSettings', 'Test-IisHostingSettingsMatch',
    'Set-IncrementalIisHostingSettings', 'Restore-IncrementalIisHostingSettings', 'Wait-IncrementalIisPreloadStartup',
    'Test-IncrementalRollbackHoldRelease')) {
    $definitions = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    if ($definitions.Count -ne 1) { throw "Missing deployment function: $name" }
    Invoke-Expression $definitions[0].Extent.Text
}

$SiteName = 'FluxKnowledge'
$CanonicalDeployRoot = 'I:\FluxKnowledge\App'
$script:original = @{ StartMode='OnDemand'; IdleTimeout='00:20:00'; PreloadEnabled=$false }
$desired = @{ StartMode='AlwaysRunning'; IdleTimeout='00:00:00'; PreloadEnabled=$true }
$script:current = $script:original.Clone()
$script:poolState = 'Stopped'; $script:moduleEnabled = $true
$script:commits = 0; $script:failCommit = ''; $script:physicalPath = $CanonicalDeployRoot

# Disposable native-management port: commits publish only this synthetic configuration.
function New-IncrementalIisServerManager {
    $pool = [pscustomobject]@{ StartMode=$script:current.StartMode; State=$script:poolState
        ProcessModel=[pscustomobject]@{ IdleTimeout=[TimeSpan]::Parse($script:current.IdleTimeout) } }
    $app = [pscustomobject]@{ ApplicationPoolName='FluxKnowledge'; Preload=$script:current.PreloadEnabled
        VirtualDirectories=@{ '/'=[pscustomobject]@{ PhysicalPath=$script:physicalPath } } }
    $app | Add-Member ScriptMethod GetAttributeValue { param($name) $this.Preload }
    $app | Add-Member ScriptMethod SetAttributeValue { param($name,$value)
        if ($name -cne 'preloadEnabled') { throw 'Unexpected application setting.' }; $this.Preload=$value }
    $module = [pscustomobject]@{}
    $module | Add-Member ScriptMethod GetAttributeValue { param($name) 'ApplicationInitializationModule' }
    $section = [pscustomobject]@{ Entries=@($module) }
    $section | Add-Member ScriptMethod GetCollection { if ($script:moduleEnabled) { $this.Entries } }
    $config = [pscustomobject]@{ Section=$section }
    $config | Add-Member ScriptMethod GetSection { param($name)
        if ($name -cne 'system.webServer/modules') { throw 'Unexpected configuration section.' }; $this.Section }
    $manager = [pscustomobject]@{ ApplicationPools=@{ FluxKnowledge=$pool }
        Sites=@{ FluxKnowledge=[pscustomobject]@{ Applications=@{ '/'=$app } } }; Config=$config }
    $manager | Add-Member ScriptMethod GetWebConfiguration { param($site)
        if ($site -cne 'FluxKnowledge') { throw 'Another site was accessed.' }; $this.Config }
    $manager | Add-Member ScriptMethod CommitChanges {
        $script:commits++
        $failure = $script:failCommit; $script:failCommit = ''
        if ($failure -ceq 'before') { throw 'Synthetic pre-commit failure.' }
        $script:current = @{ StartMode=[string]$this.ApplicationPools.FluxKnowledge.StartMode
            IdleTimeout=$this.ApplicationPools.FluxKnowledge.ProcessModel.IdleTimeout.ToString()
            PreloadEnabled=[bool]$this.Sites.FluxKnowledge.Applications['/'].Preload }
        if ($failure -ceq 'after') { throw 'Synthetic lost commit acknowledgement.' }
    }
    $manager | Add-Member ScriptMethod Dispose {}
    $manager
}

function Assert-Rejected {
    param([scriptblock]$Action, [string]$Reason)
    try { & $Action; throw 'Unexpected acceptance.' }
    catch { if ($_.Exception.Message -notmatch $Reason) { throw } }
}

$baseline = Get-IncrementalIisHostingSettings
if (!(Test-IisHostingSettingsMatch $baseline $script:original) -or !$baseline.ModuleEnabled -or $script:commits) {
    throw 'Read-only hosting inspection changed settings or lost the original tuple.'
}
Set-IncrementalIisHostingSettings -Expected $baseline -Desired $desired
if (!(Test-IisHostingSettingsMatch (Get-IncrementalIisHostingSettings) $desired) -or $script:commits -ne 1) {
    throw 'The three hosting settings were not committed together.'
}
Restore-IncrementalIisHostingSettings -Original $baseline -Desired $desired
if (!(Test-IisHostingSettingsMatch (Get-IncrementalIisHostingSettings) $baseline) -or $script:commits -ne 2) {
    throw 'Rollback did not restore the exact original tuple.'
}
Restore-IncrementalIisHostingSettings -Original $baseline -Desired $desired
if ($script:commits -ne 2) { throw 'Already-restored settings caused a duplicate commit.' }

$script:poolState = 'Started'
Assert-Rejected { Set-IncrementalIisHostingSettings -Expected $baseline -Desired $desired } 'stopped'
$script:poolState = 'Stopped'; $script:moduleEnabled = $false
Assert-Rejected { Set-IncrementalIisHostingSettings -Expected $baseline -Desired $desired } 'Initialization'
$script:moduleEnabled = $true; $script:physicalPath = 'C:\another-application'
Assert-Rejected { Set-IncrementalIisHostingSettings -Expected $baseline -Desired $desired } 'canonical'
$script:physicalPath = $CanonicalDeployRoot
$script:current.IdleTimeout = '00:30:00'
Assert-Rejected { Set-IncrementalIisHostingSettings -Expected $baseline -Desired $desired } 'changed'
Assert-Rejected { Restore-IncrementalIisHostingSettings -Original $baseline -Desired $desired } 'changed'
if ($script:commits -ne 2 -or $script:current.IdleTimeout -cne '00:30:00') {
    throw 'A rejected or concurrent configuration was overwritten.'
}

foreach ($failure in @('before','after')) {
    $script:current = $script:original.Clone(); $script:failCommit = $failure
    Assert-Rejected { Set-IncrementalIisHostingSettings -Expected $baseline -Desired $desired } 'failure|acknowledgement'
    $script:failCommit = ''
    Restore-IncrementalIisHostingSettings -Original $baseline -Desired $desired
    if (!(Test-IisHostingSettingsMatch (Get-IncrementalIisHostingSettings) $baseline)) {
        throw "Failed commit ($failure) did not recover the original settings."
    }
}
$validation = @{ HoldCreated=$true; PayloadRollbackVerified=$true; RollbackVerified=$true }
if (Test-IncrementalRollbackHoldRelease -Validation $validation -HostingRollbackVerified $false) {
    throw 'An unverified hosting rollback released the admission hold.'
}
if (!(Test-IncrementalRollbackHoldRelease -Validation $validation -HostingRollbackVerified $true)) {
    throw 'Verified hosting rollback could not complete existing hold recovery.'
}

$script:startupAfter = [DateTime]::UtcNow
$script:workerCreated = $script:startupAfter.AddMilliseconds(50)
$script:eventTime = $script:startupAfter.AddMilliseconds(100)
function Get-HybridIisWorkerIds { param($AppCmdPath,$PoolName)
    if ($PoolName -cne 'FluxKnowledge') { throw 'Unrelated worker inventory.' }; 97 }
function Get-CimInstance { param($ClassName,$Filter,$ErrorAction)
    if ($ClassName -cne 'Win32_Process' -or $Filter -cne 'ProcessId = 97') { throw 'Unexpected process lookup.' }
    [pscustomobject]@{ ProcessId=97; CreationDate=$script:workerCreated } }
function Get-WinEvent { param($FilterHashtable,$ErrorAction)
    if ($script:failEventRead) { throw 'Synthetic event log read denied.' }
    if ($FilterHashtable.LogName -cne 'Application' -or $FilterHashtable.ProviderName -cne 'IIS AspNetCore Module V2' -or
        $FilterHashtable.Id -ne 1032 -or $FilterHashtable.StartTime.Ticks -ne $script:startupAfter.Ticks) {
        throw 'Unscoped or stale startup event query.'
    }
    $script:startupEvents
}
$script:startupEvents = @([pscustomobject]@{ ProcessId=97; TimeCreated=$script:eventTime; RecordId=1
    Properties=@([pscustomobject]@{Value="Application '$CanonicalDeployRoot\' started successfully."}) })
$startup = Wait-IncrementalIisPreloadStartup -AfterUtc $script:startupAfter -TimeoutSeconds 0
if ($startup.WorkerId -ne 97 -or $startup.EventRecordId -ne 1) { throw 'Correct native startup evidence was rejected.' }
foreach ($badEvidence in @('wrong-worker','old-event','wrong-path','reused-pid','no-event')) {
    $script:startupEvents[0].ProcessId=if ($badEvidence -ceq 'wrong-worker') { 98 } else { 97 }
    $script:startupEvents[0].TimeCreated=if ($badEvidence -ceq 'old-event') { $script:startupAfter.AddSeconds(-1) } else { $script:eventTime }
    $script:startupEvents[0].Properties[0].Value=if ($badEvidence -ceq 'wrong-path') { "Application 'C:\another-app\' started successfully." } else { "Application '$CanonicalDeployRoot\' started successfully." }
    $script:workerCreated=if ($badEvidence -ceq 'reused-pid') { $script:eventTime.AddMilliseconds(50) } else { $script:startupAfter.AddMilliseconds(50) }
    if ($badEvidence -ceq 'no-event') { $script:startupEvents=@() }
    Assert-Rejected { Wait-IncrementalIisPreloadStartup -AfterUtc $script:startupAfter -TimeoutSeconds 0 } 'startup evidence'
}
$script:failEventRead=$true
Assert-Rejected { Wait-IncrementalIisPreloadStartup -AfterUtc $script:startupAfter -TimeoutSeconds 0 } 'read denied'
$script:failEventRead=$false

# Execute the updater's actual swap invocation against disposable payloads and ports.
$swaps = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -ceq 'Invoke-IncrementalApplicationPayloadSwap' -and
    $node.Extent.Text.Contains('$hostingChange.Attempted')
}, $true))
if ($swaps.Count -ne 1) { throw 'The hosting-aware payload swap is missing or ambiguous.' }
$module = Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force -PassThru
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path $temporaryParent ('FluxKnowledge-HostingSwap-' + [Guid]::NewGuid().ToString('N'))))
if (!$temporaryRoot.StartsWith($temporaryParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The disposable hosting test root escaped the temporary directory.'
}
function Stop-HybridIisAfterGpuDrain {
    $script:poolState='Stopped'
    if ($script:driftDuringStop) { $script:current.IdleTimeout='00:30:00' }
}
function New-DeploymentValidationHold { param($Path,$ReleaseId) $true }
function Get-RetainedPipelineStateBaseline { [pscustomobject]@{ Identity='retained' } }
function Assert-RetainedPipelineStateUnchanged { param($Baseline,$Current)
    if ($Baseline.Identity -cne $Current.Identity) { throw 'Retained identity changed.' } }
function Invoke-CandidatePayloadActivation { param($CandidateRoot,$ApplicationRoot)
    Copy-Item -LiteralPath $CandidateRoot -Destination $ApplicationRoot -Recurse }
function Start-WebAppPool { param($Name) $script:poolState='Started'; $script:starts++ }
function Wait-IncrementalIisPreloadStartup { param($AfterUtc,$TimeoutSeconds)
    if ($script:probes -ne 0 -or $script:poolState -cne 'Started') { throw 'HTTP probes masked preload startup.' }
    $script:preloadWaits++; [pscustomobject]@{ WorkerId=97; EventRecordId=1 } }
function Wait-IisAppPoolState { param($Name,$ExpectedState,$TimeoutSeconds)
    if ($script:poolState -cne $ExpectedState) { throw 'Pool state did not match.' } }
function Invoke-RequiredLoopbackProbes { param($Origin,$TimeoutSeconds)
    $script:probes++
    if ($script:failProbeOnce) { $script:failProbeOnce=$false; throw 'Synthetic candidate probe failure.' } }
try {
    foreach ($scenario in @('success','probe-failure','lost-acknowledgement','drift')) {
        $scenarioRoot = Join-Path $temporaryRoot $scenario
        $CanonicalDeployRoot = Join-Path $scenarioRoot 'active'
        $candidateRoot = Join-Path $scenarioRoot 'candidate'; $previousRoot=Join-Path $scenarioRoot 'previous'
        $failedRoot = Join-Path $scenarioRoot 'failed'
        New-Item -ItemType Directory -Path $CanonicalDeployRoot,$candidateRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'marker.txt'),'previous')
        [IO.File]::WriteAllText((Join-Path $candidateRoot 'marker.txt'),'candidate')
        $script:physicalPath=$CanonicalDeployRoot; $script:current=$script:original.Clone()
        $script:poolState='Started'; $script:starts=0; $script:probes=0; $script:preloadWaits=0
        $script:driftDuringStop=$scenario -ceq 'drift'; $script:failProbeOnce=$scenario -ceq 'probe-failure'
        $script:failCommit=if ($scenario -ceq 'lost-acknowledgement') { 'after' } else { '' }
        $EnableUnattendedDiscovery=$true; $RecoverStoppedPool=$false; $DeferReadinessForScopedRemediation=$false
        $ApplyMigrations=$false; $ApplyCorpusChunkFullTextMigration=$false; $ApplyCodeDisclosureProofMigration=$false
        $ValidationHoldPath=Join-Path $scenarioRoot 'hold.json'; $releaseId='synthetic-release'
        $ReadinessTimeoutSeconds=1; $loopbackOrigin=@{ Origin='http://127.0.0.1:5137' }
        $releaseRoot=$scenarioRoot
        $deploymentValidation=@{ HoldCreated=$false; Baseline=$null; PayloadRollbackVerified=$false }
        $hostingChange=@{ Original=(Get-IncrementalIisHostingSettings); Desired=$desired; Attempted=$false; RollbackVerified=$true }
        if ($scenario -ceq 'success') {
            Invoke-Expression $swaps[0].Extent.Text | Out-Null
            if (!(Test-IisHostingSettingsMatch (Get-IncrementalIisHostingSettings) $desired) -or
                [IO.File]::ReadAllText((Join-Path $CanonicalDeployRoot 'marker.txt')) -cne 'candidate' -or
                $script:starts -ne 1 -or $script:preloadWaits -ne 1) {
                throw 'The actual swap did not activate the target hosting and payload together.'
            }
        }
        else {
            $expectedError=if ($scenario -ceq 'drift') { 'rollback was unsuccessful' } else { 'prior application payload was restored' }
            Assert-Rejected { Invoke-Expression $swaps[0].Extent.Text } $expectedError
            if ([IO.File]::ReadAllText((Join-Path $CanonicalDeployRoot 'marker.txt')) -cne 'previous') {
                throw 'The actual swap failed to restore predecessor bytes.'
            }
            if ($scenario -ceq 'drift') {
                if ($hostingChange.RollbackVerified -or $deploymentValidation.PayloadRollbackVerified -or
                    $script:starts -ne 0 -or $script:current.IdleTimeout -cne '00:30:00') {
                    throw 'Unknown hosting drift was overwritten, restarted or treated as recovered.'
                }
            }
            elseif (!$hostingChange.RollbackVerified -or !$deploymentValidation.PayloadRollbackVerified -or
                !(Test-IisHostingSettingsMatch (Get-IncrementalIisHostingSettings) $hostingChange.Original)) {
                throw 'The actual rollback restarted without verified prior hosting settings.'
            }
        }
    }
}
finally {
    Remove-Module $module -Force
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
'PASS: atomic scoped hosting changes, exact rollback, drift refusal and hold retention.'
