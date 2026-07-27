using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
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
        var active = await environment.ActiveGenerationAsync();
        var priorPath = active.IndexPath;

        await environment.AddAndPumpAsync("second document");
        var failing = new UsearchGenerationBuilder(
            environment.Store,
            new UsearchIndexOptions(environment.IndexRoot),
            new ThrowingValidator());

        await Assert.ThrowsAsync<IndexGenerationValidationException>(
            async () => await failing.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(priorPath, UsearchGenerationValidator.IndexFileName)));
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

        Assert.Equal("café\nline\n", canonical.SearchText);
        Assert.NotEmpty(chunks);
        Assert.NotEmpty(vectors);
        Assert.All(vectors, vector => Assert.True(vector.VectorId > 0));
        Assert.NotNull(active.ActiveIndexGenerationId);
        Assert.Equal(vectors.Select(vector => vector.VectorId).Order(), membership.Select(member => member.VectorId).Order());
        var generation = await context.IndexGenerations.SingleAsync(generation => generation.Id == active.ActiveIndexGenerationId);
        Assert.True(File.Exists(Path.Combine(generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
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

    private sealed class ThrowingValidator : UsearchGenerationValidator
    {
        public override void Validate(string directory, IndexGenerationDescriptor expected, IReadOnlyList<CanonicalVector> vectors) =>
            throw new IndexGenerationValidationException("injected candidate validation failure");
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
