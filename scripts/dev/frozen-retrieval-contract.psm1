Set-StrictMode -Version Latest

function Read-FrozenRetrievalContract {
    param([Parameter(Mandatory)][string]$WorkloadPath)
    $contract = Get-Content -LiteralPath $WorkloadPath -Raw | ConvertFrom-Json
    $execution = $contract.execution
    if ($execution.rounds -ne 2 -or $execution.concurrent_callers -ne 2 -or
        $execution.total_searches -ne 40 -or $execution.limit -ne 10 -or
        $execution.outer_deadline_seconds -ne 25 -or $execution.p95_acceptance_seconds -ne 20 -or
        @($contract.cases).Count -ne 20 -or @($contract.cases.id | Select-Object -Unique).Count -ne 20) {
        throw 'The agreed frozen retrieval workload or acceptance contract changed.'
    }
    return $contract
}

function Assert-FrozenRetrievalObservation {
    param([Parameter(Mandatory)]$Observation, [Parameter(Mandatory)]$Contract)
    if ($null -eq $Observation.duration_ms) { throw 'The search elapsed measurement is missing.' }
    $elapsed = [double]$Observation.duration_ms
    if ([double]::IsNaN($elapsed) -or [double]::IsInfinity($elapsed) -or $elapsed -lt 0 -or
        $elapsed -gt (1000.0 * $Contract.execution.outer_deadline_seconds)) {
        throw 'The search exceeded the agreed outer deadline or has an invalid elapsed measurement.'
    }
    if ($Observation.http_status -ne 200 -or !$Observation.envelope -or !$Observation.envelope.ok -or
        $Observation.envelope.result.semanticStatus -cne 'ready' -or
        $Observation.envelope.result.retrievalMode -cne 'hybrid') {
        throw 'The search did not produce a successful semantic-ready hybrid result.'
    }
}

Export-ModuleMember -Function Read-FrozenRetrievalContract, Assert-FrozenRetrievalObservation
