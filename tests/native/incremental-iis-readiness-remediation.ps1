[CmdletBinding()]
param([string]$SourceRoot = "")

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$source = Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'The incremental updater has a PowerShell parse error.' }
$function = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Invoke-ScopedReadinessRemediationProbes' }, $true))
if ($function.Count -ne 1) { throw 'Expected one scoped-readiness probe function.' }
Invoke-Expression $function[0].Extent.Text

$script:ReadinessStatus = 503
$script:FailPath = ''
function Invoke-FixedLoopbackProbe {
    param([string]$Uri, [int]$TimeoutSeconds)
    if ($Uri.EndsWith($script:FailPath, [StringComparison]::Ordinal) -and $script:FailPath) {
        throw 'probe failed'
    }
    if ($Uri.EndsWith('/health/ready', [StringComparison]::Ordinal) -and $script:ReadinessStatus -ne 200) {
        if ($script:ReadinessStatus -eq 503) {
            throw 'The fixed-loopback endpoint returned HTTP 503; exact HTTP 200 is required.'
        }
        throw "The fixed-loopback endpoint returned HTTP $($script:ReadinessStatus); exact HTTP 200 is required."
    }
    $response = [pscustomobject]@{}
    $response | Add-Member ScriptMethod Dispose { }
    return $response
}

function Assert-Rejected {
    param([scriptblock]$Action)
    $rejected = $false
    try { & $Action | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'The scoped-readiness probe unexpectedly accepted an invalid result.' }
}

$origin = 'http://127.0.0.1:5137'
if ((Invoke-ScopedReadinessRemediationProbes -Origin $origin -TimeoutSeconds 2) -ne 'pending') {
    throw 'Held HTTP 503 was not accepted.'
}
$script:ReadinessStatus = 200
Assert-Rejected { Invoke-ScopedReadinessRemediationProbes -Origin $origin -TimeoutSeconds 2 }
if ((Invoke-ScopedReadinessRemediationProbes -Origin $origin -TimeoutSeconds 2 -AfterHoldRelease) -ne 'ready') {
    throw 'Post-release HTTP 200 was not accepted.'
}
$script:ReadinessStatus = 503
if ((Invoke-ScopedReadinessRemediationProbes -Origin $origin -TimeoutSeconds 2 -AfterHoldRelease) -ne 'pending') {
    throw 'Post-release HTTP 503 was not accepted.'
}
$script:ReadinessStatus = 500
Assert-Rejected { Invoke-ScopedReadinessRemediationProbes -Origin $origin -TimeoutSeconds 2 -AfterHoldRelease }
$script:ReadinessStatus = 503
$script:FailPath = '/api/index-health'
Assert-Rejected { Invoke-ScopedReadinessRemediationProbes -Origin $origin -TimeoutSeconds 2 -AfterHoldRelease }
$script:FailPath = '/health/live'
Assert-Rejected { Invoke-ScopedReadinessRemediationProbes -Origin $origin -TimeoutSeconds 2 -AfterHoldRelease }
'PASS: held and post-release readiness states are bounded to the reviewed HTTP results.'

$required = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Invoke-RequiredLoopbackProbes' }, $true))
if ($required.Count -ne 1) { throw 'Expected one required-readiness probe function.' }
Invoke-Expression $required[0].Extent.Text

$script:ProbeCalls = [Collections.Generic.List[string]]::new()
$script:ReadyMode = 'starting'
$script:FailPath = ''
$script:FailureStatus = 500
$script:DisposedResponses = 0
function Invoke-FixedLoopbackProbe {
    param([string]$Uri, [int]$TimeoutSeconds, [Threading.CancellationToken]$CancellationToken)
    $script:ProbeCalls.Add(([uri]$Uri).AbsolutePath)
    if ($script:FailPath -and $Uri.EndsWith($script:FailPath, [StringComparison]::Ordinal)) {
        throw "The fixed-loopback endpoint returned HTTP $($script:FailureStatus); exact HTTP 200 is required."
    }
    if ($Uri.EndsWith('/health/ready', [StringComparison]::Ordinal)) {
        $readyCalls = @($script:ProbeCalls | Where-Object { $_ -eq '/health/ready' }).Count
        if ($script:ReadyMode -eq 'blocked') {
            if (-not $CancellationToken.CanBeCanceled) { throw 'Readiness request has no shared deadline.' }
            [void]$CancellationToken.WaitHandle.WaitOne()
            $CancellationToken.ThrowIfCancellationRequested()
        }
        if ($script:ReadyMode -eq 'late') { Start-Sleep -Milliseconds 1100 }
        if ($script:ReadyMode -eq 'absent' -or ($script:ReadyMode -eq 'starting' -and $readyCalls -eq 1)) {
            throw 'The fixed-loopback endpoint returned HTTP 503; exact HTTP 200 is required.'
        }
    }
    $response = [pscustomobject]@{}
    $response | Add-Member ScriptMethod Dispose { $script:DisposedResponses++ }
    return $response
}

Invoke-RequiredLoopbackProbes -Origin $origin -TimeoutSeconds 2
if (($script:ProbeCalls -join ',') -cne '/health/live,/health/ready,/health/ready,/api/index-health' -or
    $script:DisposedResponses -ne 3) {
    throw 'Asynchronous startup must converge to HTTP 200 before index-health validation.'
}
foreach ($mode in @('absent', 'blocked', 'late')) {
    $script:ProbeCalls.Clear(); $script:ReadyMode = $mode
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Assert-Rejected { Invoke-RequiredLoopbackProbes -Origin $origin -TimeoutSeconds 1 }
    if ($timer.Elapsed.TotalSeconds -lt 0.9 -or $timer.Elapsed.TotalSeconds -gt 3 -or
        $script:ProbeCalls.Contains('/api/index-health')) {
        throw 'Unready, blocked or late readiness must fail within the original shared deadline.'
    }
}
$script:ReadyMode = 'ready'
foreach ($failureStatus in @(500, 503)) { foreach ($path in @('/health/live', '/health/ready', '/api/index-health')) {
    if ($failureStatus -eq 503 -and $path -eq '/health/ready') { continue }
    $script:FailureStatus = $failureStatus
    $script:ProbeCalls.Clear(); $script:FailPath = $path
    Assert-Rejected { Invoke-RequiredLoopbackProbes -Origin $origin -TimeoutSeconds 2 }
    if (@($script:ProbeCalls | Where-Object { $_ -eq $path }).Count -ne 1) {
        throw 'An unrelated probe failure must not be retried.'
    }
}}
'PASS: required readiness waits only for startup HTTP 503 and retains its deadline and strict failure gates.'
