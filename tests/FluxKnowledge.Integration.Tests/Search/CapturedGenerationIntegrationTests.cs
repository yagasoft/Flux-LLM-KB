using System.Data.Common;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

[Collection("sql-full-text")]
public sealed partial class CapturedGenerationIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task An_older_query_touch_during_metadata_preparation_is_retryable_warming_rather_than_permanent_capacity_failure()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "First immutable segment.", segmented: true);
        await environment.AddAndPumpAsync("Second immutable segment.");
        using var services = environment.Services.CreateScope();
        await using var ann = await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>()
            .OpenAsync(await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)), CancellationToken.None);
        var manifest = await services.ServiceProvider.GetRequiredService<ISegmentedIndexStore>().ReadManifestAsync(ann.Generation.Id, CancellationToken.None);
        var metadata = new InterleavedMetadata(ann.Generation.CorpusStamp!.CorpusEpoch, manifest.Segments[0]);
        await metadata.WarmOldAsync();
        var error = await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() => ann.PrepareAsync(metadata, CancellationToken.None).AsTask());
        Assert.Equal("warming", error.Status);
        await ann.PrepareAsync(metadata, CancellationToken.None);
        Assert.True(ann.IsPrepared(metadata));
    }

    [NativeSqlServerFact]
    public async Task A_cold_projection_refuses_serving_before_loading_immutable_metadata()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Cold semantic preparation must not consume a user search deadline.", segmented: true);
        await using (var db = await environment.Factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
        var commands = new NoVectorPayloads();
        var reader = new SqlCorpusRetrievalReader(new Factory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString).AddInterceptors(commands).Options));
        await using var captured = await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System));
        using var services = environment.Services.CreateScope();
        await using var ann = await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(captured, CancellationToken.None);
        var query = await environment.Embeddings.CreateEmbeddingAsync("preparation", CancellationToken.None);
        var error = await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() =>
            reader.ReadDenseCandidatesAsync(ann, new("all", [], null), query.Values, CancellationToken.None).AsTask());
        Assert.Equal("warming", error.Status);
        Assert.Equal(0, commands.StaticBindingReads);
        Assert.False(ann.IsPrepared(reader));
        await ann.PrepareAsync(reader, CancellationToken.None);
        Assert.True(ann.IsPrepared(reader));
        var reads = commands.StaticBindingReads;
        Assert.NotEmpty((await reader.ReadDenseCandidatesAsync(ann, new("all", [], null), query.Values, CancellationToken.None)).Candidates);
        Assert.Equal(reads, commands.StaticBindingReads);
    }

    [NativeSqlServerFact]
    public async Task Warm_binding_cache_obeys_the_real_source_deletion_tombstone_writer()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Independent current evidence.", segmented: true);
        var removed = await environment.AddRetainedAndPumpAsync("Withdrawn document must not survive in a warm metadata cache.");
        await using var context = await environment.Factory.CreateDbContextAsync();
        await context.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
        var revision = await (from record in context.PipelineRecords join source in context.SourceRevisions on record.SourceRevisionId equals source.Id
            where record.Id == removed select source).SingleAsync();
        var commands = new NoVectorPayloads();
        var reader = new SqlCorpusRetrievalReader(new Factory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString).AddInterceptors(commands).Options), new CorpusAnnBindingCache());
        var leases = new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System);
        using var services = environment.Services.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>();
        var query = await environment.Embeddings.CreateEmbeddingAsync("document evidence", CancellationToken.None);
        await using (var ann = await factory.OpenAsync(await Acquire(leases), CancellationToken.None))
        {
            await ann.PrepareAsync(reader, CancellationToken.None);
            Assert.Contains((await reader.ReadDenseCandidatesAsync(ann, new("all", [], null), query.Values, CancellationToken.None)).Candidates,
                candidate => candidate.PipelineRecordId == removed);
        }
        var loaded = commands.StaticBindingReads;
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, CancellationToken.None);
            await context.SourceRootConfigurations.Where(value => value.Id == revision.SourceRootId)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.State, 2));
            await SqlPublishedPassageSelection.AdvanceVersionAsync(context, DateTimeOffset.UtcNow, CancellationToken.None,
                [new(3, revision.SourceRootId)]);
            context.SourceDeletionOperations.Add(new()
            {
                Id = Guid.NewGuid(), SourceRootId = revision.SourceRootId, Phase = "accepted",
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync(); await transaction.CommitAsync();
        }
        var deletion = new SqlSourceDeletionStore(environment.Factory, TimeProvider.System, queryOwnerProbe: new WindowsInteractiveGpuOwnerProbe());
        var work = await deletion.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(work);
        var outcome = await deletion.PurgeAsync(work, null, CancellationToken.None);
        Assert.Equal("rebuild-index", outcome.Phase);
        Assert.True(await context.PipelineRecords.Where(value => value.Id == removed).Select(value => value.IsDeleted).SingleAsync());
        Assert.True(await (from vector in context.Vectors join chunk in context.TextChunks on vector.TextChunkId equals chunk.Id
            join artifact in context.Artifacts on chunk.ArtifactId equals artifact.Id
            where artifact.PipelineRecordId == removed select vector.IsDeleted).AllAsync(value => value));
        await using var next = await factory.OpenAsync(await Acquire(leases), CancellationToken.None);
        var after = await reader.ReadDenseCandidatesAsync(next, new("all", [], null), query.Values, CancellationToken.None);
        Assert.Equal("ready", after.Status);
        Assert.DoesNotContain(after.Candidates, candidate => candidate.PipelineRecordId == removed);
        Assert.Equal(loaded, commands.StaticBindingReads);
    }

    [NativeSqlServerFact]
    public async Task Complete_search_preserves_captured_evidence_while_unrelated_indexing_publishes()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Original published evidence remains eligible.", segmented: true);
        await using (var db = await environment.Factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
        var reader = new SqlCorpusRetrievalReader(environment.Factory);
        using (var warmServices = environment.Services.CreateScope())
        {
            await using var warm = await warmServices.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>()
                .OpenAsync(await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)), CancellationToken.None);
            await warm.PrepareAsync(reader, CancellationToken.None);
        }
        var models = new PausedReranker(environment.Embeddings);
        using var services = environment.Services.CreateScope();
        var codec = new CorpusEvidenceCodec(new EphemeralDataProtectionProvider());
        var engine = new HybridPassageRetrievalEngine(reader, reader,
            new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System),
            services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>(), models,
            new WindowsInteractiveGpuOwnerProbe(), codec, new LocalPrivateContentDisclosure());
        var search = engine.SearchAsync("evidence", new("all", [], null), 3, CancellationToken.None).AsTask();
        try
        {
            await models.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await environment.AddAndPumpAsync("New unrelated source publishes while reranking is in progress.");
        }
        finally { models.Release.TrySetResult(); }
        var result = await search;
        Assert.Equal("ready", result.SemanticStatus);
        Assert.Equal("hybrid", result.RetrievalMode);
        var hit = Assert.Single(result.Results);
        Assert.Equal("Original published evidence remains eligible.", hit.Passage);
        var context = await reader.ReadAsync(codec.Decode(hit.EvidenceRef), 0, CancellationToken.None);
        Assert.NotNull(context);
        Assert.Equal(hit.Passage, context.Text);
        await using var final = await environment.Factory.CreateDbContextAsync();
        Assert.NotEqual((await final.IndexState.SingleAsync()).ActiveIndexGenerationId, result.IndexGeneration);
        Assert.Empty(await final.CorpusQueryLeases.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Final_binding_validation_rejects_a_source_suppressed_after_initial_cited_read()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Independent baseline.", segmented: true);
        var record = await environment.AddRetainedAndPumpAsync("Revocable evidence from a physical document.");
        await using var db = await environment.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
        var sourceId = await db.PipelineRecords.Where(value => value.Id == record).Select(value => value.SourceRevisionId).SingleAsync();
        var reader = new SqlCorpusRetrievalReader(new Factory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString, sql => sql.EnableRetryOnFailure()).Options));
        await using var captured = await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System));
        using var services = environment.Services.CreateScope();
        await using var ann = await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(captured, CancellationToken.None);
        var query = await environment.Embeddings.CreateEmbeddingAsync("Revocable evidence", CancellationToken.None);
        await ann.PrepareAsync(reader, CancellationToken.None);
        var candidates = await reader.ReadDenseCandidatesAsync(ann, new("all", [], null), query.Values, CancellationToken.None);
        var passage = Assert.Single(candidates.Candidates, value => value.PipelineRecordId == record);
        var binding = new CorpusEvidenceBinding(2, passage.RootId, passage.OwnerSourceRevisionId,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(passage.SourceIdentity))),
            passage.PipelineRecordId, passage.PipelineRecordRevision, passage.ArtifactId, passage.ArtifactHash,
            passage.ChunkId, passage.ChunkHash, passage.StartOffset, passage.Length, passage.CorpusEpoch);
        Assert.NotNull(await reader.ReadAsync(binding, 0, CancellationToken.None));
        await using (var change = await db.Database.BeginTransactionAsync())
        {
            await SqlPublishedPassageSelection.AcquireFenceAsync(db, CancellationToken.None);
            await db.SourceRevisions.Where(value => value.Id == sourceId)
                .ExecuteUpdateAsync(set => set.SetProperty(value => value.SuppressedAtUtc, DateTimeOffset.UtcNow));
            await change.CommitAsync();
        }
        Assert.Empty(await reader.ValidateCurrentBindingsAsync([binding], CancellationToken.None));
        Assert.True(await ann.IsReadableAsync(CancellationToken.None));
    }

    [NativeSqlServerTheory]
    [InlineData("root")]
    [InlineData("workspace")]
    [InlineData("all")]
    public async Task Filtered_native_retrieval_uses_captured_membership_after_publication_without_selecting_SQL_vector_payloads(string kind)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Unrelated published baseline.", segmented: true);
        var recordId = await environment.AddRetainedAndPumpAsync("The eligible annual allowance is twenty days.");
        await using var context = await environment.Factory.CreateDbContextAsync();
        await context.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
        var revision = await (from record in context.PipelineRecords join source in context.SourceRevisions on record.SourceRevisionId equals source.Id
            where record.Id == recordId select source).SingleAsync();
        var captured = await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System));
        using var services = environment.Services.CreateScope();
        await using var ann = await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(captured, CancellationToken.None);
        await environment.AddAndPumpAsync("A later publication must not invalidate a pinned eligible neighbour.");
        var commands = new NoVectorPayloads();
        var reader = new SqlCorpusRetrievalReader(new Factory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString).AddInterceptors(commands).Options));
        var query = await environment.Embeddings.CreateEmbeddingAsync("holiday entitlement", CancellationToken.None);
        var scope = new ResolvedCorpusScope(kind, kind == "all" ? [] : [revision.SourceRootId],
            kind == "workspace" ? Path.GetDirectoryName(revision.CanonicalPath) : null);
        await ann.PrepareAsync(reader, CancellationToken.None);
        var result = await reader.ReadDenseCandidatesAsync(ann, scope, query.Values, CancellationToken.None);
        Assert.Equal("ready", result.Status);
        Assert.Contains(result.Candidates, value => value.PipelineRecordId == recordId);
        if (kind != "all") Assert.All(result.Candidates, value => Assert.Equal(revision.SourceRootId, value.RootId));
        var loaded = commands.StaticBindingReads;
        Assert.True(loaded > 0);
        var repeated = await reader.ReadDenseCandidatesAsync(ann, scope, query.Values, CancellationToken.None);
        Assert.Equal(result.Candidates.Select(value => value.ChunkId), repeated.Candidates.Select(value => value.ChunkId));
        Assert.Equal(loaded, commands.StaticBindingReads);
        Assert.Equal(2, commands.LiveBindingReads);
    }

    [NativeSqlServerFact]
    public async Task A_captured_generation_remains_readable_after_an_unrelated_publication_advances_the_active_projection()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Original published evidence remains eligible.", segmented: true);
        var leases = new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System);
        await using var captured = await Acquire(leases);
        await environment.AddAndPumpAsync("An unrelated source publishes a new immutable segment.");
        Assert.False(await captured.IsCurrentAsync(CancellationToken.None));
        Assert.True(await captured.IsReadableAsync(CancellationToken.None));
        await using var latest = await Acquire(leases);
        Assert.NotEqual(captured.Generation.Id, latest.Generation.Id);
    }

    [NativeSqlServerFact]
    public async Task A_readable_capture_uses_the_published_generation_stamp_while_new_publication_waits_for_projection()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Available published evidence.", segmented: true);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var state = await context.IndexState.SingleAsync();
        var generation = await context.IndexGenerations.SingleAsync(value => value.Id == state.ActiveIndexGenerationId);
        state.CorpusVersion++;
        await context.SaveChangesAsync();
        var leases = new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System);
        var owner = new WindowsInteractiveGpuOwnerProbe().Current;
        Assert.Null(await leases.TryAcquireAsync(Guid.NewGuid(), owner,
            DeterministicTokenHashEmbeddingProvider.Fingerprint, 256, CancellationToken.None));
        await using var captured = await Acquire(leases);
        Assert.Equal(generation.CorpusVersion, captured.Generation.CorpusStamp!.CorpusVersion);
        Assert.True(await captured.IsReadableAsync(CancellationToken.None));
        Assert.False(await captured.IsCurrentAsync(CancellationToken.None));
    }

    [NativeSqlServerTheory]
    [InlineData("epoch")]
    [InlineData("rebuild")]
    [InlineData("lease")]
    public async Task Epoch_replacement_rebuild_and_lost_durable_ownership_refuse_captured_reads(string transition)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Read ownership cannot survive a replacement epoch.", segmented: true);
        await using var captured = await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System));
        await using var context = await environment.Factory.CreateDbContextAsync();
        if (transition == "lease")
            await context.CorpusQueryLeases.Where(value => value.Id == captured.LeaseId).ExecuteDeleteAsync();
        else
        {
            var state = await context.IndexState.SingleAsync();
            if (transition == "epoch") state.CorpusEpoch = Guid.NewGuid();
            else
            {
                var operation = new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.CorpusRebuildOperationEntity
                {
                    Id = Guid.NewGuid(), TargetEpoch = Guid.NewGuid(), ManifestJson = "{}",
                    ManifestHash = new string('a', 64), CreatedAtUtc = DateTimeOffset.UtcNow
                };
                context.CorpusRebuildOperations.Add(operation);
                state.CorpusRebuildOperationId = operation.Id;
            }
            await context.SaveChangesAsync();
        }
        Assert.False(await captured.IsReadableAsync(CancellationToken.None));
    }

    private static async Task<ICorpusGenerationLease> Acquire(ICorpusGenerationLeaseStore leases) =>
        await leases.TryAcquireReadableAsync(Guid.NewGuid(), new WindowsInteractiveGpuOwnerProbe().Current,
            DeterministicTokenHashEmbeddingProvider.Fingerprint, 256, CancellationToken.None)
        ?? throw new InvalidOperationException("Expected a readable published generation.");

    private sealed class Factory(DbContextOptions<FluxKnowledgeDbContext> options) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }
    private sealed class InterleavedMetadata(Guid epoch, IndexSegmentDescriptor first) : ICorpusAnnBindingPreparation
    {
        private readonly CorpusAnnBindingCache _cache = new(new TickClock(), 1024);
        private readonly IndexSegmentDescriptor _old = first with { Id = new string('z', 64) };
        private bool _touchOld = true;
        public long MaximumRetainedBytes => 1024;
        private static ValueTask<SegmentBindingGroups> Load(CancellationToken _) => ValueTask.FromResult(
            new SegmentBindingGroups(new Dictionary<CanonicalBindingKey, long[]>(), new HashSet<CanonicalBindingKey>(), 512));
        public async Task WarmOldAsync() => _ = await _cache.GetAsync(epoch, _old, Load, CancellationToken.None);
        public bool IsPrepared(Guid requestedEpoch, IndexSegmentDescriptor segment) => requestedEpoch == epoch && _cache.TryGet(epoch, segment) is not null;
        public async ValueTask<long> PrepareAsync(ICorpusGenerationLease lease, IndexSegmentDescriptor segment, CancellationToken ct)
        {
            await _cache.GetAsync(epoch, segment, Load, ct);
            if (_touchOld && segment == first) { _touchOld = false; Assert.NotNull(_cache.TryGet(epoch, _old)); }
            return 512;
        }
    }
    private sealed class TickClock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Increment(ref _ticks));
    }
    private sealed class NoVectorPayloads : DbCommandInterceptor
    {
        public int StaticBindingReads { get; private set; }
        public int LiveBindingReads { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Assert.DoesNotContain("[Values]", command.CommandText, StringComparison.Ordinal);
            if (command.CommandText.Contains("corpus-ann-static-bindings", StringComparison.Ordinal)) StaticBindingReads++;
            if (command.CommandText.Contains("corpus-ann-live-bindings", StringComparison.Ordinal)) LiveBindingReads++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PausedReranker(IEmbeddingProvider embedding) : IScheduledPassageInference, IPassageReranker
    {
        public EmbeddingProfile EmbeddingProfile => new(DeterministicTokenHashEmbeddingProvider.Fingerprint, 256);
        public string RerankerFingerprint => "test-reranker";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
            CancellationToken token) => work(embedding, this, token);
        public async ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new(passages.Select((passage, index) => new RerankScore(passage.PassageId, -index)).ToArray(), RerankerFingerprint);
        }
    }
}
