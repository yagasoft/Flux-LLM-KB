[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper-task.psm1') -Force
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'The incremental updater does not parse.' }
$swap = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -ceq 'Invoke-IncrementalApplicationPayloadSwap'
}, $true))[-1]
$outer = $swap.Parent
while ($null -ne $outer -and $outer -isnot [Management.Automation.Language.TryStatementAst]) { $outer = $outer.Parent }
if ($null -eq $outer -or $outer.CatchClauses.Count -ne 1 -or $null -eq $outer.Finally) {
    throw 'The ordinary updater recovery boundary is missing.'
}
$afterSwap = [scriptblock]::Create((@($outer.Body.Statements | Where-Object {
    $_.Extent.StartOffset -gt $swap.Extent.EndOffset
}).Extent.Text -join "`n"))
$recover = [scriptblock]::Create(($outer.CatchClauses[0].Body.Statements.Extent.Text -join "`n"))
$finish = [scriptblock]::Create(($outer.Finally.Statements.Extent.Text -join "`n"))
function Assert-True([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
# Native control-flow port; actual SQL admission is covered by index-format-admission.ps1.
function Invoke-DeploymentIndexAdmission { param($Roles,$Action) & $Action }
$enableDefinition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Enable-IndexCompatibleOutlookTask'},$true)
Assert-True ($null -ne $enableDefinition) 'The admitted task callback is missing.'
Invoke-Expression $enableDefinition.Extent.Text
# This fixture exercises the legacy baseline rollback path. Protected and
# unavailable journal boundaries have their own actual recovery tests.
$journalGuard=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -ceq 'Assert-DeploymentSourceJournalPredecessorRestorationAllowed'},$true)
Assert-True ($null -ne $journalGuard) 'The predecessor restoration guard is missing.'
Invoke-Expression $journalGuard.Extent.Text
function Get-DeploymentSourceJournalBoundary { @{Protected=$false} }
function Remove-DeploymentValidationHold {
    param($Path, $ReleaseId)
    $calls.Removes++
    if ($failurePoint -ceq 'remove-before') { throw 'injected removal failure before deletion' }
    [IO.File]::Delete($Path)
    if ($failurePoint -ceq 'remove-after') { throw 'injected lost removal acknowledgement' }
}
function Test-InteractiveHostPayload { param($Path)
    Assert-True (Test-Path -LiteralPath (Join-Path $Path 'version')) 'Companion bytes missing.'
}
function Restore-InteractiveHostPayload { param($PreviousRoot, $LiveRoot)
    $calls.Restores++
    [IO.File]::WriteAllText((Join-Path $LiveRoot 'version'), [IO.File]::ReadAllText((Join-Path $PreviousRoot 'version')))
}
function Enable-ScheduledTask { param($TaskName) $calls.Enables++ }
function Complete-RecoveryReferenceWrite { param($Lease)
    Assert-True ([object]::ReferenceEquals($Lease, $recoveryWriter)) 'The wrong Recovery writer was completed.'
    $calls.WriterCompletions++
}
function Close-RecoveryReferenceWrite { param($Lease)
    Assert-True ([object]::ReferenceEquals($Lease, $recoveryWriter)) 'The wrong Recovery writer was closed.'
    $calls.WriterClosures++
}
function Invoke-RequiredLoopbackProbes { param($Origin, $TimeoutSeconds)
    if ($failurePoint -ceq 'post-release-probe') { throw 'injected post-release probe failure' }
}
function Test-IncrementalRollbackHoldRelease {
    param($Validation, $ApplyMigrations, $ApplyCorpusChunkFullTextMigration, $CorpusRollbackVerified,
        $ApplyCodeDisclosureProofMigration, $CodeDisclosureRecoveryVerified, $ApplyResumableSourceDeletionCleanupMigration,
        $InteractiveHostMutationStarted, $InteractiveHostRollbackVerified, $RecoverStoppedPool, $HostingRollbackVerified)
    $calls.CleanupChecks++
    # Model a previously verified rollback: release attempts must bypass this gate entirely.
    return $true
}
$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('flux-release-boundary-' + [guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    foreach ($failurePoint in @('before-boundary', 'remove-before', 'remove-after', 'post-release-probe', 'none')) {
        $releaseRoot = Join-Path $testRoot $failurePoint
        $CanonicalDeployRoot = Join-Path $releaseRoot 'web'
        $InteractiveHostRoot = Join-Path $releaseRoot 'companion'
        $interactiveHostPreviousRoot = Join-Path $releaseRoot 'previous-companion'
        New-Item -ItemType Directory -Path $CanonicalDeployRoot, $InteractiveHostRoot, $interactiveHostPreviousRoot | Out-Null
        [IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'version'), 'new')
        [IO.File]::WriteAllText((Join-Path $InteractiveHostRoot 'version'), 'new')
        [IO.File]::WriteAllText((Join-Path $interactiveHostPreviousRoot 'version'), 'old')
        $ValidationHoldPath = Join-Path $releaseRoot 'hold.json'
        [IO.File]::WriteAllText($ValidationHoldPath, '"owned"')
        $releaseId = 'owned'; $commit = 'd' * 40; $SiteName = 'disposable'; $InteractiveHostTaskName = 'disposable'
        $ApplyResumableSourceDeletionCleanupMigration = $false; $ApplyMigrations = $false
        $ApplyCorpusChunkFullTextMigration = $false; $ApplyCodeDisclosureProofMigration = $false
        $RecoverStoppedPool = $false; $EnableUnattendedDiscovery = $false; $DeferReadinessForScopedRemediation = $false
        $sourceDeletionCleanupState = $null; $corpusMigrationState = $null; $codeDisclosureMigrationState = $null
        $sourceAccessChange = $null; $recoveryWriter = [object]::new()
        $sourceJournalHelperState = $null; $sourceJournalNativePorts = $null
        $sourceJournalMode = $false; $ApplyIncrementalSourceScanning = $false; $sourceJournalChange = $null
        $holdReleaseAttempted = $false; $holdReleased = $false
        $interactiveHostMutationStarted = $true; $interactiveHostTaskWasEnabled = $true; $interactiveHostRollbackVerified = $false
        $deploymentValidation = @{ HoldCreated = $true; PayloadRollbackVerified = $true; RollbackVerified = $true }
        $hostingChange = @{ RollbackVerified = $true; PreloadReceipt = $null }
        $swap = @{ PreviousPayload = 'disposable-previous' }; $applyAnyMigration = $false
        $loopbackOrigin = @{ Origin = 'http://127.0.0.1:1' }; $ReadinessTimeoutSeconds = 1
        $calls = @{ Removes = 0; Restores = 0; Enables = 0; CleanupChecks = 0; WriterCompletions = 0; WriterClosures = 0 }
        $mutex = [Threading.Mutex]::new($false)
        $leaseAcquired = $mutex.WaitOne(0)
        $reported = $null
        try {
            try {
                if ($failurePoint -ceq 'before-boundary') {
                    # The inner payload swap already restored the web side before propagating failure.
                    [IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'version'), 'old')
                    throw 'injected held swap failure'
                }
                . $afterSwap | Out-Null
            }
            catch { . $recover }
        }
        catch { $reported = $_.Exception.Message }
        finally { try { . $finish } catch { $reported = $_.Exception.Message } }
        $web = [IO.File]::ReadAllText((Join-Path $CanonicalDeployRoot 'version'))
        $companion = [IO.File]::ReadAllText((Join-Path $InteractiveHostRoot 'version'))
        Assert-True ($calls.WriterClosures -eq 1 -and $calls.WriterCompletions -eq $(if ($failurePoint -ceq 'none') { 1 } else { 0 })) `
            'The owned Recovery writer must close on every outcome and complete only after successful release validation.'
        if ($failurePoint -ceq 'before-boundary') {
            Assert-True ($web -ceq 'old' -and $companion -ceq 'old' -and $calls.Restores -eq 1 -and
                !$holdReleaseAttempted -and $calls.CleanupChecks -eq 1 -and !(Test-Path -LiteralPath $ValidationHoldPath)) `
                'A pre-boundary failure lost the existing verified paired rollback behaviour.'
        }
        else {
            Assert-True ($holdReleaseAttempted -and $web -ceq 'new' -and $companion -ceq 'new' -and
                $calls.Restores -eq 0 -and $calls.CleanupChecks -eq 0 -and $calls.Removes -eq 1) `
                "Release attempt allowed predecessor restoration or cleanup replay at $failurePoint."
            Assert-True ((Test-Path -LiteralPath $ValidationHoldPath) -eq ($failurePoint -ceq 'remove-before')) `
                'The updater changed the hold after an ambiguous release.'
            if ($failurePoint -ceq 'none') {
                Assert-True ($null -eq $reported -and $holdReleased -and $calls.Enables -eq 1) 'Successful deployment bookkeeping changed.'
            }
            else {
                Assert-True ($reported -like '*requires reconciliation*' -and $calls.Enables -eq 0) `
                    'An attempted release failure did not retain the pair for forward recovery.'
            }
        }
    }
}
finally {
    if ((Resolve-Path -LiteralPath $testRoot).Path -cne $testRoot) { throw 'Unexpected test cleanup path.' }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
Write-Output 'Ordinary release boundary passed: pre-release paired rollback, ambiguous removal on both sides of deletion, post-release failure and success.'
