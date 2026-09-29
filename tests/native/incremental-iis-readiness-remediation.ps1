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
