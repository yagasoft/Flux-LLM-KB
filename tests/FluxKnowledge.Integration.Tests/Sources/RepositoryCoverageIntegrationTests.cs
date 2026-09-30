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
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
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

        // Git checkout and atomic editor saves can replace a physical file without changing its bytes.
        string retainedBefore, workBefore;
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            retainedBefore = await RetainedSnapshot(db, rootId);
            workBefore = await WorkSnapshot(db, rootId);
        }
        repository.Replace("Code.cs", File.ReadAllText(System.IO.Path.Combine(repository.Path, "Code.cs")));
        repository.Replace("Guide.md", File.ReadAllText(System.IO.Path.Combine(repository.Path, "Guide.md")));
        await Commit(service, Mutation("source_sync", new { rootId }));
        await ScanAndPublish(environment, scanStore, clock);
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            Assert.Equal(retainedBefore, await RetainedSnapshot(db, rootId));
            Assert.Equal(workBefore, await WorkSnapshot(db, rootId));
            Assert.Equal(codeBranchId, Assert.Single(await db.SourceProcessorCodeDocuments.Where(value => db.SourceRevisions.Any(r => r.Id == value.SourceRevisionId && r.SourceRootId == rootId)).ToListAsync()).SourceProcessorBranchId);
        }
        Assert.Contains("needle quartz is retained with citations", (await retrieval.ReadAsync(new CorpusReadRequest(original.EvidenceRef, 0), default)).Text);

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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Git_recreated_historical_bytes_restore_the_owner_without_replaying_completed_or_failed_work(bool failed)
    {
        // Published C# receipts in this class's existing database are immutable. Use a fresh disposable catalogue.
        var isolated = new NativeSqlServerFixture();
        await isolated.InitializeAsync();
        try
        {
            var clock = new Clock();
            await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(isolated, "Baseline", clock: clock);
            using var repository = new Repository(); repository.Write("Guide.md", "Original retained document."); repository.Git("add", ".");
            var commands = Commands(repository.Path, environment.Factory, clock);
            await Commit(commands, Mutation("root_create", new { path = repository.Path, displayName = "Restoration", discoveryMode = "git-tracked", indexSourceText = true }));
            var store = new SqlSourceScanStore(environment.Factory, clock);
            var rootId = await ScanAndPublish(environment, store, clock);
            SourceRevisionEntity first;
            await using (var db = await environment.Factory.CreateDbContextAsync())
            {
                first = await db.SourceRevisions.AsNoTracking().SingleAsync(value => value.SourceRootId == rootId);
                if (failed)
                {
                    var record = await db.PipelineRecords.SingleAsync(value => value.SourceRevisionId == first.Id);
                    record.CompletionCriteriaMet = false;
                    var job = await db.Jobs.SingleAsync(value => value.PipelineRecordId == record.Id && value.Stage == (int)PipelineStage.Publish);
                    job.PublicState = (int)PublicJobState.Failed; job.Reason = "Disposable failed-owner fixture";
                    await db.SaveChangesAsync();
                }
            }
            repository.Replace("Guide.md", "Changed retained document.");
            await Commit(commands, Mutation("source_sync", new { rootId })); await ScanAndPublish(environment, store, clock);
            string workBefore;
            await using (var db = await environment.Factory.CreateDbContextAsync())
            {
                Assert.NotNull((await db.SourceRevisions.FindAsync(first.Id))!.SuppressedAtUtc);
                workBefore = await WorkSnapshot(db, rootId);
            }
            repository.Replace("Guide.md", "Original retained document.");
            await Commit(commands, Mutation("source_sync", new { rootId }));
            var claim = Assert.IsType<ClaimedSourceScan>(await store.ClaimNextReleasedAsync("restoration", clock.Now, TimeSpan.FromMinutes(10), default));
            var enumerator = new LocalSourceEnumerator(); var files = new List<SourceDiscoveredFile>();
            await foreach (var value in enumerator.EnumerateAsync(claim.SourceRoot, default)) files.Add(value);
            var file = Assert.Single(files) with { ScanOwnership = new(claim.ScanRequest.Id, claim.ScanRequest.Lease!) };
            Assert.NotEqual(first.StableSourceIdentity, file.StableSourceIdentity);
            using var artifacts = new ContentAddressedSourceArtifactStore(environment.ArtifactRoot);
            var receipt = await artifacts.PutFileAsync(file, new(file.ContentSha256, "application/octet-stream", file.ByteLength), default);
            async Task<SourceRevisionId> Converge() => await store.ConvergeRevisionAndArtifactAsync(claim.SourceRoot, file, receipt, default);
            var concurrent = await Task.WhenAll(Converge(), Converge());
            Assert.All(concurrent, value => Assert.Equal(first.Id, value.Value));
            var worker = new SourceScanWorker(new LocalSourceEnumerator(), store, artifacts, new SqlSourceActivityStore(environment.Factory, clock),
                new RetainedTextActivityPlanner(new SqlRetainedTextRegistrationStore(environment.Factory, clock)));
            var result = await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, default);
            await store.CompleteAsync(claim, result, null, default);
            await using var verify = await environment.Factory.CreateDbContextAsync();
            var current = Assert.Single(await verify.SourceRevisions.Where(value => value.SourceRootId == rootId && value.SuppressedAtUtc == null).ToListAsync());
            Assert.Equal(first.Id, current.Id); Assert.Equal(first.StableSourceIdentity, current.StableSourceIdentity);
            Assert.Equal(first.DiscoveryEvidenceJson, current.DiscoveryEvidenceJson);
            Assert.Equal(2, await verify.SourceRevisions.CountAsync(value => value.SourceRootId == rootId));
            Assert.Equal(workBefore, await WorkSnapshot(verify, rootId));
        }
        finally { await isolated.DisposeAsync(); }
    }

    [NativeSqlServerTheory]
    [InlineData("foreign-repository")]
    [InlineData("missing-inventory")]
    [InlineData("malformed-json")]
    [InlineData("invalid-generation")]
    [InlineData("null-generation")]
    [InlineData("non-file-origin")]
    [InlineData("wrong-length")]
    [InlineData("artifact-mismatch")]
    [InlineData("paused")]
    [InlineData("stale-config")]
    [InlineData("expired-lease")]
    [InlineData("foreign-lease")]
    public async Task Git_replacement_cannot_reuse_an_owner_without_retained_provenance_and_current_authority(string mismatch)
    {
        using var repository = new Repository(); repository.Write("Guide.md", "Retained bytes."); repository.Git("add", ".");
        var factory = SqlTestData.CreateFactory(fixture); var clock = new Clock();
        await Commit(Commands(repository.Path, factory, clock), Mutation("root_create", new { path = repository.Path, displayName = "Replacement refusal", discoveryMode = "git-tracked", indexSourceText = true }));
        var store = new SqlSourceScanStore(factory, clock);
        var claim = Assert.IsType<ClaimedSourceScan>(await store.ClaimNextReleasedAsync("replacement-refusal", clock.Now, TimeSpan.FromMinutes(10), default));
        var enumerator = new LocalSourceEnumerator(); var files = new List<SourceDiscoveredFile>();
        await foreach (var value in enumerator.EnumerateAsync(claim.SourceRoot, default)) files.Add(value);
        var file = Assert.Single(files) with { ScanOwnership = new(claim.ScanRequest.Id, claim.ScanRequest.Lease!) };
        var inventory = file.GitInventory!;
        var evidence = mismatch switch
        {
            "foreign-repository" => JsonSerializer.Serialize(new { gitInventory = inventory with { RepositoryIdentity = new string('f', 64) } }),
            "missing-inventory" => "{}", "malformed-json" => "{",
            "invalid-generation" => JsonSerializer.Serialize(new { gitInventory = inventory with { Generation = "invalid" } }),
            "null-generation" => JsonSerializer.Serialize(new { gitInventory = inventory with { Generation = (string)null! } }),
            _ => JsonSerializer.Serialize(new { gitInventory = inventory })
        };
        var owner = new SourceRevisionEntity { Id = Guid.NewGuid(), SourceRootId = claim.SourceRoot.Id.Value,
            StableSourceIdentity = "retained-previous-physical-file", Revision = 1, ContentSha256 = file.ContentSha256,
            CanonicalPath = file.CanonicalPath, Classification = file.Classification.Classification.ToString(), Extension = ".md",
            OriginKind = mismatch == "non-file-origin" ? 2 : 0, ByteLength = file.ByteLength + (mismatch == "wrong-length" ? 1 : 0),
            DiscoveredAtUtc = clock.Now, DiscoveryEvidenceJson = evidence, SuppressedAtUtc = clock.Now };
        var receipt = new SourceArtifactReceipt(SourceArtifactId.New(), file.ContentSha256, "sha256/retained.bin", file.ByteLength, false);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.SourceRevisions.Add(owner);
            setup.SourceArtifacts.Add(new SourceArtifactEntity { Id = receipt.SourceArtifactId.Value, SourceRevisionId = owner.Id,
                ContentSha256 = file.ContentSha256, StoreRelativePath = mismatch == "artifact-mismatch" ? "sha256/different.bin" : receipt.StoreRelativePath,
                ByteLength = file.ByteLength, ChecksumVerifiedAtUtc = clock.Now, ReferenceCount = 1 });
            var root = (await setup.SourceRootConfigurations.FindAsync(claim.SourceRoot.Id.Value))!;
            if (mismatch == "paused") root.State = (int)SourceRootState.Paused;
            if (mismatch == "stale-config") root.ConfigurationRevision++;
            await setup.SaveChangesAsync();
        }
        if (mismatch == "expired-lease") clock.Advance(TimeSpan.FromMinutes(11));
        if (mismatch == "foreign-lease") file = file with { ScanOwnership = new(SourceScanRequestId.New(), claim.ScanRequest.Lease!) };
        if (mismatch is "paused" or "stale-config" or "expired-lease" or "foreign-lease")
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ConvergeRevisionAndArtifactAsync(claim.SourceRoot, file, receipt, default).AsTask());
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConvergeRevisionAndArtifactAsync(claim.SourceRoot, file, receipt, default).AsTask());
        await using var verify = await factory.CreateDbContextAsync();
        var retained = Assert.Single(await verify.SourceRevisions.Where(value => value.SourceRootId == claim.SourceRoot.Id.Value).ToListAsync());
        Assert.NotNull(retained.SuppressedAtUtc); Assert.Equal(owner.DiscoveryEvidenceJson, retained.DiscoveryEvidenceJson);
        Assert.Equal(owner.StableSourceIdentity, retained.StableSourceIdentity);
        Assert.Empty(await verify.SourceActivities.Where(value => value.SourceRevisionId == owner.Id).ToListAsync());
    }

    private static async Task<string> RetainedSnapshot(FluxKnowledgeDbContext db, Guid rootId) => JsonSerializer.Serialize(
        await db.SourceRevisions.Where(value => value.SourceRootId == rootId).OrderBy(value => value.Id)
            .Select(value => new { value.Id, value.StableSourceIdentity, value.ContentSha256, value.DiscoveryEvidenceJson, value.SuppressedAtUtc }).ToListAsync());

    private static async Task<string> WorkSnapshot(FluxKnowledgeDbContext db, Guid rootId)
    {
        var recordIds = db.PipelineRecords.Where(record => db.SourceRevisions.Any(revision => revision.Id == record.SourceRevisionId && revision.SourceRootId == rootId)).Select(value => value.Id);
        return JsonSerializer.Serialize(new {
            Records = await db.PipelineRecords.Where(value => recordIds.Contains(value.Id)).OrderBy(value => value.Id).Select(value => new { value.Id, value.RowVersion }).ToListAsync(),
            Activities = await db.SourceActivities.Where(value => db.SourceRevisions.Any(revision => revision.Id == value.SourceRevisionId && revision.SourceRootId == rootId)).OrderBy(value => value.Id).Select(value => new { value.Id, value.RowVersion }).ToListAsync(),
            Jobs = await db.Jobs.Where(value => recordIds.Contains(value.PipelineRecordId)).OrderBy(value => value.Id).Select(value => new { value.Id, value.RowVersion }).ToListAsync(),
            Outbox = await db.OutboxMessages.Where(value => recordIds.Contains(value.PipelineRecordId)).OrderBy(value => value.Id).Select(value => new { value.Id, value.RowVersion }).ToListAsync()
        });
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
        public void Replace(string name, string text)
        {
            var full = System.IO.Path.Combine(Path, name); var replacement = full + ".replacement";
            File.WriteAllText(replacement, text); File.Move(replacement, full, overwrite: true);
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
