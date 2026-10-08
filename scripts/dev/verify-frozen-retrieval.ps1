param(
    [Parameter(Mandatory)][string]$WorkloadPath,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][guid]$ExpectedGeneration,
    [string]$SiteUrl = 'http://127.0.0.1:5137'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'frozen-retrieval-contract.psm1') -Force
$spec = Read-FrozenRetrievalContract -WorkloadPath $WorkloadPath
$repositoryPrefix = [IO.Path]::GetFullPath($spec.repository).TrimEnd('\') + '\'
$summary = Get-Content -LiteralPath (Join-Path $OutputRoot 'summary.json') -Raw | ConvertFrom-Json
if (!$summary.full_frozen_workload -or $summary.observations -ne 40 -or $summary.executed_searches -ne 40 -or $summary.concurrent_callers -ne 2 -or $summary.stopped_after_failure) { throw 'Frozen workload cardinality/caller gate failed.' }
$receipts=@{}
$timings=[Collections.Generic.List[double]]::new()
foreach($case in $spec.cases) {
    foreach($round in 1..2) {
        $key=$case.id+'-'+$round
        $item=Get-Content -LiteralPath (Join-Path $OutputRoot ($key+'.json')) -Raw | ConvertFrom-Json
        Assert-FrozenRetrievalObservation -Observation $item -Contract $spec
        if(!$item.executed -or $item.case_id -cne $case.id -or $item.round -ne $round -or $item.scope -cne $case.scope -or
            $item.request.query -cne $case.query -or $item.request.limit -ne $spec.execution.limit -or $item.request.scope -cne $case.scope -or
            ($case.scope -eq 'root' -and [guid]$item.request.root_id -ne [guid]$spec.root_id) -or
            ($case.scope -eq 'workspace' -and $item.request.cwd -cne $case.cwd)) { throw 'A search receipt does not match its frozen case.' }
        $receipts[$key]=$item;$timings.Add([double]$item.duration_ms)
    }
}
$ordered=@($timings | Sort-Object)
$actualP95=$ordered[[Math]::Ceiling(0.95*$ordered.Count)-1]
if ($actualP95 -ge 20000 -or [double]$summary.p95_ms -ne $actualP95) { throw 'The full workload p95 or summary timing gate failed.' }
$checks = [Collections.Generic.List[object]]::new()
$allPassages = 0
$sourceHashes = @{}
foreach ($case in $spec.cases) {
    foreach ($round in 1..2) {
        $receipt = $receipts[$case.id+'-'+$round]
        $result = $receipt.envelope.result
        if ($receipt.http_status -ne 200 -or !$receipt.envelope.ok -or $result.semanticStatus -cne 'ready' -or
            $result.retrievalMode -cne 'hybrid' -or [guid]$result.indexGeneration -ne $ExpectedGeneration) {
            throw ('Semantic/current-generation gate failed: ' + $case.id + ' round ' + $round)
        }
        $hits = @($result.results)
        if (!$hits.Count) { throw ('Search returned no cited results: ' + $case.id) }
        foreach ($hit in $hits) {
            $sourcePath = [IO.Path]::GetFullPath($hit.sourceIdentity)
            if ([guid]$hit.rootId -ne [guid]$spec.root_id -or
                !$sourcePath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                ($case.scope -eq 'workspace' -and !$sourcePath.StartsWith($case.cwd.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))) {
                throw ('Citation escaped the configured scope: ' + $case.id)
            }
            if (!(Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw 'A cited current source is missing.' }
            $canonical = [IO.File]::ReadAllText($sourcePath).Replace("`r`n", "`n").Normalize([Text.NormalizationForm]::FormC)
            if ($hit.startOffset -lt 0 -or $hit.length -le 0 -or $hit.startOffset + $hit.length -gt $canonical.Length -or
                $canonical.Substring($hit.startOffset, $hit.length) -cne $hit.passage) { throw ('Search passage differs from current canonical text: ' + $case.id) }
            $sourceHash=(Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
            if($sourceHashes.ContainsKey($sourcePath) -and $sourceHashes[$sourcePath] -cne $sourceHash) { throw 'A cited source changed during workload verification.' }
            $sourceHashes[$sourcePath] = $sourceHash
            $allPassages++
        }
        $anchor = $repositoryPrefix + $case.anchor_path.Replace('/', '\')
        $anchorHits = @($hits | Where-Object sourceIdentity -CEQ $anchor)
        $selected = if ($anchorHits.Count) { $anchorHits[0] } else { $hits[0] }
        $body = @{evidence_ref=$selected.evidenceRef;context_characters=0} | ConvertTo-Json -Compress
        $response = Invoke-WebRequest -Uri ($SiteUrl.TrimEnd('/') + '/api/v1/corpus/read') -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 30
        $readEnvelope = $response.Content | ConvertFrom-Json
        $response.Content | Set-Content -LiteralPath (Join-Path $OutputRoot ($case.id + '-' + $round + '-read.json')) -Encoding utf8
        if (!$readEnvelope.ok) { throw ('Current cited read was refused: ' + $case.id + ' ' + $readEnvelope.reasonCode) }
        $read = $readEnvelope.result
        $canonical = [IO.File]::ReadAllText($read.sourceIdentity).Replace("`r`n", "`n").Normalize([Text.NormalizationForm]::FormC)
        if ($read.sourceIdentity -cne $selected.sourceIdentity -or $read.startOffset -ne $selected.startOffset -or
            $read.length -ne $selected.length -or $read.text -cne $selected.passage -or
            $canonical.Substring($read.startOffset, $read.length) -cne $read.text) { throw ('Cited read differs from its current search binding: ' + $case.id) }
        $checks.Add([pscustomobject]@{case_id=$case.id;round=$round;scope=$case.scope;semantic_ready=$true;generation=$ExpectedGeneration;passages=$hits.Count;exact_current_passages=$true;exact_current_read=$true;expected_anchor_in_top10=($anchorHits.Count -gt 0)})
    }
}
foreach ($sourcePath in $sourceHashes.Keys) {
    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -cne $sourceHashes[$sourcePath]) { throw 'A cited source changed during workload verification.' }
}
$receipt = [ordered]@{observed_utc=[DateTime]::UtcNow.ToString('o');full_frozen_workload=$true;concurrent_callers=2;searches=$checks.Count;p95_ms=$summary.p95_ms;semantic_ready_searches=$checks.Count;current_generation=$ExpectedGeneration;exact_current_search_passages=$allPassages;exact_current_cited_reads=$checks.Count;distinct_verified_sources=$sourceHashes.Count;expected_anchor_searches=@($checks | Where-Object expected_anchor_in_top10).Count;checks=@($checks)}
$receipt | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $OutputRoot 'acceptance.json') -Encoding utf8
[pscustomobject]$receipt | Select-Object observed_utc,searches,p95_ms,semantic_ready_searches,current_generation,exact_current_search_passages,exact_current_cited_reads,distinct_verified_sources,expected_anchor_searches | ConvertTo-Json
