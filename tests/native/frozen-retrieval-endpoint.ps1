param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
$temporaryRoot=Join-Path ([IO.Path]::GetTempPath()) ('flux-frozen-endpoint-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporaryRoot)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
public sealed class FrozenEndpoint : IDisposable {
    private readonly HttpListener listener = new();
    private readonly ConcurrentBag<Task> handlers = new();
    private readonly Task loop;
    private readonly string source;
    private readonly string root;
    private readonly string generation;
    private int active;
    public int MaximumActive;
    public int DelayedStarted;
    public int DelayedCleaned;
    public ConcurrentQueue<string> Requests = new();
    public FrozenEndpoint(string url, string source, string root, string generation) {
        this.source=source;this.root=root;this.generation=generation;
        listener.Prefixes.Add(url+"/");listener.Start();
        loop=Task.Run(async () => {
            try { while(listener.IsListening) { var context=await listener.GetContextAsync(); handlers.Add(Task.Run(()=>Handle(context))); } }
            catch(HttpListenerException) {} catch(ObjectDisposedException) {}
        });
    }
    private async Task Handle(HttpListenerContext context) {
        var current=Interlocked.Increment(ref active);
        int previous;
        do { previous=MaximumActive; if(previous>=current) break; } while(Interlocked.CompareExchange(ref MaximumActive,current,previous)!=previous);
        bool delayed=false;
        try {
            using var document=JsonDocument.Parse(await new StreamReader(context.Request.InputStream).ReadToEndAsync());
            Requests.Enqueue(context.Request.Url.AbsolutePath);
            object result;
            var text=File.ReadAllText(source);
            if(context.Request.Url.AbsolutePath.EndsWith("/search")) {
                delayed=document.RootElement.GetProperty("query").GetString().StartsWith("delay-");
                if(delayed) { Interlocked.Increment(ref DelayedStarted); await Task.Delay(27000); }
                else await Task.Delay(20);
                result=new {semanticStatus="ready",retrievalMode="hybrid",indexGeneration=generation,
                    results=new[]{new {rootId=root,sourceIdentity=source,startOffset=0,length=text.Length,passage=text,evidenceRef="synthetic-evidence"}}};
            } else result=new {sourceIdentity=source,startOffset=0,length=text.Length,text};
            var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {ok=true,result}));
            context.Response.ContentType="application/json";context.Response.ContentLength64=bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
        } catch(HttpListenerException) {} catch(IOException) {}
        finally { context.Response.Close();if(delayed) Interlocked.Increment(ref DelayedCleaned);Interlocked.Decrement(ref active); }
    }
    public async Task WaitForCleanup() {
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while(Volatile.Read(ref active)!=0) await Task.Delay(20,budget.Token);
    }
    public void Dispose() { listener.Stop();loop.GetAwaiter().GetResult();Task.WhenAll(handlers).GetAwaiter().GetResult();listener.Close(); }
}
'@
$server=$null
try {
    $source=Join-Path $temporaryRoot 'answer.txt';[IO.File]::WriteAllText($source,'Synthetic current citation.')
    $root=[guid]::NewGuid();$generation=[guid]::NewGuid()
    $spec=[pscustomobject]@{repository=$temporaryRoot;root_id=$root;execution=[pscustomobject]@{rounds=2;concurrent_callers=2;total_searches=40;limit=10;outer_deadline_seconds=25;p95_acceptance_seconds=20};cases=@(1..20|ForEach-Object{[pscustomobject]@{id="case-$_";scope='root';query="synthetic $_";anchor_path='answer.txt'}})}
    $workload=Join-Path $temporaryRoot 'workload.json'
    $spec|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $workload -Encoding utf8
    $reservation=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$reservation.Start();$port=$reservation.LocalEndpoint.Port;$reservation.Stop()
    $url='http://127.0.0.1:'+$port
    $server=[FrozenEndpoint]::new($url,$source,$root.ToString(),$generation.ToString())
    $runner=Join-Path $SourceRoot 'scripts\dev\measure-frozen-retrieval.ps1'
    $verifier=Join-Path $SourceRoot 'scripts\dev\verify-frozen-retrieval.ps1'
    $normal=Join-Path $temporaryRoot 'normal'
    & $runner -WorkloadPath $workload -OutputRoot $normal -SiteUrl $url | Out-Null
    & $verifier -WorkloadPath $workload -OutputRoot $normal -SiteUrl $url -ExpectedGeneration $generation | Out-Null
    $summary=Get-Content -LiteralPath (Join-Path $normal 'summary.json') -Raw|ConvertFrom-Json
    if($summary.executed_searches -ne 40 -or $server.MaximumActive -ne 2 -or $server.Requests.Count -ne 80) { throw 'The real endpoint workload/citation/concurrency contract failed.' }
    $before=$server.Requests.Count
    foreach($case in $spec.cases) { foreach($round in 1..2) {
        $path=Join-Path $normal ($case.id+'-'+$round+'.json');$item=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
        $item.duration_ms=21000;$item|ConvertTo-Json -Depth 16|Set-Content -LiteralPath $path -Encoding utf8
    } }
    $summary.p95_ms=1;$summary|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $normal 'summary.json') -Encoding utf8
    $refused=$false;try { & $verifier -WorkloadPath $workload -OutputRoot $normal -SiteUrl $url -ExpectedGeneration $generation|Out-Null } catch {$refused=$true}
    if(!$refused -or $server.Requests.Count -ne $before) { throw 'A false summary p95 passed or caused extra requests.' }
    $spec.cases[0].query='delay-1';$spec.cases[1].query='delay-2'
    $spec|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $workload -Encoding utf8
    $failed=Join-Path $temporaryRoot 'failed';$refused=$false
    try { & $runner -WorkloadPath $workload -OutputRoot $failed -SiteUrl $url|Out-Null } catch {$refused=$true}
    $summary=Get-Content -LiteralPath (Join-Path $failed 'summary.json') -Raw|ConvertFrom-Json
    if(!$refused -or $summary.observations -ne 40 -or $summary.executed_searches -ne 2 -or !$summary.requires_outcome_and_cleanup_verification -or
        @($summary.results|Where-Object error_type -EQ 'NotStartedAfterFailedGate').Count -ne 38 -or $server.Requests.Count -ne ($before+2)) { throw 'Deadline failure was dropped or more requests were issued after it.' }
    $server.WaitForCleanup().GetAwaiter().GetResult()
    if($server.DelayedStarted -ne 2 -or $server.DelayedCleaned -ne 2) { throw 'Synthetic prior workers did not finish cleanup.' }
    $before=$server.Requests.Count;$refused=$false
    try { & $verifier -WorkloadPath $workload -OutputRoot $failed -SiteUrl $url -ExpectedGeneration $generation|Out-Null } catch {$refused=$true}
    if(!$refused -or $server.Requests.Count -ne $before) { throw 'Partial acceptance passed or performed readback.' }
    'Frozen endpoint checks passed: 40 searches, two callers, exact citations, independent p95, timeout receipts, stopped dispatch and observed synthetic cleanup.'
} finally {
    if($server) {$server.Dispose()}
    $resolved=[IO.Path]::GetFullPath($temporaryRoot)
    if(!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or !(Split-Path -Leaf $resolved).StartsWith('flux-frozen-endpoint-')) {throw 'Unsafe fixture cleanup target.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
