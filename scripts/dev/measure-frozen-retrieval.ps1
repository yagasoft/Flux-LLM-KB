param([Parameter(Mandatory)][string]$WorkloadPath,[Parameter(Mandatory)][string]$OutputRoot,[string[]]$CaseIds,[string]$SiteUrl='http://127.0.0.1:5137')
$ErrorActionPreference='Stop'
if($CaseIds) {$CaseIds=@($CaseIds | ForEach-Object {$_.Split(',')})}
Import-Module (Join-Path $PSScriptRoot 'frozen-retrieval-contract.psm1') -Force
$workload=Read-FrozenRetrievalContract -WorkloadPath $WorkloadPath
$timeoutSeconds=[int]$workload.execution.outer_deadline_seconds
[void][IO.Directory]::CreateDirectory($OutputRoot)
if (@(Get-ChildItem -LiteralPath $OutputRoot -Force).Count) { throw 'Use an empty output directory; retain previous workload evidence.' }
$cases=@($workload.cases | Where-Object { !$CaseIds -or $_.id -in $CaseIds })
if (!$cases.Count -or ($CaseIds -and @($CaseIds | Where-Object { $_ -notin $workload.cases.id }).Count)) { throw 'Unknown or empty diagnostic case selection.' }
$stop=[Collections.Concurrent.ConcurrentDictionary[string,string]]::new()
$observations=foreach($round in 1..$workload.execution.rounds) {
 $cases | ForEach-Object -Parallel {
  $case=$_; $caseRound=$using:round; $root=$using:OutputRoot; $spec=$using:workload
  $body=@{query=$case.query;limit=10;scope=$case.scope}
  if($case.scope -eq 'root') {$body.root_id=$spec.root_id} else {$body.cwd=$case.cwd}
  $trace=[guid]::NewGuid().ToString('N');$span=[guid]::NewGuid().ToString('N').Substring(0,16)
  $timer=[Diagnostics.Stopwatch]::new();$envelope=$null;$status=$null;$errorCode=$null;$elapsed=$null;$executed=$false
  if (($using:stop).ContainsKey('reason')) { $errorCode='NotStartedAfterFailedGate' }
  else { try {
   $executed=$true;$timer.Start()
   $response=Invoke-WebRequest -Uri (($using:SiteUrl).TrimEnd('/') + '/api/v1/corpus/search') -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) -Headers @{traceparent="00-$trace-$span-01"} -TimeoutSec $using:timeoutSeconds
   $status=[int]$response.StatusCode; $envelope=$response.Content | ConvertFrom-Json
  } catch {$errorCode=$_.Exception.GetType().Name}
   $timer.Stop();$elapsed=$timer.Elapsed.TotalMilliseconds
   if($errorCode -or $status -ne 200 -or !$envelope.ok -or $envelope.result.semanticStatus -cne 'ready' -or $envelope.result.retrievalMode -cne 'hybrid' -or $elapsed -gt (1000*$using:timeoutSeconds)) {
    [void]($using:stop).TryAdd('reason','Failed retrieval gate; verify native outcome and cleanup before further work.')
   }
  }
  $receipt=[ordered]@{observed_utc=[DateTime]::UtcNow.ToString('o');case_id=$case.id;round=$caseRound;scope=$case.scope;trace_id=$trace;executed=$executed;duration_ms=$elapsed;http_status=$status;error_type=$errorCode;anchor_path=$case.anchor_path;request=$body;envelope=$envelope}
  [IO.File]::WriteAllText((Join-Path $root ("$($case.id)-$caseRound.json")),($receipt | ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
  [pscustomobject]@{case_id=$case.id;round=$caseRound;executed=$executed;duration_ms=$elapsed;ok=$envelope.ok;semantic_status=$envelope.result.semanticStatus;mode=$envelope.result.retrievalMode;generation=$envelope.result.indexGeneration;hit_count=@($envelope.result.results).Count;error_type=$errorCode}
 } -ThrottleLimit $workload.execution.concurrent_callers
}
$ordered=@($observations | Where-Object executed | Sort-Object duration_ms)
$p95=if($ordered.Count) {$ordered[[Math]::Ceiling(0.95*$ordered.Count)-1].duration_ms} else {$null}
$summary=[ordered]@{observed_utc=[DateTime]::UtcNow.ToString('o');full_frozen_workload=(!$CaseIds);observations=$observations.Count;executed_searches=$ordered.Count;concurrent_callers=2;p95_ms=$p95;stopped_after_failure=$stop.ContainsKey('reason');requires_outcome_and_cleanup_verification=$stop.ContainsKey('reason');states=@($observations | Group-Object semantic_status -NoElement | Select-Object Name,Count);results=@($observations | Sort-Object case_id,round)}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputRoot 'summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 6
if($stop.ContainsKey('reason')) { throw $stop['reason'] }
