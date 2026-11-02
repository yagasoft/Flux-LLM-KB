[CmdletBinding()]
param(
    [string]$SourceRoot = '',
    [string]$CloseoutScript = '',
    [ValidateSet('', 'closeout', 'deployment')][string]$Scenario = '',
    [ValidateRange(1, 2)][int]$MaxParallelScenarios = 2,
    [string]$OutputRoot = '',
    [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None
)
$ErrorActionPreference = 'Stop'
if (-not $SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
if (-not $CloseoutScript) { $CloseoutScript = Join-Path $SourceRoot 'scripts\dev\complete-feature.ps1' }
if ($Scenario) {
    $contract = if ($Scenario -eq 'closeout') { 'complete-feature-integration.ps1' } else { 'native-deployment-plan.ps1' }
    $arguments = @('-NoProfile', '-File', (Join-Path $SourceRoot "tests\native\$contract"), '-SourceRoot', $SourceRoot)
    if ($Scenario -eq 'closeout') { $arguments += @('-CloseoutScript', $CloseoutScript) }
    & pwsh @arguments
    exit $LASTEXITCODE
}

# These two contracts own separate GUID fixtures and child environments. The
# existing job runner contains each outer worker before any contract code runs.
# The closeout contract retains its own four-scenario scheduling internally.
$temporaryOutput = -not $OutputRoot
if ($temporaryOutput) {
    $OutputRoot = Join-Path ([IO.Path]::GetTempPath()) "FluxKnowledgeIsolatedContracts-$([Guid]::NewGuid().ToString('N'))"
}
if (Test-Path -LiteralPath $OutputRoot) { throw "Contract output already exists: $OutputRoot" }
Import-Module (Join-Path $PSScriptRoot 'WorkflowScenarioRunner.psm1') -Force
$passed = $false
try {
    Invoke-WorkflowScenarios -ScriptPath $PSCommandPath -SourceRoot $SourceRoot -CloseoutScript $CloseoutScript `
        -Scenario @('closeout', 'deployment') -MaxParallelScenarios $MaxParallelScenarios -SerialScenario @() `
        -OutputRoot $OutputRoot -CancellationToken $CancellationToken |
        ForEach-Object { Write-Output $_.Stdout.TrimEnd() }
    $passed = $true
} finally {
    if ($passed -and $temporaryOutput) {
        $resolved = (Resolve-Path -LiteralPath $OutputRoot).Path
        if ((Split-Path -Leaf $resolved) -notlike 'FluxKnowledgeIsolatedContracts-*' -or
            -not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Unsafe isolated-contract cleanup path.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    } else {
        Write-Output "Isolated contract results: $OutputRoot"
    }
}
