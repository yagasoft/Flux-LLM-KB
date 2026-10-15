using System.Diagnostics;
using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

internal static class Exercise
{
    internal static async Task RunAsync(string root,string origin,IDbContextFactory<FluxKnowledgeDbContext> factory,bool restart,bool retry)
    {
        var manifestFile = Directory.GetFiles(Path.Combine(root,"Recovery","acceptance"),"manifest.json",SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        var verificationComplete = false;
        try
        {
            var accepted = await RunCoreAsync(root,origin,factory,restart,retry,manifestFile);
            verificationComplete = true;
            if (!accepted) throw new InvalidOperationException("Staging acceptance criteria failed; inspect the retained evidence.");
        }
        catch (Exception exception)
        {
            // A failed verifier must leave a failed outcome, never just a terminal
            // deletion row that could be mistaken for successful acceptance.
            var failure = new { accepted = false,verificationComplete,
                failureType = exception.GetType().Name,exception.Message,
                sqlNumber = exception is SqlException sql ? (int?)sql.Number : null };
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(manifestFile)!,"failure.json"),
                JsonSerializer.Serialize(failure,new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
    }
    private static async Task<bool> RunCoreAsync(string root,string origin,IDbContextFactory<FluxKnowledgeDbContext> factory,
        bool restart,bool retry,string manifestFile)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestFile));
        var ownedRoot = manifest.RootElement.GetProperty("rootIds")[0].GetGuid();
        var controlRoot = manifest.RootElement.GetProperty("rootIds")[1].GetGuid();
        var controlName = "Deletion acceptance control " + manifest.RootElement.GetProperty("fixtureId").GetGuid().ToString("N");
        var evidence = Path.GetDirectoryName(manifestFile)!;
        var hasUntouchedGeneration = manifest.RootElement.TryGetProperty("untouchedGenerationId",out var untouchedId);
        if (manifest.RootElement.GetProperty("mode").GetString() == "seed-scale" && !hasUntouchedGeneration)
            throw new InvalidOperationException("Scale requires the current fixture with an explicit survivor-only generation; no mutation sent.");
        using var transport = new HttpClientHandler { UseProxy = false,AllowAutoRedirect = false };
        using var client = new HttpClient(transport) { BaseAddress = new Uri(origin),Timeout = TimeSpan.FromSeconds(5) };
        var baseline = new List<Probe>(); var concurrent = new List<Probe>();
        var failedRoutes = new HashSet<string>(StringComparer.Ordinal);
        string unrelatedBefore;
        await using (var before = await factory.CreateDbContextAsync()) unrelatedBefore = await UnrelatedSignatureAsync(before,ownedRoot);
        await File.WriteAllTextAsync(Path.Combine(evidence,"unrelated-before.sha256"),unrelatedBefore);
        async Task ProbeAsync(List<Probe> samples)
        {
            foreach (var route in new[] { "/","/sources",$"/sources/{controlRoot:D}","/api/v1/corpus/query" })
            {
                var watch = Stopwatch.StartNew();
                var status = 0;
                try
                {
                    using var response = route.EndsWith("query",StringComparison.Ordinal)
                        ? await client.PostAsJsonAsync(route,new { view = "roots",limit = 100 }) : await client.GetAsync(route);
                    var body = await response.Content.ReadAsStringAsync();
                    status = (int)response.StatusCode;
                    var validResponse = response.IsSuccessStatusCode && response.Headers.TryGetValues("X-FluxKnowledge-Environment",out var environments) &&
                        environments.SequenceEqual(["isolated-staging-functional"]);
                    var currentContent = route switch
                    {
                        "/" => HasCurrentOverview(body),
                        "/sources" => body.Contains($"/sources/{controlRoot:D}",StringComparison.OrdinalIgnoreCase),
                        _ when route.StartsWith("/sources/",StringComparison.Ordinal) => body.Contains(controlName,StringComparison.Ordinal),
                        _ => true
                    };
                    if (route.EndsWith("query",StringComparison.Ordinal))
                    {
                        using var json = JsonDocument.Parse(body);
                        currentContent &= json.RootElement.ValueKind == JsonValueKind.Object &&
                            json.RootElement.TryGetProperty("ok",out var ok) && ok.ValueKind == JsonValueKind.True;
                    }
                    var outcome = !validResponse ? "failure" : route == "/" ? OverviewOutcome(body)
                        : currentContent ? "usable" : "failure";
                    var milliseconds = watch.Elapsed.TotalMilliseconds;
                    if (outcome != "usable" && failedRoutes.Add(route))
                        await File.WriteAllTextAsync(Path.Combine(evidence,$"first-failed-read-{failedRoutes.Count}.html"),body);
                    samples.Add(new(route,milliseconds,status,outcome,DateTimeOffset.UtcNow,
                        outcome == "usable" ? null : "HTTP identity or rendered/read data unavailable"));
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
                {
                    samples.Add(new(route,watch.Elapsed.TotalMilliseconds,status,"failure",DateTimeOffset.UtcNow,exception.GetType().Name));
                }
            }
            if (ReferenceEquals(samples,concurrent))
                await File.WriteAllTextAsync(Path.Combine(evidence,"read-samples.json"),JsonSerializer.Serialize(new { baseline,concurrent }));
        }
        var baselineStarted = DateTimeOffset.UtcNow;
        for (var repeat = 0; repeat < 60; repeat++)
        {
            await ProbeAsync(baseline);
            await Task.Delay(1000);
            if ((repeat+1)%15 == 0) Console.WriteLine(JsonSerializer.Serialize(new { stage = "read-baseline",samples = repeat+1 }));
        }
        var baselineOverview = Availability(baseline,baselineStarted,DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(evidence,"baseline-availability.json"),JsonSerializer.Serialize(baselineOverview));
        if (baseline.Any(p => p.Outcome == "failure" || p.Route != "/" && !p.Success) ||
            baselineOverview.UsablePercent < 90 || baselineOverview.MaximumUsableGapSeconds > 10)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence,"failed-read-baseline.json"),JsonSerializer.Serialize(baseline));
            throw new InvalidOperationException("Staging identity/read validation failed; no mutation was sent.");
        }
        var mutation = new { action = "root_delete",payload = new { rootId = ownedRoot } };
        using var previewResponse = await client.PostAsJsonAsync("/api/v1/corpus/actions/preview",mutation);
        var previewText = await previewResponse.Content.ReadAsStringAsync();
        RequireStagingIdentity(previewResponse);
        await File.WriteAllTextAsync(Path.Combine(evidence,"preview.json"),previewText);
        using var preview = JsonDocument.Parse(previewText);
        if (!preview.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Real REST preview failed.");
        var confirmation = preview.RootElement.GetProperty("result").GetProperty("confirmationId").GetString();
        var key = "source-deletion-acceptance:" + ownedRoot.ToString("N");
        async Task<string> CommitAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,"/api/v1/corpus/actions/commit")
            { Content = JsonContent.Create(new { mutation.action,mutation.payload,confirmation_id = confirmation }) };
            request.Headers.Add("Idempotency-Key",key);
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            RequireStagingIdentity(response);
            using var receipt = JsonDocument.Parse(text);
            if (!response.IsSuccessStatusCode || !receipt.RootElement.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException("Real REST commit failed: " + text);
            return text;
        }
        var watch = Stopwatch.StartNew();
        var cleanupStarted = DateTimeOffset.UtcNow;
        var firstReceipt = await CommitAsync(); var replayReceipt = await CommitAsync();
        await File.WriteAllTextAsync(Path.Combine(evidence,"commit.json"),firstReceipt);
        await File.WriteAllTextAsync(Path.Combine(evidence,"replay.json"),replayReceipt);
        using (var first = JsonDocument.Parse(firstReceipt)) using (var replay = JsonDocument.Parse(replayReceipt))
            if (first.RootElement.GetProperty("result").GetProperty("operationId").GetGuid() !=
                replay.RootElement.GetProperty("result").GetProperty("operationId").GetGuid() ||
                !replay.RootElement.GetProperty("result").GetProperty("wasReplay").GetBoolean())
                throw new InvalidOperationException("The repeated REST request did not replay its receipt.");
        var progress = new List<object>();
        var restarted = false;
        var deliberateOutages = new List<ReadOutage>();
        var retried = false;
        Guid? failedOperationId = null;
        while (watch.Elapsed < TimeSpan.FromMinutes(10))
        {
            await using var context = await factory.CreateDbContextAsync();
            var operation = await context.SourceDeletionOperations.AsNoTracking().SingleAsync(o => o.SourceRootId == ownedRoot);
            progress.Add(new { utc = DateTimeOffset.UtcNow,operation.State,operation.Phase,operation.ReasonCode,
                operation.MembershipRowsDeleted,operation.MembershipVectorCursor,operation.LastProgressAtUtc });
            await File.WriteAllTextAsync(Path.Combine(evidence,"progress.json"),JsonSerializer.Serialize(progress));
            Console.WriteLine(JsonSerializer.Serialize(new { elapsedSeconds = watch.Elapsed.TotalSeconds,operation.State,operation.Phase,operation.MembershipRowsDeleted }));
            if (restart && !restarted && operation.Phase == "cleanup-memberships" && operation.MembershipRowsDeleted > 0)
            {
                var outageStarted = DateTimeOffset.UtcNow;
                await using var barrier = await new SqlDerivedIndexRecoveryStore(factory,TimeProvider.System)
                    .TryAcquireSharedLeaseAsync(TimeSpan.FromSeconds(5),default) ?? throw new InvalidOperationException("Cannot establish restart barrier.");
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    await using var check = await factory.CreateDbContextAsync();
                    var held = await check.SourceDeletionOperations.AsNoTracking().SingleAsync(o => o.Id == operation.Id);
                    if (held.State == 0 && held.LeaseId is null && held.ReasonCode == "source-delete-search-queries-active")
                    {
                        await File.WriteAllTextAsync(Path.Combine(evidence,"restart-ready.json"),JsonSerializer.Serialize(new {
                            held.Id,held.Phase,held.State,held.MembershipRowsDeleted,held.MembershipVectorCursor,held.LeaseId }));
                        Console.WriteLine("Restart barrier prepared: " + evidence);
                        break;
                    }
                    await Task.Delay(100);
                }
                if (!File.Exists(Path.Combine(evidence,"restart-ready.json"))) throw new InvalidOperationException("Worker did not yield its claim at the restart barrier.");
                while (!File.Exists(Path.Combine(evidence,"resume-after-restart.signal"))) await Task.Delay(250);
                deliberateOutages.Add(new(outageStarted,DateTimeOffset.UtcNow));
                await File.WriteAllTextAsync(Path.Combine(evidence,"deliberate-restart-outages.json"),JsonSerializer.Serialize(deliberateOutages));
                restarted = true;
            }
            if (operation.State == 4)
            {
                if (!retry || retried || operation.ReasonCode != "source-delete-artifact-binding-invalid" || operation.ResumePhase != "cleanup-files")
                    throw new InvalidOperationException("Cleanup entered attention: " + operation.ReasonCode);
                failedOperationId = operation.Id;
                await File.WriteAllTextAsync(Path.Combine(evidence,"injected-failure.json"),JsonSerializer.Serialize(new {
                    operation.Id,operation.Phase,operation.ResumePhase,operation.ReasonCode,operation.MembershipRowsDeleted }));
                foreach (var original in manifest.RootElement.GetProperty("originals").EnumerateArray().Where(o => o.GetProperty("source").GetInt32() == 0))
                    File.Copy(original.GetProperty("path").GetString()!,Path.Combine(root,"Data","Retained",original.GetProperty("retained").GetString()!),overwrite:true);
                using var response = await client.PostAsJsonAsync("/api/v1/corpus/actions/preview",mutation);
                RequireStagingIdentity(response);
                using var nextPreview = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (!nextPreview.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Retry preview failed.");
                confirmation = nextPreview.RootElement.GetProperty("result").GetProperty("confirmationId").GetString();
                key += ":retry";
                await File.WriteAllTextAsync(Path.Combine(evidence,"retry-commit.json"),await CommitAsync());
                await File.WriteAllTextAsync(Path.Combine(evidence,"retry-replay.json"),await CommitAsync());
                retried = true;
            }
            if (operation.State == 3) break;
            await ProbeAsync(concurrent);
            await Task.Delay(1000);
        }
        var deletionElapsed = watch.Elapsed;
        var completionObservedAt = DateTimeOffset.UtcNow;
        var verificationWatch = Stopwatch.StartNew();
        var concurrentOverview = Availability(concurrent,cleanupStarted,completionObservedAt,deliberateOutages);
        await File.WriteAllTextAsync(Path.Combine(evidence,"cleanup-availability.json"),JsonSerializer.Serialize(concurrentOverview));
        await using var final = await factory.CreateDbContextAsync();
        var completed = await final.SourceDeletionOperations.SingleAsync(o => o.SourceRootId == ownedRoot);
        var checks = new Dictionary<string,object> { ["operationState"] = completed.State,
            ["operationPhase"] = completed.Phase,["membershipRowsDeleted"] = completed.MembershipRowsDeleted,
            ["deletionElapsedSeconds"] = deletionElapsed.TotalSeconds,["completionObservedAtUtc"] = completionObservedAt };
        async Task SaveChecksAsync() => await File.WriteAllTextAsync(Path.Combine(evidence,"verification.json"),
            JsonSerializer.Serialize(checks,new JsonSerializerOptions { WriteIndented = true }));
        await SaveChecksAsync();
        var (originalsIntact,ownedFilesAbsent,unrelatedFilesIntact,historicalNativeGone,controls,
            unrelatedUnchanged,controlHistoricalRows,foreignHistoricalRows,untouchedGenerationPreserved,
            survivorNativeVerified,ownedGraphAbsent) = await VerifyPreservationAsync(
                root,factory,final,manifest,unrelatedBefore,checks,SaveChecksAsync);
        static double? P95(IEnumerable<Probe> values) { var sorted = values.Where(v => v.Success).Select(v => v.Milliseconds).Order().ToArray(); return sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length*.95)-1]; }
        var results = baseline.Select(p => p.Route).Distinct().Select(route => new { route,
            baselineP95Milliseconds = P95(baseline.Where(p => p.Route == route)),
            concurrentP95Milliseconds = P95(concurrent.Where(p => p.Route == route)),
            concurrentMaximumMilliseconds = concurrent.Where(p => p.Route == route && p.Success).Select(p => (double?)p.Milliseconds).Max(),
            usableReads = concurrent.Count(p => p.Route == route && p.Success),
            consistencyUnavailable = concurrent.Count(p => p.Route == route && p.Outcome == "consistency-unavailable"),
            failures = baseline.Concat(concurrent).Count(p => p.Route == route && p.Outcome == "failure") }).ToArray();
        var accepted = completed.State == 3 && (!restart || restarted) && (!retry || retried && completed.Id == failedOperationId) && originalsIntact && ownedFilesAbsent && unrelatedFilesIntact && historicalNativeGone && unrelatedUnchanged && untouchedGenerationPreserved && ownedGraphAbsent && survivorNativeVerified &&
            deletionElapsed < TimeSpan.FromMinutes(10) &&
            completed.MembershipRowsDeleted == manifest.RootElement.GetProperty("historicalOwned").GetInt64()+manifest.RootElement.GetProperty("ownedCount").GetInt64() &&
            controlHistoricalRows == manifest.RootElement.GetProperty("historicalOther").GetInt64() && foreignHistoricalRows == 0 &&
            controls == manifest.RootElement.GetProperty("otherCount").GetInt64() &&
            concurrentOverview.UsablePercent >= 90 && baselineOverview.UsablePercent-concurrentOverview.UsablePercent <= 5 &&
            concurrentOverview.MaximumUsableGapSeconds <= 10 &&
            results.All(r => r.failures == 0 && r.baselineP95Milliseconds is { } idle &&
                r.concurrentP95Milliseconds is { } measured && measured <= Math.Max(2000,2*idle));
        var outcome = new { accepted,restarted,retried,elapsedSeconds = deletionElapsed.TotalSeconds,
            verificationElapsedSeconds = verificationWatch.Elapsed.TotalSeconds,totalCleanupAndVerificationSeconds = watch.Elapsed.TotalSeconds,
            completionObservedAtUtc = completionObservedAt,completed.MembershipRowsDeleted,
            originalsIntact,ownedFilesAbsent,unrelatedFilesIntact,historicalNativeGone,unrelatedUnchanged,survivorNativeVerified,ownedGraphAbsent,
            untouchedGenerationChecked = hasUntouchedGeneration,untouchedGenerationPreserved,
            controlVectors = controls,controlHistoricalRows,foreignHistoricalRows,baselineOverview,concurrentOverview,deliberateOutages,results };
        var output = JsonSerializer.Serialize(outcome,new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(evidence,"result.json"),output);
        await File.WriteAllTextAsync(Path.Combine(evidence,"read-samples.json"),JsonSerializer.Serialize(new { baseline,concurrent }));
        Console.WriteLine(output);
        return accepted;
    }
    internal static async Task VerifyPreservationOnlyAsync(string root,IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        var manifestFile = Directory.GetFiles(Path.Combine(root,"Recovery","acceptance"),"manifest.json",SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        var evidence = Path.GetDirectoryName(manifestFile)!;
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestFile));
        var ownedRoot = manifest.RootElement.GetProperty("rootIds")[0].GetGuid();
        var unrelatedBefore = await File.ReadAllTextAsync(Path.Combine(evidence,"unrelated-before.sha256"));
        await using var context = await factory.CreateDbContextAsync();
        var completed = await context.SourceDeletionOperations.AsNoTracking().SingleAsync(o => o.SourceRootId == ownedRoot);
        if (completed.State != 3) throw new InvalidOperationException("Read-only preservation recheck requires a completed synthetic deletion.");
        var checks = new Dictionary<string,object> { ["acceptanceReevaluated"] = false,
            ["operationId"] = completed.Id,["membershipRowsDeleted"] = completed.MembershipRowsDeleted };
        async Task SaveChecksAsync() => await File.WriteAllTextAsync(Path.Combine(evidence,"preservation-recheck.json"),
            JsonSerializer.Serialize(checks,new JsonSerializerOptions { WriteIndented = true }));
        var result = await VerifyPreservationAsync(root,factory,context,manifest,unrelatedBefore,checks,SaveChecksAsync);
        var passed = result.OriginalsIntact && result.OwnedFilesAbsent && result.UnrelatedFilesIntact &&
            result.HistoricalNativeGone && result.UnrelatedUnchanged && result.UntouchedGenerationPreserved &&
            result.SurvivorNativeVerified && result.OwnedGraphAbsent &&
            result.Controls == manifest.RootElement.GetProperty("otherCount").GetInt64() &&
            result.ControlHistoricalRows == manifest.RootElement.GetProperty("historicalOther").GetInt64() &&
            result.ForeignHistoricalRows == 0 && completed.MembershipRowsDeleted ==
                manifest.RootElement.GetProperty("historicalOwned").GetInt64()+manifest.RootElement.GetProperty("ownedCount").GetInt64();
        checks["preservationPassed"] = passed;
        await SaveChecksAsync();
        Console.WriteLine(JsonSerializer.Serialize(checks));
        if (!passed) throw new InvalidOperationException("Preservation recheck failed; original acceptance evidence is unchanged.");
    }
    private static async Task<Preservation> VerifyPreservationAsync(string root,
        IDbContextFactory<FluxKnowledgeDbContext> factory,FluxKnowledgeDbContext final,JsonDocument manifest,
        string unrelatedBefore,Dictionary<string,object> checks,Func<Task> SaveChecksAsync)
    {
        var ownedRoot = manifest.RootElement.GetProperty("rootIds")[0].GetGuid();
        var controlRoot = manifest.RootElement.GetProperty("rootIds")[1].GetGuid();
        var hasUntouchedGeneration = manifest.RootElement.TryGetProperty("untouchedGenerationId",out var untouchedId);
        if (manifest.RootElement.GetProperty("mode").GetString() == "seed-scale" && !hasUntouchedGeneration)
            throw new InvalidOperationException("Scale preservation requires the current untouched-generation fixture.");
        var originalsIntact = true; var ownedFilesAbsent = true; var unrelatedFilesIntact = true;
        foreach (var original in manifest.RootElement.GetProperty("originals").EnumerateArray())
        {
            var bytes = await File.ReadAllBytesAsync(original.GetProperty("path").GetString()!);
            originalsIntact &= Convert.ToHexStringLower(SHA256.HashData(bytes)) == original.GetProperty("sha256").GetString();
            var retained = Path.Combine(root,"Data","Retained",original.GetProperty("retained").GetString()!);
            if (original.GetProperty("source").GetInt32() == 0) ownedFilesAbsent &= !File.Exists(retained);
            else unrelatedFilesIntact &= File.Exists(retained) && Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(retained))) == original.GetProperty("sha256").GetString();
        }
        var historicalNativeGone = manifest.RootElement.GetProperty("generationIds").EnumerateArray()
            .All(id => !Directory.Exists(Path.Combine(root,"Data","Index","generations",id.GetGuid().ToString("N"))));
        var initialNativeGone = !Directory.Exists(Path.Combine(root,"Data","Index","generations",
            manifest.RootElement.GetProperty("activeGeneration").GetGuid().ToString("N")));
        historicalNativeGone &= initialNativeGone;
        checks["originalsIntact"] = originalsIntact; checks["ownedFilesAbsent"] = ownedFilesAbsent;
        checks["unrelatedFilesIntact"] = unrelatedFilesIntact; checks["historicalNativeGone"] = historicalNativeGone;
        checks["initialNativeGone"] = initialNativeGone;
        await SaveChecksAsync();
        var controls = await final.Vectors.LongCountAsync(v => final.TextChunks.Any(c => c.Id == v.TextChunkId &&
            final.Artifacts.Any(a => a.Id == c.ArtifactId && final.PipelineRecords.Any(p => p.Id == a.PipelineRecordId &&
                final.SourceRevisions.Any(r => r.Id == p.SourceRevisionId && r.SourceRootId == controlRoot)))));
        var unrelatedUnchanged = unrelatedBefore == await UnrelatedSignatureAsync(final,ownedRoot);
        var historyIds = manifest.RootElement.GetProperty("generationIds").EnumerateArray().Select(i => i.GetGuid()).ToArray();
        checks["controlVectors"] = controls; checks["unrelatedCanonicalUnchanged"] = unrelatedUnchanged;
        await SaveChecksAsync();
        var (controlHistoricalRows,foreignHistoricalRows) = await CountHistoricalControlsAsync(final,controlRoot,historyIds);
        checks["controlHistoricalRows"] = controlHistoricalRows; checks["foreignHistoricalRows"] = foreignHistoricalRows;
        await SaveChecksAsync();
        var untouchedGenerationPreserved = true;
        if (hasUntouchedGeneration)
        {
            var untouched = await final.IndexGenerations.AsNoTracking().SingleOrDefaultAsync(g => g.Id == untouchedId.GetGuid());
            var untouchedVector = manifest.RootElement.GetProperty("untouchedVectorId").GetInt64();
            var directory = Path.Combine(root,"Data","Index","generations",untouchedId.GetGuid().ToString("N"));
            var marker = Path.Combine(directory,"untouched-history.txt");
            untouchedGenerationPreserved = untouched is not null && untouched.RetiredAtUtc is null &&
                untouched.IndexPath == directory && untouched.VectorCount == 1 && untouched.Dimensions == 1 &&
                untouched.ModelFingerprint == "source-deletion-synthetic:1" && untouched.MetadataChecksum == new string('b',64) &&
                Convert.ToHexStringLower(untouched.RowVersion) == manifest.RootElement.GetProperty("untouchedRowVersion").GetString() &&
                await final.IndexGenerationVectors.CountAsync(m => m.GenerationId == untouchedId.GetGuid()) == 1 &&
                await final.IndexGenerationVectors.AnyAsync(m => m.GenerationId == untouchedId.GetGuid() && m.VectorId == untouchedVector) &&
                File.Exists(marker) && Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(marker))) ==
                    manifest.RootElement.GetProperty("untouchedMarkerSha256").GetString();
        }
        checks["untouchedGenerationChecked"] = hasUntouchedGeneration;
        checks["untouchedGenerationPreserved"] = untouchedGenerationPreserved;
        await SaveChecksAsync();
        var activeId = (await final.IndexState.SingleAsync()).ActiveIndexGenerationId ?? throw new InvalidOperationException("Survivor index is missing.");
        var indexStore = new SqlPipelineStore(factory,TimeProvider.System);
        var active = await indexStore.GetGenerationAsync(activeId,default) ?? throw new InvalidOperationException("Survivor descriptor is missing.");
        new UsearchGenerationValidator().Validate(active.IndexPath,active,await indexStore.ReadVectorsAsync(activeId,default));
        var survivorControls = await final.IndexGenerationVectors.LongCountAsync(m => m.GenerationId == activeId &&
            final.Vectors.Any(v => v.VectorId == m.VectorId && final.TextChunks.Any(c => c.Id == v.TextChunkId &&
                final.Artifacts.Any(a => a.Id == c.ArtifactId && final.PipelineRecords.Any(p => p.Id == a.PipelineRecordId &&
                    final.SourceRevisions.Any(r => r.Id == p.SourceRevisionId && r.SourceRootId == controlRoot))))));
        var survivorNativeVerified = survivorControls == controls;
        checks["survivorNativeVerified"] = survivorNativeVerified;
        checks["survivorControlVectors"] = survivorControls;
        await SaveChecksAsync();
        var ownedRecords = manifest.RootElement.GetProperty("originals").EnumerateArray()
            .Where(o => o.GetProperty("source").GetInt32() == 0)
            .Select(o => Guid.Parse(Path.GetFileNameWithoutExtension(o.GetProperty("path").GetString()!))).ToArray();
        var ownedGraphAbsent = !await final.SourceRevisions.AnyAsync(r => r.SourceRootId == ownedRoot) &&
            !await final.PipelineRecords.AnyAsync(p => ownedRecords.Contains(p.Id));
        checks["ownedGraphAbsent"] = ownedGraphAbsent;
        await SaveChecksAsync();
        return new(originalsIntact,ownedFilesAbsent,unrelatedFilesIntact,historicalNativeGone,controls,
            unrelatedUnchanged,controlHistoricalRows,foreignHistoricalRows,untouchedGenerationPreserved,
            survivorNativeVerified,ownedGraphAbsent);
    }
    private sealed record Preservation(bool OriginalsIntact,bool OwnedFilesAbsent,bool UnrelatedFilesIntact,
        bool HistoricalNativeGone,long Controls,bool UnrelatedUnchanged,long ControlHistoricalRows,
        long ForeignHistoricalRows,bool UntouchedGenerationPreserved,bool SurvivorNativeVerified,bool OwnedGraphAbsent);
    private static async Task<(long Controls,long Foreign)> CountHistoricalControlsAsync(FluxKnowledgeDbContext context,
        Guid controlRoot,IEnumerable<Guid> generationIds)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            var connection = (SqlConnection)context.Database.GetDbConnection();
            // An unparameterised batch creates the table in this connection's
            // scope; creating it inside sp_executesql would drop it on return.
            await using (var create = new SqlCommand(
                "CREATE TABLE #AcceptanceControlVectors (VectorId bigint NOT NULL PRIMARY KEY);",connection))
                await create.ExecuteNonQueryAsync();
            await using (var setup = new SqlCommand("""
                INSERT INTO #AcceptanceControlVectors
                SELECT v.VectorId FROM Vectors v
                INNER JOIN TextChunks c ON c.Id = v.TextChunkId
                INNER JOIN Artifacts a ON a.Id = c.ArtifactId
                INNER JOIN PipelineRecords p ON p.Id = a.PipelineRecordId
                INNER JOIN SourceRevisions r ON r.Id = p.SourceRevisionId
                WHERE r.SourceRootId = @root;
                """,connection))
            {
                setup.Parameters.Add("@root",SqlDbType.UniqueIdentifier).Value = controlRoot;
                await setup.ExecuteNonQueryAsync();
            }
            // Bound each statement to one clustered GenerationId range. The
            // typed control table has real statistics; no OPENJSON fan-out estimate.
            await using var count = new SqlCommand("""
                SELECT COUNT_BIG(c.VectorId), COUNT_BIG(*) - COUNT_BIG(c.VectorId)
                FROM IndexGenerationVectors m LEFT JOIN #AcceptanceControlVectors c ON c.VectorId = m.VectorId
                WHERE m.GenerationId = @generation OPTION (RECOMPILE);
                """,connection);
            var parameter = count.Parameters.Add("@generation",SqlDbType.UniqueIdentifier);
            long controls = 0,foreign = 0;
            foreach (var generation in generationIds)
            {
                parameter.Value = generation;
                await using var reader = await count.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) throw new InvalidOperationException("Historical verification returned no count.");
                controls = checked(controls+reader.GetInt64(0)); foreign = checked(foreign+reader.GetInt64(1));
            }
            await using var drop = new SqlCommand("DROP TABLE #AcceptanceControlVectors",connection);
            await drop.ExecuteNonQueryAsync();
            return (controls,foreign);
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }
    private static void RequireStagingIdentity(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-FluxKnowledge-Environment",out var values) ||
            values.Single() != "isolated-staging-functional") throw new InvalidOperationException("Unexpected responder; refusing further mutations.");
    }
    private static async Task<string> UnrelatedSignatureAsync(FluxKnowledgeDbContext context,Guid excluded)
    {
        var revisions = context.SourceRevisions.Where(r => r.SourceRootId != excluded);
        var records = context.PipelineRecords.Where(p => context.SourceRevisions.Any(r => r.Id == p.SourceRevisionId && r.SourceRootId != excluded));
        var artifacts = context.Artifacts.Where(a => records.Any(p => p.Id == a.PipelineRecordId));
        var chunks = context.TextChunks.Where(c => artifacts.Any(a => a.Id == c.ArtifactId));
        var snapshot = new {
            revisions = await revisions.OrderBy(r => r.Id).Select(r => new { r.Id,r.SourceRootId,r.RowVersion }).ToArrayAsync(),
            identities = await context.SourceIdentities.Where(i => records.Any(p => p.SourceIdentityId == i.Id)).OrderBy(i => i.Id)
                .Select(i => new { i.Id,i.SourceKind,i.StableKey,i.CreatedAtUtc }).ToArrayAsync(),
            retained = await context.SourceArtifacts.Where(a => revisions.Any(r => r.Id == a.SourceRevisionId)).OrderBy(a => a.Id)
                .Select(a => new { a.Id,a.SourceRevisionId,a.RowVersion }).ToArrayAsync(),
            records = await records.OrderBy(p => p.Id).Select(p => new { p.Id,p.SourceRevisionId,p.SourceIdentityId,p.ContentHash,p.IsDeleted,p.Revision }).ToArrayAsync(),
            artifacts = await artifacts.OrderBy(a => a.Id).Select(a => new { a.Id,a.PipelineRecordId,a.ContentHash,a.SearchText,a.SourceRevision,a.Stage }).ToArrayAsync(),
            chunks = await chunks.OrderBy(c => c.Id).Select(c => new { c.Id,c.ArtifactId,c.SourceRevision,c.Ordinal,c.StartOffset,c.Length,c.Content,c.ContentHash }).ToArrayAsync(),
            vectors = await context.Vectors.Where(v => chunks.Any(c => c.Id == v.TextChunkId)).OrderBy(v => v.VectorId)
                .Select(v => new { v.VectorId,v.TextChunkId,v.SourceRevision,v.ModelFingerprint,v.Dimensions,v.Values,v.PayloadChecksum,v.TextChunkContentHash,v.IsDeleted,v.IndexGenerationId }).ToArrayAsync() };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot)));
    }
    internal static void VerifyReadClassifier()
    {
        const string timestamp = "Status as of <time datetime=\"2026-10-14T12:00:00Z\">12:00</time>";
        (string Name,string Body,string Expected)[] cases = [
            ("fresh",timestamp,"usable"),
            ("source warning",timestamp+"Source watcher warning","usable"),
            ("changed with old timestamp",timestamp+"Status changed during the read.","consistency-unavailable"),
            ("initial busy","Status is busy. Waiting for a consistent sample. Status unavailable","consistency-unavailable"),
            ("busy with old timestamp",timestamp+"Status is busy. Waiting for a consistent sample.","consistency-unavailable"),
            ("genuine failure",timestamp+"Status refresh failed.","failure"),
            ("busy and genuine failure",timestamp+"Status is busy. Status refresh failed.","failure"),
            ("missing timestamp","Healthy","failure") ];
        foreach (var item in cases)
            if (OverviewOutcome(item.Body) != item.Expected)
                throw new InvalidOperationException("Read classifier failed: "+item.Name);
        Console.WriteLine($"Read classifier passed {cases.Length} cases; stale/busy samples are unavailable.");
    }
    private static string OverviewOutcome(string body) => HasCurrentOverview(body) ? "usable"
        : (body.Contains("Status changed during the read.",StringComparison.Ordinal) ||
           body.Contains("Status is busy.",StringComparison.Ordinal)) &&
          !body.Contains("Status refresh failed.",StringComparison.Ordinal) ? "consistency-unavailable" : "failure";

    private static bool HasCurrentOverview(string body)
    {
        if (body.Contains("Status is busy.",StringComparison.Ordinal)) return false;
        var timestamp = Regex.Match(body,"Status as of\\s*<time\\s+datetime=\"([^\"]+)\"",RegexOptions.CultureInvariant);
        return timestamp.Success && DateTimeOffset.TryParse(WebUtility.HtmlDecode(timestamp.Groups[1].Value),
            CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out _) &&
            !body.Contains("Status changed during the read.",StringComparison.Ordinal) &&
            !body.Contains("Status unavailable",StringComparison.Ordinal) &&
            !body.Contains("Status refresh failed.",StringComparison.Ordinal);
    }
    private static OverviewAvailability Availability(IEnumerable<Probe> samples,DateTimeOffset start,DateTimeOffset end,
        IReadOnlyList<ReadOutage>? deliberateOutages = null)
    {
        var overview = samples.Where(p => p.Route == "/").ToArray();
        var last = start; var maximum = 0d;
        foreach (var sample in overview.Where(p => p.Success).OrderBy(p => p.ObservedAtUtc))
        {
            maximum = Math.Max(maximum,MeasuredGap(last,sample.ObservedAtUtc));
            last = sample.ObservedAtUtc;
        }
        maximum = Math.Max(maximum,MeasuredGap(last,end));
        return new(overview.Length,overview.Count(p => p.Success),overview.Count(p => p.Outcome == "consistency-unavailable"),
            overview.Length == 0 ? 0 : 100d*overview.Count(p => p.Success)/overview.Length,maximum);
        double MeasuredGap(DateTimeOffset from,DateTimeOffset to) => Math.Max(0,(to-from).TotalSeconds -
            (deliberateOutages ?? []).Sum(outage => Math.Max(0,
                ((outage.End < to ? outage.End : to)-(outage.Start > from ? outage.Start : from)).TotalSeconds)));
    }
    private sealed record ReadOutage(DateTimeOffset Start,DateTimeOffset End);
    private sealed record OverviewAvailability(int Samples,int Usable,int ConsistencyUnavailable,double UsablePercent,double MaximumUsableGapSeconds);
    private sealed record Probe(string Route,double Milliseconds,int Status,string Outcome,DateTimeOffset ObservedAtUtc,string? Failure = null)
    {
        public bool Success => Outcome == "usable";
    }
}
