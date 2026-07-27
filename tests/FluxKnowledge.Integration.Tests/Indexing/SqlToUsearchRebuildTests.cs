using System.Security.Cryptography;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Infrastructure.Usearch.Search;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class SqlToUsearchRebuildTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Rebuild_after_index_root_deletion_uses_sql_membership_and_keeps_the_active_generation_searchable()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first document");
        var active = await environment.ActiveGenerationAsync();
        var query = await environment.Embeddings.CreateEmbeddingAsync("first", CancellationToken.None);

        Directory.Delete(environment.IndexRoot, recursive: true);
        var rebuilt = await environment.Builder.RebuildFromSqlAsync(active.Id, CancellationToken.None);
        var pointer = await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None);
        var matches = await environment.Reader.SearchAsync(query.Values, 5, CancellationToken.None);

        Assert.Equal(active.Id, rebuilt.Id);
        Assert.Equal(active.Id, pointer);
        Assert.True(File.Exists(Path.Combine(rebuilt.IndexPath, UsearchGenerationValidator.IndexFileName)));
        Assert.NotEmpty(matches);
        Assert.Equal(active.VectorCount, rebuilt.VectorCount);
    }

    [NativeSqlServerFact]
    public async Task Candidate_validation_failure_preserves_the_prior_active_pointer_and_immutable_directory()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first document");
        await environment.AddAndPumpAsync("second document");
        var active = await environment.ActiveGenerationAsync();
        var activePath = active.IndexPath;
        var failing = new UsearchGenerationBuilder(
            environment.Store,
            new UsearchIndexOptions(environment.IndexRoot),
            new ThrowingValidator());

        await Assert.ThrowsAsync<IndexGenerationValidationException>(
            async () => await failing.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(activePath, UsearchGenerationValidator.IndexFileName)));
    }

    [NativeSqlServerFact]
    public async Task Hosted_pipeline_persists_canonical_chunks_stable_vectors_membership_and_active_generation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "cafe\u0301\r\nline\r");
        var receipt = environment.LastReceipt;
        Assert.NotNull(receipt);
        await using var context = await environment.Factory.CreateDbContextAsync();

        var canonical = await context.Artifacts.SingleAsync(artifact =>
            artifact.PipelineRecordId == receipt!.PipelineRecordId.Value &&
            artifact.Stage == (int)FluxKnowledge.Domain.Pipeline.PipelineStage.CanonicalIndex);
        var chunks = await context.TextChunks.Where(chunk => chunk.ArtifactId == canonical.Id).OrderBy(chunk => chunk.Ordinal).ToListAsync();
        var vectors = await context.Vectors.OrderBy(vector => vector.VectorId).ToListAsync();
        var active = await context.IndexState.SingleAsync(state => state.Id == 1);
        var membership = await context.IndexGenerationVectors.Where(member => member.GenerationId == active.ActiveIndexGenerationId).ToListAsync();
        var record = await context.PipelineRecords.SingleAsync(candidate =>
            candidate.Id == receipt.PipelineRecordId.Value);

        Assert.Equal("café\nline\n", canonical.SearchText);
        Assert.NotEmpty(chunks);
        Assert.NotEmpty(vectors);
        Assert.All(vectors, vector => Assert.True(vector.VectorId > 0));
        Assert.NotNull(active.ActiveIndexGenerationId);
        Assert.Equal((int)PipelineStage.Publish, record.CurrentStage);
        Assert.True(record.CompletionCriteriaMet);
        Assert.Equal(vectors.Select(vector => vector.VectorId).Order(), membership.Select(member => member.VectorId).Order());
        var generation = await context.IndexGenerations.SingleAsync(generation => generation.Id == active.ActiveIndexGenerationId);
        Assert.True(File.Exists(Path.Combine(generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
    }

    [NativeSqlServerFact]
    public async Task Worker_produced_vector_round_trips_through_hybrid_search_and_preserves_stale_chunk_protection()
    {
        const string sourceText = "restart the native worker safely";
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, sourceText);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var vector = await context.Vectors.SingleAsync();
        var chunk = await context.TextChunks.SingleAsync(candidate => candidate.Id == vector.TextChunkId);

        Assert.Equal(chunk.ContentHash, vector.TextChunkContentHash);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(vector.Values)),
            vector.PayloadChecksum);
        Assert.NotEqual(vector.TextChunkContentHash, vector.PayloadChecksum);

        var lexical = new SqlFullTextSearch(environment.Factory);
        IReadOnlyList<RankedCandidate> lexicalCandidates = [];
        var fullTextDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (lexicalCandidates.Count == 0 && DateTimeOffset.UtcNow < fullTextDeadline)
        {
            lexicalCandidates = await lexical.SearchAsync("restart", 5, CancellationToken.None);
            if (lexicalCandidates.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }

        Assert.Contains(lexicalCandidates, candidate => candidate.VectorId == vector.VectorId);

        var search = new HybridSearchService(
            lexical,
            new UsearchNearestNeighbourQuery(environment.Embeddings, environment.Reader),
            new SqlSearchHydrator(environment.Factory),
            environment.Store);
        var response = await search.SearchAsync(
            new SearchRequest("restart", 5, "local_first", null, null, null),
            CancellationToken.None);
        var hit = Assert.Single(response.Results);
        Assert.Contains(sourceText, hit.Snippet, StringComparison.Ordinal);
        Assert.Contains(hit.Explanation, item => item.StartsWith("lexical:", StringComparison.Ordinal));
        Assert.Contains(hit.Explanation, item => item.StartsWith("semantic:", StringComparison.Ordinal));

        vector.TextChunkContentHash = new string('f', 64);
        await context.SaveChangesAsync();

        var staleResponse = await search.SearchAsync(
            new SearchRequest("restart", 5, "local_first", null, null, null),
            CancellationToken.None);

        Assert.Empty(staleResponse.Results);
    }

    [NativeSqlServerFact]
    public async Task Second_corpus_publish_retains_vectors_from_two_independent_current_sources()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "alpha source");
        await environment.AddAndPumpAsync("bravo source");
        await using var context = await environment.Factory.CreateDbContextAsync();
        var activeId = (await context.IndexState.SingleAsync(state => state.Id == 1)).ActiveIndexGenerationId;
        var membership = await context.IndexGenerationVectors
            .Where(member => member.GenerationId == activeId)
            .Select(member => member.VectorId)
            .OrderBy(id => id)
            .ToListAsync();
        var allVectors = await context.Vectors.OrderBy(vector => vector.VectorId).Select(vector => vector.VectorId).ToListAsync();

        Assert.Equal(allVectors, membership);
        Assert.True(membership.Count >= 2);
    }

    [NativeSqlServerFact]
    public async Task Prebuilt_snapshot_is_superseded_by_a_newer_publish_without_pointer_regression()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        var stale = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        await environment.AddAndPumpAsync("second source");
        var active = await environment.ActiveGenerationAsync();
        var transition = new SqlStageTransitionStore(environment.Factory);
        var request = await ClaimPublishAsync(environment, stale);

        var result = await transition.TransitionAsync(
            request,
            CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var record = await context.PipelineRecords.SingleAsync(candidate =>
            candidate.Id == request.CurrentJob.PipelineRecordId.Value);

        Assert.False(result.ExistingTransition);
        Assert.True(record.CompletionCriteriaMet);
        Assert.NotEqual(stale.Generation.Id, active.Id);
        Assert.True(File.Exists(Path.Combine(stale.Generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Completed_publish_replay_does_not_duplicate_membership_or_replace_a_valid_placement()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "replay source");
        var active = await environment.ActiveGenerationAsync();
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var transition = new SqlStageTransitionStore(environment.Factory);
        var request = await ClaimPublishAsync(environment, candidate);
        var first = await transition.TransitionAsync(request, CancellationToken.None);
        var replay = await transition.TransitionAsync(request, CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var members = await context.IndexGenerationVectors.Where(member => member.GenerationId == active.Id).ToListAsync();
        var record = await context.PipelineRecords.SingleAsync(candidate =>
            candidate.Id == request.CurrentJob.PipelineRecordId.Value);

        Assert.False(first.ExistingTransition);
        Assert.True(replay.ExistingTransition);
        Assert.True(record.CompletionCriteriaMet);
        Assert.Equal(first.ArtifactId, replay.ArtifactId);
        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.Equal(members.Select(member => member.VectorId).Distinct().Count(), members.Count);
        Assert.True(File.Exists(Path.Combine(candidate.Generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
    }

    [NativeSqlServerFact]
    public async Task Failed_terminal_publish_rolls_back_the_completion_flag()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "failed publish source");
        var active = await environment.ActiveGenerationAsync();
        var vectors = await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None);
        var incompatible = active with { IndexPath = active.IndexPath + "-incompatible" };
        var request = await ClaimPublishAsync(
            environment,
            new IndexGenerationCandidateSnapshot(incompatible, vectors));

        await Assert.ThrowsAsync<IndexGenerationStaleException>(
            async () => await new SqlStageTransitionStore(environment.Factory)
                .TransitionAsync(request, CancellationToken.None));

        await using var context = await environment.Factory.CreateDbContextAsync();
        var record = await context.PipelineRecords.SingleAsync(candidate =>
            candidate.Id == request.CurrentJob.PipelineRecordId.Value);

        Assert.False(record.CompletionCriteriaMet);
        Assert.DoesNotContain(
            await context.Artifacts.ToListAsync(),
            artifact => artifact.Id == request.Artifact.Id);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_same_candidate_activation_creates_one_generation_and_membership_snapshot()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "concurrent source");
        await PrepareUntrackedCandidateAsync(environment);
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var barrier = new ActivationBarrier();
        var first = new SqlStageTransitionStore(environment.Factory, barrier);
        var second = new SqlStageTransitionStore(environment.Factory, barrier);
        var firstRequest = await ClaimPublishAsync(environment, candidate);
        var secondRequest = await ClaimPublishAsync(environment, candidate);

        var firstTransition = first.TransitionAsync(firstRequest, CancellationToken.None).AsTask();
        var secondTransition = second.TransitionAsync(secondRequest, CancellationToken.None).AsTask();
        await barrier.BothArtifactsWritten.Task.WaitAsync(TimeSpan.FromSeconds(10));
        barrier.Release();
        var transitions = await Task.WhenAll(firstTransition, secondTransition);

        Assert.All(transitions, transition => Assert.False(transition.ExistingTransition));
        Assert.Equal(candidate.Generation.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.Single(await context.IndexGenerations.Where(generation => generation.Id == candidate.Generation.Id).ToListAsync());
        Assert.Equal(candidate.Vectors.Count, await context.IndexGenerationVectors.CountAsync(
            membership => membership.GenerationId == candidate.Generation.Id));
    }

    [NativeSqlServerFact]
    public async Task Existing_generation_with_empty_membership_is_repaired_idempotently()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "empty membership source");
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            await context.IndexGenerationVectors
                .Where(membership => membership.GenerationId == candidate.Generation.Id)
                .ExecuteDeleteAsync();
        }

        await new SqlStageTransitionStore(environment.Factory).TransitionAsync(
            await ClaimPublishAsync(environment, candidate),
            CancellationToken.None);

        await using var verification = await environment.Factory.CreateDbContextAsync();
        Assert.Single(await verification.IndexGenerations.Where(generation => generation.Id == candidate.Generation.Id).ToListAsync());
        Assert.Equal(candidate.Vectors.Count, await verification.IndexGenerationVectors.CountAsync(
            membership => membership.GenerationId == candidate.Generation.Id));
    }

    private static async Task PrepareUntrackedCandidateAsync(PipelineEnvironment environment)
    {
        var active = await environment.ActiveGenerationAsync();
        await using var context = await environment.Factory.CreateDbContextAsync();
        var origin = new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.IndexGenerationEntity
        {
            Id = Guid.NewGuid(),
            ModelFingerprint = active.ModelFingerprint,
            Dimensions = active.Dimensions,
            IndexPath = active.IndexPath,
            MetadataChecksum = active.MetadataChecksum,
            VectorCount = active.VectorCount,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ValidatedAtUtc = DateTimeOffset.UtcNow
        };
        context.IndexGenerations.Add(origin);
        await context.SaveChangesAsync();
        var vectors = await context.Vectors.ToListAsync();
        foreach (var vector in vectors)
        {
            vector.IndexGenerationId = origin.Id;
        }
        var state = await context.IndexState.SingleAsync(candidate => candidate.Id == 1);
        state.ActiveIndexGenerationId = null;
        await context.SaveChangesAsync();
        await context.IndexGenerationVectors
            .Where(membership => membership.GenerationId == active.Id)
            .ExecuteDeleteAsync();
        await context.IndexGenerations
            .Where(generation => generation.Id == active.Id)
            .ExecuteDeleteAsync();
    }

    private async Task<StageTransitionRequest> ClaimPublishAsync(
        PipelineEnvironment environment,
        IndexGenerationCandidateSnapshot candidate)
    {
        var now = DateTimeOffset.UtcNow;
        await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null,
            stage: PipelineStage.Publish,
            operation: PipelineOperations.Publish);
        var outbox = await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync(
            "task-5-publish-dispatcher",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(2),
            [PipelineOperations.Publish],
            CancellationToken.None);
        Assert.NotNull(outbox);
        var job = await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(
            outbox!,
            "task-5-publish-worker",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        Assert.NotNull(job);
        return new StageTransitionRequest(
            outbox!,
            job!,
            new StageArtifact(
                Guid.NewGuid(),
                PipelineStage.Publish,
                candidate.Generation.MetadataChecksum,
                "application/vnd.fluxknowledge.usearch-generation",
                candidate.Generation.Id.ToString("N"),
                now),
            null,
            null,
            nameof(SqlToUsearchRebuildTests),
            new IndexingStageOutput(
                ActivateGeneration: candidate.Generation,
                ActivateMembership: candidate.Vectors));
    }

    private sealed class ThrowingValidator : UsearchGenerationValidator
    {
        public override void Validate(string directory, IndexGenerationDescriptor expected, IReadOnlyList<CanonicalVector> vectors) =>
            throw new IndexGenerationValidationException("injected candidate validation failure");
    }

    private sealed class ActivationBarrier : IStageTransitionFailureInjector
    {
        private int _arrivals;
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BothArtifactsWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask AfterArtifactWrittenAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                BothArtifactsWritten.TrySetResult(true);
            }

            return new ValueTask(_release.Task.WaitAsync(cancellationToken));
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class PipelineEnvironment : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _ingressRoot;
        private PipelineEnvironment(ServiceProvider provider, string ingressRoot, string indexRoot, IDbContextFactory<FluxKnowledgeDbContext> factory)
        {
            _provider = provider; _ingressRoot = ingressRoot; IndexRoot = indexRoot; Factory = factory;
            Store = new SqlPipelineStore(factory); Builder = _provider.GetRequiredService<UsearchGenerationBuilder>();
            Reader = _provider.GetRequiredService<UsearchAnnIndex>(); Embeddings = _provider.GetRequiredService<IEmbeddingProvider>();
        }
        public string IndexRoot { get; }
        public IDbContextFactory<FluxKnowledgeDbContext> Factory { get; }
        public SqlPipelineStore Store { get; }
        public UsearchGenerationBuilder Builder { get; }
        public UsearchAnnIndex Reader { get; }
        public IEmbeddingProvider Embeddings { get; }
        public FluxKnowledge.Application.Contracts.RegisterUtf8FileResult? LastReceipt { get; private set; }

        public static async Task<PipelineEnvironment> CreateAsync(NativeSqlServerFixture fixture, string text)
        {
            await SqlTestData.ClearPipelineAsync(fixture);
            var ingress = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIngress_{Guid.NewGuid():N}");
            var index = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIndexes_{Guid.NewGuid():N}");
            Directory.CreateDirectory(ingress);
            var services = new ServiceCollection();
            services.AddSingleton(SqlTestData.CreateFactory(fixture));
            services.AddSingleton<IUtf8FileSourceReader>(new Utf8FileSourceReader(new LocalIngressOptions([ingress])));
            services.AddFluxKnowledgeOutboxWorkers();
            services.AddSingleton<IEmbeddingProvider, DeterministicTokenHashEmbeddingProvider>();
            services.AddFluxKnowledgeUsearch(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Usearch:RootPath"] = index }).Build());
            services.AddScoped<IStageWorker, CanonicalIndexStageWorker>();
            services.AddScoped<IStageWorker, EmbedStageWorker>();
            services.AddScoped<IStageWorker, PublishStageWorker>();
            var provider = services.BuildServiceProvider();
            var environment = new PipelineEnvironment(provider, ingress, index, provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>());
            await environment.AddAndPumpAsync(text);
            return environment;
        }

        public async Task AddAndPumpAsync(string text)
        {
            var path = Path.Combine(_ingressRoot, $"{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, text);
            using var scope = _provider.CreateScope();
            LastReceipt = await scope.ServiceProvider.GetRequiredService<RegisterUtf8FileHandler>().HandleAsync(new(path, "native-sql-test", null), CancellationToken.None);
            await _provider.GetRequiredService<OutboxPumpService>().PumpOnceAsync(CancellationToken.None);
        }

        public async Task<IndexGenerationDescriptor> ActiveGenerationAsync()
        {
            var id = await Store.GetActiveGenerationIdAsync(CancellationToken.None);
            Assert.NotNull(id);
            return (await Store.GetGenerationAsync(id!.Value, CancellationToken.None))!;
        }

        public ValueTask DisposeAsync()
        {
            _provider.Dispose();
            if (Directory.Exists(_ingressRoot)) Directory.Delete(_ingressRoot, true);
            if (Directory.Exists(IndexRoot)) Directory.Delete(IndexRoot, true);
            return ValueTask.CompletedTask;
        }
    }
}
