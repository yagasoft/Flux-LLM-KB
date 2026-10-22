[CmdletBinding()]
param([string]$SourceRoot = '')

$ErrorActionPreference = 'Stop'
if (-not $SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
Import-Module (Join-Path $SourceRoot 'tests\native\WorkflowScenarioRunner.psm1') -Force
if (-not ('WorkflowScenarioJob' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'WorkflowScenarioProcess.cs') }
$root = Join-Path ([IO.Path]::GetTempPath()) "FluxKnowledgeWorkflowParallelContract-$([Guid]::NewGuid().ToString('N'))"
$savedRoot = $env:FLUXKNOWLEDGE_WORKFLOW_PROOF_ROOT
$savedCase = $env:FLUXKNOWLEDGE_WORKFLOW_CASE

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $worker = Join-Path $root 'worker.ps1'
    @'
param([string]$SourceRoot, [string]$CloseoutScript, [string]$Scenario, [int]$MaxParallelScenarios)
$ErrorActionPreference = 'Stop'
$root = $env:FLUXKNOWLEDGE_WORKFLOW_PROOF_ROOT
$env:FLUXKNOWLEDGE_WORKFLOW_CASE = $Scenario
if ($MaxParallelScenarios -ne 1) { throw 'A scenario child must not recursively fan out.' }
@{ scenario=$Scenario; pid=$PID; value=$env:FLUXKNOWLEDGE_WORKFLOW_CASE } |
    ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $root "$Scenario.started")
if ($Scenario -in @('left', 'right')) {
    $peer = if ($Scenario -eq 'left') { 'right' } else { 'left' }
    $deadline = [datetime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath (Join-Path $root "$peer.started"))) {
        if ([datetime]::UtcNow -ge $deadline) { throw 'Independent scenarios did not overlap.' }
        Start-Sleep -Milliseconds 20
    }
}
if ($Scenario -eq 'after') {
    foreach ($peer in @('left', 'right')) {
        if (-not (Test-Path -LiteralPath (Join-Path $root "$peer.done"))) { throw 'Serial scenario overlapped a parallel scenario.' }
    }
}
if ($Scenario -eq 'fail') { [Console]::Error.WriteLine('injected child failure'); exit 7 }
if ($Scenario -in @('cancel', 'orphan')) {
    $hang = Join-Path $root 'hang.ps1'
    Set-Content -LiteralPath $hang -Value 'Start-Sleep -Seconds 90'
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in @('-NoProfile', '-File', $hang)) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    Set-Content -LiteralPath (Join-Path $root 'descendant.pid') -Value $child.Id
    if ($Scenario -eq 'orphan') { exit 0 }
    Start-Sleep -Seconds 90
}
Set-Content -LiteralPath (Join-Path $root "$Scenario.done") -Value $env:FLUXKNOWLEDGE_WORKFLOW_CASE
Write-Output "Worker $Scenario passed."
'@ | Set-Content -LiteralPath $worker
    $env:FLUXKNOWLEDGE_WORKFLOW_PROOF_ROOT = $root
    $env:FLUXKNOWLEDGE_WORKFLOW_CASE = 'parent-sentinel'
    $common = @{ ScriptPath=$worker; SourceRoot=$root; CloseoutScript=$worker }
    # Run this regression inside a separately owned job with a watchdog. A
    # broken runner must fail the contract, rather than hang or leak its probe.
    $driver = Join-Path $root 'orphan-driver.ps1'
    @'
param([string]$SourceRoot, [string]$CloseoutScript, [string]$Scenario, [int]$MaxParallelScenarios)
Import-Module $env:FLUXKNOWLEDGE_WORKFLOW_RUNNER -Force
$cancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(3))
try {
    Invoke-WorkflowScenarios -ScriptPath $CloseoutScript -SourceRoot $SourceRoot -CloseoutScript $CloseoutScript -Scenario @('orphan') -OutputRoot (Join-Path $SourceRoot 'orphan') -CancellationToken $cancellation.Token | Out-Null
    throw 'An exited worker with an active descendant passed.'
} catch {
    if ($_.ToString().Contains('active descendant') -and -not $_.ToString().StartsWith('An exited')) { exit 0 }
    Write-Error $_
    exit 1
} finally { $cancellation.Dispose() }
'@ | Set-Content -LiteralPath $driver
    $savedRunner = $env:FLUXKNOWLEDGE_WORKFLOW_RUNNER
    $env:FLUXKNOWLEDGE_WORKFLOW_RUNNER = Join-Path $SourceRoot 'tests\native\WorkflowScenarioRunner.psm1'
    $probeJob = [WorkflowScenarioJob]::new()
    $probe = $null
    try {
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        $gate = Join-Path $root 'probe.gate'
        foreach ($argument in @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'WorkflowScenarioWorker.ps1'), '-StartGate', $gate,
            '-ScriptPath', $driver, '-SourceRoot', $root, '-CloseoutScript', $worker, '-Scenario', 'probe')) { $start.ArgumentList.Add($argument) }
        $probe = [Diagnostics.Process]::Start($start)
        $stdout = $probe.StandardOutput.ReadToEndAsync(); $stderr = $probe.StandardError.ReadToEndAsync()
        $probeJob.Assign($probe)
        [IO.File]::WriteAllText($gate, 'owned')
        Assert-True ($probe.WaitForExit(10000)) 'Exited-worker pipe draining ignored its cancellation deadline.'
        Assert-True ($stdout.Wait(2000) -and $stderr.Wait(2000)) 'Exited-worker pipes stayed open.'
        Assert-True ($probe.ExitCode -eq 0) ("Exited-worker cleanup failed: " + $stdout.Result + $stderr.Result)
        $descendant = [int](Get-Content -LiteralPath (Join-Path $root 'descendant.pid'))
        Assert-True ($null -eq (Get-Process -Id $descendant -ErrorAction SilentlyContinue)) 'Exited-worker cleanup left its descendant running.'
    } finally {
        $probeJob.Dispose()
        if ($null -ne $probe) { $probe.WaitForExit(3000) | Out-Null; $probe.Dispose() }
        $env:FLUXKNOWLEDGE_WORKFLOW_RUNNER = $savedRunner
    }
    $results = @(Invoke-WorkflowScenarios @common -Scenario @('left', 'right', 'after') -SerialScenario @('after') -MaxParallelScenarios 2 -OutputRoot (Join-Path $root 'parallel'))
    Assert-True ($results.Count -eq 3) 'The aggregate lost a scenario.'
    $left = Get-Content -LiteralPath (Join-Path $root 'left.started') -Raw | ConvertFrom-Json
    $right = Get-Content -LiteralPath (Join-Path $root 'right.started') -Raw | ConvertFrom-Json
    Assert-True ($left.pid -ne $right.pid) 'Scenarios shared one process.'
    Assert-True ($left.value -eq 'left' -and $right.value -eq 'right') 'Scenario environments crossed.'
    Assert-True ($env:FLUXKNOWLEDGE_WORKFLOW_CASE -eq 'parent-sentinel') 'A scenario changed the parent environment.'
    Assert-True (@($results | Where-Object ExitCode -ne 0).Count -eq 0) 'A successful aggregate contained failure.'

    $failure = $null
    try { Invoke-WorkflowScenarios @common -Scenario @('fail', 'ok') -MaxParallelScenarios 2 -OutputRoot (Join-Path $root 'failure') | Out-Null }
    catch { $failure = $_ }
    Assert-True ($null -ne $failure -and $failure.ToString().Contains('fail')) 'A failed child was hidden.'
    $failedResults = @(Get-Content -LiteralPath (Join-Path $root 'failure\scenario-results.json') -Raw | ConvertFrom-Json)
    Assert-True (@($failedResults | Where-Object { $_.Scenario -eq 'fail' -and $_.ExitCode -eq 7 }).Count -eq 1) 'Failure lost its exact scenario/exit code.'
    Assert-True (@($failedResults | Where-Object { $_.Scenario -eq 'ok' -and $_.ExitCode -eq 0 }).Count -eq 1) 'Failure discarded its sibling result.'

    $launchAfterDescendant = {
        param($StartInfo, $Name)
        if ($Name -ne 'cancel') {
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
            while (-not [IO.File]::Exists((Join-Path $env:FLUXKNOWLEDGE_WORKFLOW_PROOF_ROOT 'descendant.pid'))) {
                if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Live-worker setup timed out.' }
                [Threading.Thread]::Sleep(20)
            }
            if ($Name -eq 'launch-failure') { throw 'injected launcher failure' }
        }
        [Diagnostics.Process]::Start($StartInfo)
    }
    Remove-Item -LiteralPath (Join-Path $root 'descendant.pid') -ErrorAction SilentlyContinue
    $failure = $null
    try { Invoke-WorkflowScenarios @common -Scenario @('cancel', 'launch-failure') -OutputRoot (Join-Path $root 'launch-failure') -ProcessLauncher $launchAfterDescendant | Out-Null }
    catch { $failure = $_ }
    Assert-True ($null -ne $failure -and $failure.ToString().Contains('injected launcher failure')) 'Cleanup replaced the primary launcher failure.'
    $launchResults = @(Get-Content -LiteralPath (Join-Path $root 'launch-failure\scenario-results.json') -Raw | ConvertFrom-Json)
    Assert-True (@($launchResults | Where-Object { $_.Scenario -eq 'launch-failure' -and $_.Interrupted }).Count -eq 1) 'Launcher failure lost its named result.'
    $descendant = [int](Get-Content -LiteralPath (Join-Path $root 'descendant.pid'))
    Assert-True ($null -eq (Get-Process -Id $descendant -ErrorAction SilentlyContinue)) 'Launcher failure stranded a sibling descendant.'

    Remove-Item -LiteralPath (Join-Path $root 'descendant.pid')
    $unwritable = Join-Path $root 'log-failure'
    New-Item -ItemType Directory -Path (Join-Path $unwritable 'unwritable.stdout.log') -Force | Out-Null
    $deadlineCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
    try {
        $failure = $null
        try { Invoke-WorkflowScenarios @common -Scenario @('cancel', 'unwritable') -OutputRoot $unwritable -ProcessLauncher $launchAfterDescendant -CancellationToken $deadlineCancellation.Token | Out-Null }
        catch { $failure = $_ }
        Assert-True ($null -ne $failure) 'A log failure and cancelled worker passed.'
        $cleanupResults = @(Get-Content -LiteralPath (Join-Path $unwritable 'scenario-results.json') -Raw | ConvertFrom-Json)
        Assert-True (@($cleanupResults | Where-Object { $_.Scenario -eq 'unwritable' -and ($_.Errors -join ' ').Contains('stdout log failed') }).Count -eq 1) 'The log failure was lost.'
        Assert-True (@($cleanupResults | Where-Object { $_.Scenario -eq 'cancel' -and $_.Interrupted }).Count -eq 1) 'The remaining worker was lost during failed cleanup.'
        $descendant = [int](Get-Content -LiteralPath (Join-Path $root 'descendant.pid'))
        Assert-True ($null -eq (Get-Process -Id $descendant -ErrorAction SilentlyContinue)) 'Log failure stranded a sibling descendant.'
    } finally { $deadlineCancellation.Dispose() }

    $cancellation = [Threading.CancellationTokenSource]::new()
    try {
        $cancellation.CancelAfter([TimeSpan]::FromSeconds(5))
        $cancelled = $null
        try { Invoke-WorkflowScenarios @common -Scenario @('cancel') -MaxParallelScenarios 1 -OutputRoot (Join-Path $root 'cancelled') -CancellationToken $cancellation.Token | Out-Null }
        catch { $cancelled = $_ }
        Assert-True ($null -ne $cancelled) 'Cancellation succeeded as if all scenarios completed.'
        $descendant = [int](Get-Content -LiteralPath (Join-Path $root 'descendant.pid'))
        Assert-True ($null -eq (Get-Process -Id $descendant -ErrorAction SilentlyContinue)) 'Cancellation left an owned descendant running.'
    } finally { $cancellation.Dispose() }
    Write-Output 'Workflow parallel isolation, failure aggregation and cancellation passed.'
} finally {
    $env:FLUXKNOWLEDGE_WORKFLOW_PROOF_ROOT = $savedRoot
    $env:FLUXKNOWLEDGE_WORKFLOW_CASE = $savedCase
    if (Test-Path -LiteralPath $root) {
        $resolved = (Resolve-Path -LiteralPath $root).Path
        $expected = [IO.Path]::GetFullPath($root)
        if ($resolved -ne $expected -or (Split-Path -Leaf $resolved) -notlike 'FluxKnowledgeWorkflowParallelContract-*') { throw 'Unsafe contract cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
