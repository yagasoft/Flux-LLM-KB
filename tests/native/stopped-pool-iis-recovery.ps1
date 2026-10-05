[CmdletBinding()]
param([string]$SourceRoot = '')
$ErrorActionPreference = 'Stop'
if (-not $SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) }
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$updater = Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($updater, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Updater does not parse.' }
function Import-Function([string]$Name) {
    $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name }, $true))
    if ($definitions.Count -ne 1) { throw "Required function missing: $Name" }
    $functionBody = & ([scriptblock]::Create($definitions[0].Extent.Text + "`n(Get-Item Function:$Name).ScriptBlock"))
    Set-Item -LiteralPath "Function:script:$Name" -Value $functionBody
}
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    try { & $Action } catch { if ($_.Exception.Message -match $Message) { return }; throw }
    throw "Expected failure: $Message"
}

$planOutput = & pwsh -NoProfile -File $updater -SourceRoot $SourceRoot -PlanOnly -RecoverStoppedPool 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "Stopped-pool plan failed: $planOutput" }
$plan = $planOutput | ConvertFrom-Json
if (-not $plan.recover_stopped_pool -or $plan.migrations -or
    $plan.rollback -cne 'restore-prior-payloads-with-pool-stopped-and-hold-retained' -or
    $plan.candidate_validation -cne 'held-loopback-probes-and-unchanged-retained-pipeline-state') { throw 'Recovery plan lost its stopped-pool boundaries.' }
foreach ($incompatible in @('ApplyMigrations','ApplyCorpusChunkFullTextMigration','ApplyHybridPassageRebuild','DeferReadinessForScopedRemediation')) {
    $savedPreference = $ErrorActionPreference
    try { $ErrorActionPreference = 'Continue'; $output = & pwsh -NoProfile -File $updater -PlanOnly -RecoverStoppedPool "-$incompatible" 2>&1 | Out-String; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $savedPreference }
    if ($code -eq 0 -or $output -notmatch 'Stopped-pool recovery cannot') { throw "Recovery accepted incompatible mode: $incompatible" }
}

Import-Function 'Assert-StoppedPoolRecoveryBaseline'
Import-Function 'Test-IncrementalRollbackHoldRelease'
Import-Function 'Assert-NotReparsePoint'
Import-Function 'New-DeploymentValidationHold'
$SiteName = 'FluxKnowledge'
$baseline = @{ Pool = 'Stopped'; Workers = @() }
function Get-WebAppPoolState { param($Name); if ($Name -cne 'FluxKnowledge') { throw 'Wrong pool' }; [pscustomobject]@{Value=$baseline.Pool} }
function Get-HybridIisWorkerIds { param($AppCmdPath,$PoolName); if ($PoolName -cne 'FluxKnowledge') { throw 'Wrong worker pool' }; $baseline.Workers }
Assert-StoppedPoolRecoveryBaseline
$baseline.Pool = 'Started'
Expect-Failure { Assert-StoppedPoolRecoveryBaseline } 'must be stopped'
$baseline.Pool = 'Stopped'; $baseline.Workers = @(404)
Expect-Failure { Assert-StoppedPoolRecoveryBaseline } 'worker'
$baseline.Workers = @()
$validation = @{ HoldCreated=$true; PayloadRollbackVerified=$true; RollbackVerified=$true }
$releaseArguments = @{ Validation=$validation; ApplyMigrations=$false; ApplyCorpusChunkFullTextMigration=$false; CorpusRollbackVerified=$true; InteractiveHostMutationStarted=$true; InteractiveHostRollbackVerified=$true }
if (Test-IncrementalRollbackHoldRelease @releaseArguments -RecoverStoppedPool $true) { throw 'Recovery rollback released its hold.' }
if (-not (Test-IncrementalRollbackHoldRelease @releaseArguments)) { throw 'Ordinary verified rollback changed.' }

$allSwaps = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Invoke-IncrementalApplicationPayloadSwap' }, $true))
$swap = @($allSwaps | Where-Object {
    $parent = $_.Parent
    while ($null -ne $parent -and $parent -isnot [Management.Automation.Language.FunctionDefinitionAst]) { $parent = $parent.Parent }
    $null -eq $parent
})
if ($swap.Count -ne 1 -or $swap[0].Extent.Text -notmatch 'RestartPreviousApplication:\(-not \$RecoverStoppedPool\)') { throw 'Recovery is not wired to suppress predecessor startup.' }

$parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$testRoot = [IO.Path]::GetFullPath((Join-Path $parent ('FluxStoppedRecovery_' + [guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid disposable root.' }
$module = Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force -PassThru
$hashModule = Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force -PassThru
try {
    foreach ($case in @('healthy','start-failure','probe-failure','activation-failure','restore-failure','stop-failure')) {
        $caseRoot = Join-Path $testRoot $case
        $active = Join-Path $caseRoot 'active'; $candidate = Join-Path $caseRoot 'candidate'
        $previous = Join-Path $caseRoot 'previous'; $failed = Join-Path $caseRoot 'failed'; $hold = Join-Path $caseRoot 'hold.json'
        New-Item -ItemType Directory -Path $active,$candidate -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $active 'marker.txt'),'previous')
        [IO.File]::WriteAllText((Join-Path $candidate 'marker.txt'),'candidate')
        $state = @{ Starts=[Collections.Generic.List[string]]::new(); Stopped=$true; Stops=0; Restored=$false }
        $parameters = @{
            ApplicationRoot=$active; CandidateRoot=$candidate; PreviousRoot=$previous; FailedRoot=$failed
            RestartPreviousApplication=$false
            ActivateCandidate={ if ($case -eq 'activation-failure') { throw 'synthetic activation failed' }; Copy-Item -LiteralPath $candidate -Destination $active -Recurse }
            StopApplication={ $state.Stops++; [void](New-DeploymentValidationHold -Path $hold -ReleaseId $case); $state.Stopped=$true; if ($case -eq 'stop-failure') { throw 'synthetic stop proof failed' } }
            StartApplication={ $marker=[IO.File]::ReadAllText((Join-Path $active 'marker.txt')); $state.Starts.Add($marker); if ($marker -cne 'candidate') { throw 'Predecessor was started' }; $state.Stopped=$false; if ($case -eq 'start-failure') { throw 'synthetic start failed' } }
            ValidateApplication={ if ($case -ne 'healthy') { throw 'synthetic probe failed' } }
            PrepareRollbackApplication={ if ($case -eq 'restore-failure') { throw 'synthetic restore verification failed' } }
            ValidateRollbackApplication={ if (-not $state.Stopped -or [IO.File]::ReadAllText((Join-Path $active 'marker.txt')) -cne 'previous') { throw 'Stopped prior payload not restored' }; $state.Restored=$true }
        }
        if ($case -eq 'healthy') {
            $result = Invoke-IncrementalApplicationPayloadSwap @parameters
            if ($result.RolledBack -or $state.Stopped -or $state.Starts.Count -ne 1 -or $state.Starts[0] -cne 'candidate') { throw 'Healthy recovery did not start only the candidate.' }
        }
        else {
            Expect-Failure { Invoke-IncrementalApplicationPayloadSwap @parameters } 'Incremental IIS deployment failed'
            if (-not $state.Stopped -or $state.Starts.Contains('previous') -or -not (Test-Path -LiteralPath $hold)) { throw "Recovery failure did not retain stopped state/hold: $case" }
            if ($case -ne 'restore-failure' -and -not $state.Restored) { throw "Prior payload was not verified: $case" }
            if ($case -eq 'restore-failure' -and $state.Restored) { throw 'Failed restoration was reported verified.' }
        }
    }

    # Exercise the actual updater rollback callback with real disposable bytes.
    # Its recovery branch must prove stopped state and hashes without health probes.
    $elements = $swap[0].CommandElements
    $rollbackCallback = $null
    for ($i=0; $i -lt $elements.Count-1; $i++) {
        if ($elements[$i] -is [Management.Automation.Language.CommandParameterAst] -and
            $elements[$i].ParameterName -ceq 'ValidateRollbackApplication') {
            $rollbackCallback = [scriptblock]::Create($elements[$i+1].ScriptBlock.EndBlock.Extent.Text)
        }
    }
    if ($null -eq $rollbackCallback) { throw 'Rollback callback missing.' }
    $RecoverStoppedPool = $true
    $CanonicalDeployRoot = Join-Path $testRoot 'rollback-bytes'
    New-Item -ItemType Directory -Path $CanonicalDeployRoot -Force | Out-Null
    $markerPath = Join-Path $CanonicalDeployRoot 'marker.txt'
    [IO.File]::WriteAllText($markerPath,'previous')
    $recoveryOriginalPayloadHash = Get-HybridPayloadFingerprint -Path $CanonicalDeployRoot
    $deploymentValidation = @{Baseline='original';PayloadRollbackVerified=$false}
    $comparison = @{Current='original';Calls=0}
    function Get-RetainedPipelineStateBaseline { $comparison.Current }
    function Assert-RetainedPipelineStateUnchanged { param($Baseline,$Current); $comparison.Calls++; if ($Baseline -cne $Current) { throw 'retained state changed' } }
    function Invoke-RequiredLoopbackProbes { throw 'Predecessor health probe is forbidden in recovery rollback' }
    & $rollbackCallback
    if (-not $deploymentValidation.PayloadRollbackVerified -or $comparison.Calls -ne 1) { throw 'Stopped rollback was not verified.' }
    $deploymentValidation.PayloadRollbackVerified=$false
    [IO.File]::WriteAllText($markerPath,'corrupted')
    Expect-Failure { & $rollbackCallback } 'exact prior application bytes'
    if ($deploymentValidation.PayloadRollbackVerified) { throw 'Corrupt restoration was reported verified.' }
    [IO.File]::WriteAllText($markerPath,'previous'); $comparison.Current='changed'
    Expect-Failure { & $rollbackCallback } 'retained state changed'
    if ($deploymentValidation.PayloadRollbackVerified) { throw 'Changed retained state was reported verified.' }

    $outerCatches = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CatchClauseAst] -and
        $node.Body.Extent.Text.Contains('if ($holdReleased)') }, $true))
    if ($outerCatches.Count -ne 1) { throw 'Outer recovery failure handler missing.' }
    $outerCatch = [scriptblock]::Create(($outerCatches[0].Body.Statements.Extent.Text -join "`n"))
    $holdReleased=$false; $interactiveHostMutationStarted=$true
    $interactiveHostPreviousRoot=Join-Path $testRoot 'previous-companion'
    $InteractiveHostRoot=Join-Path $testRoot 'active-companion'
    $releaseRoot=Join-Path $testRoot 'recovery-receipt'
    New-Item -ItemType Directory -Path $interactiveHostPreviousRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $interactiveHostPreviousRoot 'marker.txt'),'previous-companion')
    $recoveryOriginalInteractiveHostHash=Get-HybridPayloadFingerprint -Path $interactiveHostPreviousRoot
    $interactiveHostRollbackVerified=$false
    function Test-InteractiveHostPayload { param($Path); if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw 'Companion missing' } }
    function Restore-InteractiveHostPayload { param($PreviousRoot,$LiveRoot); Copy-Item -LiteralPath $PreviousRoot -Destination $LiveRoot -Recurse }
    $reported=$null
    try { try { throw 'synthetic inner application verification failure' } catch { & $outerCatch } }
    catch { $reported=$_.Exception.Message }
    if ($null -eq $reported -or -not $reported.Contains('synthetic inner application verification failure') -or
        $reported -notmatch 'application rollback verified=False' -or $reported -notmatch 'companion restored=True' -or
        -not $reported.Contains($releaseRoot) -or $reported -notmatch 'hold retained') {
        throw "Outer recovery handler concealed unverified application rollback: $reported"
    }
    function Restore-InteractiveHostPayload { param($PreviousRoot,$LiveRoot); throw 'synthetic companion restoration failure' }
    $reported=$null
    try { try { throw 'synthetic inner application verification failure' } catch { & $outerCatch } }
    catch { $reported=$_.Exception.Message }
    if ($null -eq $reported -or -not $reported.Contains('synthetic inner application verification failure') -or
        -not $reported.Contains('synthetic companion restoration failure') -or
        $reported -notmatch 'application rollback verified=False' -or $reported -notmatch 'companion restored=False' -or
        -not $reported.Contains($releaseRoot) -or $reported -notmatch 'hold retained') {
        throw "Outer recovery handler concealed failed restoration: $reported"
    }
}
finally {
    Remove-Module $module -Force -ErrorAction SilentlyContinue
    Remove-Module $hashModule -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
Write-Output 'Stopped-pool recovery contract passed (plans, admission, hold retention and six disposable swap outcomes).'
