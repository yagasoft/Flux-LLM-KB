param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $SourceRoot 'scripts\dev\frozen-retrieval-contract.psm1') -Force
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('flux-frozen-contract-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporaryRoot)
try {
    $spec = [pscustomobject]@{
        repository = $temporaryRoot
        execution = [pscustomobject]@{rounds=2;concurrent_callers=2;total_searches=40;limit=10;outer_deadline_seconds=25;p95_acceptance_seconds=20}
        cases = @(1..20 | ForEach-Object { [pscustomobject]@{id="case-$_";scope='root';query="synthetic question $_";anchor_path='answer.txt'} })
    }
    $path = Join-Path $temporaryRoot 'workload.json'
    $spec | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8
    $contract = Read-FrozenRetrievalContract -WorkloadPath $path
    if ($contract.execution.outer_deadline_seconds -ne 25 -or $contract.cases.Count -ne 20) { throw 'The frozen contract changed.' }
    $observation = [pscustomobject]@{duration_ms=19999.0;http_status=200;envelope=[pscustomobject]@{ok=$true;result=[pscustomobject]@{semanticStatus='ready';retrievalMode='hybrid'}}}
    Assert-FrozenRetrievalObservation -Observation $observation -Contract $contract
    foreach ($invalid in @(25000.001, [double]::NaN, [double]::PositiveInfinity, -1.0, $null)) {
        $observation.duration_ms = $invalid
        $refused = $false
        try { Assert-FrozenRetrievalObservation -Observation $observation -Contract $contract } catch { $refused = $true }
        if (!$refused) { throw 'A late or invalid elapsed measurement passed.' }
    }
    $observation.duration_ms = 15000.0
    foreach ($status in @('timeout','index-updating','unavailable')) {
        $observation.envelope.result.semanticStatus = $status
        $refused = $false
        try { Assert-FrozenRetrievalObservation -Observation $observation -Contract $contract } catch { $refused = $true }
        if (!$refused) { throw 'A semantic fallback passed the frozen gate.' }
    }
    $spec.cases = @($spec.cases | Select-Object -First 19)
    $spec | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8
    $refused = $false
    try { Read-FrozenRetrievalContract -WorkloadPath $path | Out-Null } catch { $refused = $true }
    if (!$refused) { throw 'A reduced workload was accepted.' }
    'Frozen retrieval contract checks passed: original workload, late/invalid timings and honest fallback.'
} finally {
    $resolved = [IO.Path]::GetFullPath($temporaryRoot)
    if (!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -or
        !(Split-Path -Leaf $resolved).StartsWith('flux-frozen-contract-')) { throw 'Unsafe fixture cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
