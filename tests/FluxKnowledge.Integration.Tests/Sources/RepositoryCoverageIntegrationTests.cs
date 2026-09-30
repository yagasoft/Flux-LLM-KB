using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

[Collection("sql-full-text")]
public sealed class RepositoryCoverageIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        // Each case owns a fresh root. Quiesce preceding cases in this disposable database without deleting immutable receipts.
        await using var context = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        await context.SourceRootConfigurations.ExecuteUpdateAsync(setters => setters
            .SetProperty(root => root.State, (int)SourceRootState.Paused)
            .SetProperty(root => root.ConfigurationRevision, root => root.ConfigurationRevision + 1));
    }
    public Task DisposeAsync() => Task.CompletedTask;
    [NativeSqlServerFact]
    public async Task Single_native_repository_configuration_publishes_code_and_cited_docs_then_converges_lifecycle()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        var clock = new Clock();
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrelated baseline", clock: clock);
        using var repository = new Repository();
        repository.Write("Code.cs", "namespace Coverage; public class CoverageFact { public string Read() => \"code needle quartz\"; public string Use() => Read(); }");
        repository.Write("Guide.md", "Documentation needle quartz is retained with citations.");
        var formats = new Dictionary<string, string>
        {
            ["Script.ps1"] = "Write-Output 'lighthouse script'", ["Module.psm1"] = "function Get-Light { 'lighthouse module' }",
            ["Page.razor"] = "<p>lighthouse page</p>", ["Style.css"] = ".lighthouse { color: red; }",
            ["Query.sql"] = "select 'lighthouse query';", ["Client.js"] = "const lighthouse = 'client';",
            ["Project.csproj"] = "<Project><!-- lighthouse project --></Project>",
            ["Build.props"] = "<Project><!-- lighthouse build --></Project>",
            ["Solution.slnx"] = "<Solution><!-- lighthouse solution --></Solution>"
        };
        foreach (var format in formats) repository.Write(format.Key, format.Value);
        foreach (var excluded in new[] { "private/blocked.md", "artifacts/build.md", "models/weights.json", "appsettings.Local.json" })
            repository.Write(excluded, "Excluded retention sentinel.");
        repository.Git("add", ".");
        repository.Write("untracked.md", "Private sentinel must never be retained.");
        var service = Commands(repository.Path, environment.Factory, clock);
        var rules = Enumerable.Range(0, 900).Select(i => $"excluded-never-match-{i:D4}-with-long-enough-name/**").ToArray();
        var mutation = Mutation("root_create", new { path = repository.Path, displayName = "Repository coverage", discoveryMode = "git-tracked", indexSourceText = true, excludePatterns = rules });
        Assert.True(mutation.Payload.GetRawText().Length > 32 * 1024);
        await Commit(service, mutation);
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            var persisted = await db.SourceRootConfigurations.SingleAsync(value => value.DisplayName == "Repository coverage");
            Assert.Equal(1, persisted.CrawlMode); Assert.Equal(rules, JsonSerializer.Deserialize<string[]>(persisted.ExcludePatternsJson));
        }
        var scanStore = new SqlSourceScanStore(environment.Factory, clock);
        var rootId = await ScanAndPublish(environment, scanStore, clock);
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(new SqlSourceActivityStore(environment.Factory, clock), new LocalSourceCapabilityHandlerRegistry([new RetainedCsharpCodeCapabilityHandler()])),
            new SqlRetainedProcessorBranchStore(environment.Factory, clock), new SqlRetainedSourceReader(environment.Factory, environment.ArtifactRoot),
            new ZipArchiveRetainedProcessor(new SqlRetainedArtifactWriter(environment.Factory, environment.ArtifactRoot)),
            new RetainedProcessorOptions { CsharpCodeEnabled = true, ArchiveZipExpandEnabled = false }, clock,
            csharpProcessor: new RetainedCsharpCodeProcessor(new SqlRetainedSourceReader(environment.Factory, environment.ArtifactRoot), new LocalPrivateContentDisclosure()));
        await Commit(service, Mutation("source_sync", new { rootId }));
        var promotion = activation.RunOnceAsync(default).AsTask();
        var reconciliation = ScanAndPublish(environment, scanStore, clock);
        await Task.WhenAll(promotion, reconciliation);
        Assert.Equal(1, promotion.Result.CompletedBranches);
        Assert.Equal(rootId, reconciliation.Result);
        Guid codeBranchId;
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            codeBranchId = Assert.Single(await db.SourceProcessorCodeDocuments.Where(value => db.SourceRevisions.Any(r => r.Id == value.SourceRevisionId && r.SourceRootId == rootId)).ToListAsync()).SourceProcessorBranchId;
            Assert.True(await db.SourceProcessorCodeSymbols.AnyAsync(value => value.LocalName == "CoverageFact"));
            Assert.Equal(2 + formats.Count, await db.SourceRevisions.CountAsync(value => value.SourceRootId == rootId));
            Assert.True(await db.SourceProcessorCodeReferences.AnyAsync(value => db.SourceProcessorCodeDocuments.Any(document =>
                document.SourceProcessorBranchId == value.DocumentId && db.SourceRevisions.Any(revision => revision.Id == document.SourceRevisionId && revision.SourceRootId == rootId))));
        }
        var cursors = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var codeQuery = new NativeCodeQueryService(new SqlNativeV1ProjectionReader(environment.Factory, new LocalPrivateContentDisclosure(), new UnusedDetailReader(), cursors), cursors);
        var matches = JsonSerializer.SerializeToElement(await codeQuery.ExecuteAsync(new("matches", "global::Coverage.CoverageFact", codeBranchId, 10, null), default));
        Assert.Contains(matches.GetProperty("items").EnumerateArray(), item => item.GetProperty("qualifiedName").GetProperty("value").GetString() == "global::Coverage.CoverageFact");
        var retrieval = new CorpusRetrievalService(new SqlCorpusRetrievalReader(environment.Factory), new EvidenceCodec(), new LocalPrivateContentDisclosure());
        var hits = (await retrieval.SearchAsync(new CorpusSearchRequest("needle quartz", 10, "root", rootId, null), default)).Results;
        Assert.Equal(2, hits.Count);
        var original = Assert.Single(hits, hit => hit.Title == "Guide.md");
        // Zero context reads start at the cited match, which can follow the paragraph's opening word.
        Assert.Contains("needle quartz is retained with citations", (await retrieval.ReadAsync(new CorpusReadRequest(original.EvidenceRef, 0), default)).Text);
        var formatHits = (await retrieval.SearchAsync(new CorpusSearchRequest("lighthouse", 20, "root", rootId, null), default)).Results;
        Assert.Equal(formats.Keys.OrderBy(value => value), formatHits.Select(value => value.Title).OrderBy(value => value));
        foreach (var hit in formatHits) Assert.Contains("lighthouse", (await retrieval.ReadAsync(new CorpusReadRequest(hit.EvidenceRef, 0), default)).Text);

        // Watcher hints release normal durable scans; no configuration or per-file list changes.
        repository.Write("Guide.md", "Replacement documentation needle amber.");
        repository.Write("Added.py", "# Added source needle amber\nprint('hello')\n"); repository.Git("add", "Added.py");
        var watcher = new SourceWatchCoordinator(new SqlSourceRootWatchStore(environment.Factory, clock));
        await watcher.RecordAsync(new SourceWatchSignal(new SourceRootId(rootId), SourceWatchSignalKind.Changed, clock.Now), default);
        clock.Advance(TimeSpan.FromMinutes(2)); Assert.Equal(1, await watcher.ReleaseDueAsync(clock.Now, TimeSpan.FromMinutes(1), default));
        await ScanAndPublish(environment, scanStore, clock);
        await Assert.ThrowsAsync<NativeOperationException>(() => retrieval.ReadAsync(new CorpusReadRequest(original.EvidenceRef, 0), default).AsTask());
        Assert.Equal(2, (await retrieval.SearchAsync(new CorpusSearchRequest("amber", 10, "root", rootId, null), default)).Results.Count);

        // Periodic-only scan: rename, working-file deletion, and untracking with bytes left on disk.
        repository.Git("mv", "Guide.md", "Renamed.md"); repository.Git("add", "Code.cs"); repository.Git("rm", "--cached", "Code.cs");
        File.Delete(System.IO.Path.Combine(repository.Path, "Added.py")); clock.Advance(TimeSpan.FromMinutes(16));
        await ScanAndPublish(environment, scanStore, clock);
        var final = Assert.Single((await retrieval.SearchAsync(new CorpusSearchRequest("amber", 10, "root", rootId, null), default)).Results);
        Assert.Equal("Renamed.md", final.Title);
        Assert.Empty((await retrieval.SearchAsync(new CorpusSearchRequest("quartz", 10, "root", rootId, null), default)).Results);
        repository.Git("add", "-u"); repository.Git("rm", "--cached", "-r", "."); clock.Advance(TimeSpan.FromMinutes(16));
        await ScanAndPublish(environment, scanStore, clock);
        Assert.Empty((await retrieval.SearchAsync(new CorpusSearchRequest("lighthouse", 20, "root", rootId, null), default)).Results);
        await Assert.ThrowsAsync<NativeOperationException>(() => retrieval.ReadAsync(new CorpusReadRequest(final.EvidenceRef, 0), default).AsTask());
        await using var verify = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(1, (await verify.SourceRootConfigurations.SingleAsync(value => value.Id == rootId)).ConfigurationRevision);
    }

    [NativeSqlServerFact]
    public async Task Git_suppression_requires_current_configuration_and_unexpired_owned_scan_lease()
    {
        using var repository = new Repository(); repository.Write("Code.cs", "class Code { }"); repository.Git("add", ".");
        var factory = SqlTestData.CreateFactory(fixture); var clock = new Clock();
        await Commit(Commands(repository.Path, factory, clock), Mutation("root_create", new { path = repository.Path, displayName = "Fence", discoveryMode = "git-tracked", indexSourceText = true }));
        var store = new SqlSourceScanStore(factory, clock);
        var claim = Assert.IsType<ClaimedSourceScan>(await store.ClaimNextReleasedAsync("first", clock.Now, TimeSpan.FromMinutes(1), default));
        var enumerator = new LocalSourceEnumerator();
        var files = new List<SourceDiscoveredFile>(); await foreach (var file in enumerator.EnumerateAsync(claim.SourceRoot, default)) files.Add(file);
        var artifacts = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FluxCoverageArtifacts_{Guid.NewGuid():N}");
        try
        {
            using var artifactStore = new ContentAddressedSourceArtifactStore(artifacts);
            var file = Assert.Single(files) with { ScanOwnership = new(claim.ScanRequest.Id, claim.ScanRequest.Lease!) };
            var receipt = await artifactStore.PutFileAsync(file, new(file.ContentSha256, "application/octet-stream", file.ByteLength), default);
            var revision = await store.ConvergeRevisionAndArtifactAsync(claim.SourceRoot, file, receipt, default);
            var watcher = new SourceWatchCoordinator(new SqlSourceRootWatchStore(factory, clock));
            await watcher.RecordAsync(new SourceWatchSignal(claim.SourceRoot.Id, SourceWatchSignalKind.Changed, clock.Now), default);
            Assert.Equal(1, await watcher.ReleaseDueAsync(clock.Now.AddSeconds(3), TimeSpan.FromMinutes(1), default));
            Assert.Null(await store.ClaimNextReleasedAsync("overlapping", clock.Now.AddSeconds(3), TimeSpan.FromMinutes(1), default));
            clock.Advance(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ConvergeRevisionAndArtifactAsync(claim.SourceRoot, file, receipt, default).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(claim, new(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 1, 0, 0), null, default).AsTask());
            Assert.False(await store.SuppressUnseenAuthoritativelyAsync(claim.SourceRoot, claim.ScanRequest, enumerator.LastInventory!, new HashSet<SourceRevisionId>(), default));
            var successor = Assert.IsType<ClaimedSourceScan>(await store.ClaimNextReleasedAsync("second", clock.Now, TimeSpan.FromMinutes(1), default));
            Assert.False(await store.SuppressUnseenAuthoritativelyAsync(claim.SourceRoot, claim.ScanRequest, enumerator.LastInventory!, new HashSet<SourceRevisionId>(), default));
            await store.SuppressUnseenAsync(claim.SourceRoot.Id, new HashSet<SourceRevisionId>(), default); // Legacy path cannot bypass fencing.
            await using (var db = await factory.CreateDbContextAsync()) Assert.Null((await db.SourceRevisions.FindAsync(revision.Value))!.SuppressedAtUtc);
            await using (var db = await factory.CreateDbContextAsync()) { (await db.SourceRootConfigurations.FindAsync(claim.SourceRoot.Id.Value))!.ConfigurationRevision++; await db.SaveChangesAsync(); }
            Assert.False(await store.SuppressUnseenAuthoritativelyAsync(successor.SourceRoot, successor.ScanRequest, enumerator.LastInventory!, new HashSet<SourceRevisionId>(), default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(successor, new(successor.SourceRoot.Id, successor.ScanRequest.Id, 1, 1, 0, 0), null, default).AsTask());
        }
        finally { if (Directory.Exists(artifacts)) Directory.Delete(artifacts, true); }
    }

    private static async Task<Guid> ScanAndPublish(SqlToUsearchRebuildTests.PipelineEnvironment environment, SqlSourceScanStore store, Clock clock)
    {
        var claim = Assert.IsType<ClaimedSourceScan>(await store.ClaimNextReleasedAsync("coverage-worker", clock.Now, TimeSpan.FromMinutes(10), default));
        using var artifacts = new ContentAddressedSourceArtifactStore(environment.ArtifactRoot);
        var worker = new SourceScanWorker(new LocalSourceEnumerator(), store, artifacts, new SqlSourceActivityStore(environment.Factory, clock),
            new RetainedTextActivityPlanner(new SqlRetainedTextRegistrationStore(environment.Factory, clock)));
        var result = await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default);
        await store.CompleteAsync(claim, result, null, default); await environment.PumpAsync(); return claim.SourceRoot.Id.Value;
    }

    [NativeSqlServerTheory]
    [InlineData("legacy")]
    [InlineData("descriptor")]
    [InlineData("capability")]
    public async Task Git_code_registration_refuses_legacy_routes_and_mismatched_durable_safety_identity(string mismatch)
    {
        using var repository = new Repository(); repository.Write("Code.cs", "class Code { }"); repository.Git("add", ".");
        var factory = SqlTestData.CreateFactory(fixture); var clock = new Clock();
        await Commit(Commands(repository.Path, factory, clock), Mutation("root_create", new { path = repository.Path, displayName = "Admission", discoveryMode = "git-tracked", indexSourceText = true }));
        var store = new SqlSourceScanStore(factory, clock);
        var claim = Assert.IsType<ClaimedSourceScan>(await store.ClaimNextReleasedAsync("admission", clock.Now, TimeSpan.FromMinutes(10), default));
        Assert.Equal(repository.Path, claim.SourceRoot.CanonicalPath);
        var enumerator = new LocalSourceEnumerator(); var files = new List<SourceDiscoveredFile>();
        await foreach (var file in enumerator.EnumerateAsync(claim.SourceRoot, default)) files.Add(file);
        var artifacts = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FluxCoverageAdmission_{Guid.NewGuid():N}");
        try
        {
            using var artifactStore = new ContentAddressedSourceArtifactStore(artifacts);
            var file = Assert.Single(files) with { ScanOwnership = new(claim.ScanRequest.Id, claim.ScanRequest.Lease!) };
            var receipt = await artifactStore.PutFileAsync(file, new(file.ContentSha256, "application/octet-stream", file.ByteLength), default);
            var revision = await store.ConvergeRevisionAndArtifactAsync(claim.SourceRoot, file, receipt, default);
            var activity = SourceActivity.Create(revision, SourceActivityKind.TextExtraction, ExecutionClass.InProcess,
                mismatch == "legacy" ? "phase-3a-v1" : RepositorySourceTextPolicy.ProcessorVersion, file.ContentSha256, null, null,
                descriptorFingerprint: mismatch == "legacy" ? null : RepositorySourceTextPolicy.DescriptorFingerprint);
            await using (var setup = await factory.CreateDbContextAsync())
            {
                setup.SourceActivities.Add(new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.SourceActivityEntity {
                    Id = activity.Id.Value, SourceRevisionId = revision.Value, ActivityKind = (int)activity.Kind, ExecutionClass = (int)activity.ExecutionClass,
                    ProcessorVersion = activity.ProcessorVersion, InputFingerprint = activity.InputFingerprint,
                    DescriptorFingerprint = mismatch == "descriptor" ? new string('d', 64) : activity.DescriptorFingerprint,
                    RequiredCapability = mismatch == "capability" ? "unexpected-capability" : null,
                    State = (int)SourceActivityState.Pending, CreatedAtUtc = clock.Now, UpdatedAtUtc = clock.Now });
                await setup.SaveChangesAsync();
            }
            Assert.False(await new SqlRetainedTextRegistrationStore(factory, clock).RegisterAsync(activity, default));
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Null((await verify.SourceActivities.FindAsync(activity.Id.Value))!.ResultingPipelineRecordId);
            Assert.False(await verify.PipelineRecords.AnyAsync(value => value.SourceRevisionId == revision.Value));
        }
        finally { if (Directory.Exists(artifacts)) Directory.Delete(artifacts, true); }
    }

    [NativeSqlServerFact]
    public async Task Git_lease_renewal_allows_long_scans_and_pause_fences_old_workers_before_resume()
    {
        using var repository = new Repository(); repository.Write("Guide.md", "Renewed scan fact"); repository.Git("add", ".");
        var factory = SqlTestData.CreateFactory(fixture); var clock = new Clock();
        var commands = Commands(repository.Path, factory, clock);
        await Commit(commands, Mutation("root_create", new { path = repository.Path, displayName = "Renewal", discoveryMode = "git-tracked", indexSourceText = true }));
        var store = new SqlSourceScanStore(factory, clock);
        var claim = Assert.IsType<ClaimedSourceScan>(await store.ClaimNextReleasedAsync("renewing", clock.Now, TimeSpan.FromMinutes(15), default));
        Assert.Equal(repository.Path, claim.SourceRoot.CanonicalPath);
        for (var step = 0; step < 3; step++)
        {
            clock.Advance(TimeSpan.FromMinutes(6));
            Assert.True(await ((ISourceScanControlStore)store).RenewLeaseAsync(claim, TimeSpan.FromMinutes(15), default));
        }
        await store.CompleteAsync(claim, new(claim.SourceRoot.Id, claim.ScanRequest.Id, 1, 1, 0, 0), null, default);
        await Commit(commands, Mutation("source_sync", new { rootId = claim.SourceRoot.Id.Value }));
        var next = Assert.IsType<ClaimedSourceScan>(await store.ClaimNextReleasedAsync("next", clock.Now, TimeSpan.FromMinutes(15), default));
        Assert.Equal(claim.SourceRoot.Id, next.SourceRoot.Id);
        Assert.False(await ((ISourceScanControlStore)store).RenewLeaseAsync(claim, TimeSpan.FromMinutes(15), default));
        await Commit(commands, Mutation("root_pause", new { rootId = claim.SourceRoot.Id.Value }));
        Assert.False(await ((ISourceScanControlStore)store).RenewLeaseAsync(next, TimeSpan.FromMinutes(15), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(next, new(next.SourceRoot.Id, next.ScanRequest.Id, 1, 1, 0, 0), null, default).AsTask());
        clock.Advance(TimeSpan.FromMinutes(16));
        // Old workers filter on State; a paused Git root is ineligible even if they ignore CrawlMode.
        await using (var verify = await factory.CreateDbContextAsync())
        {
            var root = await verify.SourceRootConfigurations.FindAsync(next.SourceRoot.Id.Value);
            Assert.Equal((int)SourceRootState.Paused, root!.State); Assert.Equal(1, root.CrawlMode);
            Assert.False(await verify.SourceScanJobs.AnyAsync(job => job.SourceScanRequest.SourceRootId == root.Id &&
                job.SourceScanRequest.SourceRoot.State == (int)SourceRootState.Enabled));
            Assert.False(await verify.SourceScanJobs.AnyAsync(job => job.SourceScanRequest.SourceRootId == root.Id && job.LeaseExpiresAtUtc > clock.Now));
        }
        // Compatibility returns before resume; membership still uses Git mode.
        await Commit(commands, Mutation("root_resume", new { rootId = next.SourceRoot.Id.Value }));
        var resumed = Assert.IsType<ClaimedSourceScan>(await store.ClaimNextReleasedAsync("resumed", clock.Now, TimeSpan.FromMinutes(15), default));
        Assert.Equal(next.SourceRoot.Id, resumed.SourceRoot.Id);
        Assert.Equal(SourceDiscoveryMode.GitTracked, resumed.SourceRoot.DiscoveryMode);
        Assert.True(resumed.SourceRoot.ConfigurationRevision > next.SourceRoot.ConfigurationRevision);
    }

    private static NativeCorpusCommandService Commands(string path, IDbContextFactory<FluxKnowledgeDbContext> factory, Clock clock) => new(
        new NativeOperationService(new SqlNativeOperationStore(factory, clock), []),
        new SqlNativeCorpusActionStore(factory, new SourceRootPathPolicy(new LocalIngressOptions([path])), new LocalPrivateContentDisclosure()));
    private static NativeCorpusMutation Mutation(string action, object payload) => new(action, JsonSerializer.SerializeToElement(payload));
    private static async Task Commit(NativeCorpusCommandService service, NativeCorpusMutation mutation)
    {
        var preview = await service.PreviewAsync(mutation, "test", default);
        var key = $"coverage:{Guid.NewGuid():N}";
        var first = await service.CommitAsync(mutation, preview.ConfirmationId, key, "test", default);
        var replay = await service.CommitAsync(mutation, preview.ConfirmationId, key, "test", default);
        Assert.False(first.WasReplay); Assert.True(replay.WasReplay); Assert.Equal(first.OperationId, replay.OperationId);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan duration) => Now += duration;
    }
    private sealed class EvidenceCodec : ICorpusEvidenceCodec
    {
        private readonly Dictionary<string, CorpusEvidenceBinding> bindings = [];
        public string Encode(CorpusEvidenceBinding binding) { var id = Guid.NewGuid().ToString(); bindings[id] = binding; return id; }
        public CorpusEvidenceBinding Decode(string reference) => bindings[reference];
    }
    private sealed class UnusedDetailReader : ILocalRetainedDetailReader
    {
        public ValueTask<LocalRetainedDetailProjection?> ReadAsync(Guid branchId, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid branchId, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Repository : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FluxCoverageRepository_{Guid.NewGuid():N}");
        public Repository() { Directory.CreateDirectory(Path); Git("init", "--quiet"); }
        public void Write(string name, string text)
        {
            var full = System.IO.Path.Combine(Path, name); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!); File.WriteAllText(full, text);
        }
        public void Git(params string[] arguments)
        {
            var start = new ProcessStartInfo(@"C:\Program Files\Git\cmd\git.exe") { WorkingDirectory = Path, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var errors = process.StandardError.ReadToEnd(); process.WaitForExit(); Assert.True(process.ExitCode == 0, errors);
        }
        public void Dispose() { foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(Path, true); }
    }
}
