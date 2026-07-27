using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch.Search;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed class HybridSearchIntegrationTests : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture;

    public HybridSearchIntegrationTests(NativeSqlServerFixture fixture) => _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Hybrid_search_hydrates_only_current_non_deleted_candidates_and_explains_contributions()
    {
        await using var environment = await SearchEnvironment.CreateAsync(_fixture);

        var response = await environment.Service.SearchAsync(
            new SearchRequest("restart", 5, "local_first", null, null, null),
            CancellationToken.None);

        var hit = Assert.Single(response.Results);
        Assert.Equal("C:/ingress/guide.txt", hit.SourceIdentity);
        Assert.Equal(2, hit.Revision);
        Assert.Contains("restart", hit.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(hit.Explanation, item => item.StartsWith("lexical:", StringComparison.Ordinal));
        Assert.Contains(hit.Explanation, item => item.StartsWith("semantic:", StringComparison.Ordinal));
        Assert.DoesNotContain(hit.Explanation, item => item.Contains("\\", StringComparison.Ordinal));
    }

    private sealed class SearchEnvironment : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private SearchEnvironment(ServiceProvider provider, IServiceScope scope)
        {
            _provider = provider;
            Scope = scope;
            Service = Scope.ServiceProvider.GetRequiredService<HybridSearchService>();
        }

        private IServiceScope Scope { get; }
        public HybridSearchService Service { get; }

        public static async Task<SearchEnvironment> CreateAsync(NativeSqlServerFixture fixture)
        {
            await SqlTestData.ClearPipelineAsync(fixture);
            var candidateIds = await SearchFixtureData.SeedAsync(fixture);
            var services = new ServiceCollection();
            services.AddSingleton(SqlTestData.CreateFactory(fixture));
            services.AddSingleton<IEmbeddingProvider, DeterministicTokenHashEmbeddingProvider>();
            services.AddScoped<ILexicalSearch, SqlFullTextSearch>();
            services.AddScoped<ISearchHydrator, SqlSearchHydrator>();
            services.AddScoped<ISemanticSearch, UsearchNearestNeighbourQuery>();
            services.AddSingleton<IAnnIndex>(new CandidateAnnIndex(candidateIds));
            services.AddScoped<HybridSearchService>();
            services.AddScoped<IIndexGenerationStore, EmptyIndexGenerationStore>();
            var provider = services.BuildServiceProvider();
            return new SearchEnvironment(provider, provider.CreateScope());
        }

        public ValueTask DisposeAsync()
        {
            Scope.Dispose();
            _provider.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CandidateAnnIndex : IAnnIndex
    {
        private readonly IReadOnlyList<long> _candidateIds;

        public CandidateAnnIndex(IReadOnlyList<long> candidateIds) => _candidateIds = candidateIds;

        public ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(
            IReadOnlyList<float> query,
            int limit,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AnnMatch>>(
                _candidateIds.Select(static (id, index) => new AnnMatch(id, index)).ToArray());
    }

    private sealed class EmptyIndexGenerationStore : IIndexGenerationStore
    {
        public ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(FluxKnowledge.Domain.Common.PipelineRecordId pipelineRecordId, long sourceRevision, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<CanonicalTextChunk>>([]);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(Guid indexGenerationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<CanonicalVector>>([]);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadEligibleVectorsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<CanonicalVector>>([]);
        public ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(Guid indexGenerationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IndexGenerationDescriptor?>(null);
        public ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken cancellationToken) => ValueTask.FromResult<Guid?>(null);
        public ValueTask UpdateGenerationMetadataAsync(IndexGenerationDescriptor generation, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private static class SearchFixtureData
    {
        private const string CurrentHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string OldHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string ChunkHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

        public static async Task<IReadOnlyList<long>> SeedAsync(NativeSqlServerFixture fixture)
        {
            await using var context = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
            var now = DateTimeOffset.UtcNow;
            var generationId = Guid.NewGuid();
            context.IndexGenerations.Add(new IndexGenerationEntity
            {
                Id = generationId,
                ModelFingerprint = "test",
                Dimensions = 1,
                IndexPath = "C:/test-index",
                MetadataChecksum = CurrentHash,
                VectorCount = 3,
                CreatedAtUtc = now,
                ValidatedAtUtc = now
            });
            var guideSource = new SourceIdentityEntity { Id = Guid.NewGuid(), SourceKind = "local file", StableKey = "C:/ingress/guide.txt", CreatedAtUtc = now };
            var guideOld = new PipelineRecordEntity { Id = Guid.NewGuid(), SourceIdentityId = guideSource.Id, Revision = 1, ContentHash = OldHash, RootLineageRecordId = Guid.Empty, RegisteredAtUtc = now };
            guideOld.RootLineageRecordId = guideOld.Id;
            var guideCurrent = new PipelineRecordEntity { Id = Guid.NewGuid(), SourceIdentityId = guideSource.Id, Revision = 2, ContentHash = CurrentHash, RootLineageRecordId = guideOld.Id, ParentRevisionRecordId = guideOld.Id, RegisteredAtUtc = now };
            var currentVector = AddVector(context, guideCurrent, "restart the service safely", generationId, now, isDeleted: false);

            var deletedSource = new SourceIdentityEntity { Id = Guid.NewGuid(), SourceKind = "local file", StableKey = "C:/ingress/deleted.txt", CreatedAtUtc = now };
            var deletedRecord = new PipelineRecordEntity { Id = Guid.NewGuid(), SourceIdentityId = deletedSource.Id, Revision = 1, ContentHash = CurrentHash, RootLineageRecordId = Guid.Empty, RegisteredAtUtc = now };
            deletedRecord.RootLineageRecordId = deletedRecord.Id;
            var deletedVector = AddVector(context, deletedRecord, "restart deleted", generationId, now, isDeleted: true);

            var staleSource = new SourceIdentityEntity { Id = Guid.NewGuid(), SourceKind = "local file", StableKey = "C:/ingress/stale.txt", CreatedAtUtc = now };
            var staleRecord = new PipelineRecordEntity { Id = Guid.NewGuid(), SourceIdentityId = staleSource.Id, Revision = 1, ContentHash = OldHash, RootLineageRecordId = Guid.Empty, RegisteredAtUtc = now };
            staleRecord.RootLineageRecordId = staleRecord.Id;
            var staleCurrent = new PipelineRecordEntity { Id = Guid.NewGuid(), SourceIdentityId = staleSource.Id, Revision = 2, ContentHash = CurrentHash, RootLineageRecordId = staleRecord.Id, ParentRevisionRecordId = staleRecord.Id, RegisteredAtUtc = now };
            var staleVector = AddVector(context, staleRecord, "restart stale", generationId, now, isDeleted: false);

            context.SourceIdentities.AddRange(guideSource, deletedSource, staleSource);
            context.PipelineRecords.AddRange(guideOld, guideCurrent, deletedRecord, staleRecord, staleCurrent);
            await context.SaveChangesAsync();
            return [currentVector.VectorId, deletedVector.VectorId, staleVector.VectorId, 999999L];
        }

        private static VectorEntity AddVector(
            FluxKnowledgeDbContext context,
            PipelineRecordEntity record,
            string text,
            Guid generationId,
            DateTimeOffset now,
            bool isDeleted)
        {
            var artifact = new ArtifactEntity { Id = Guid.NewGuid(), PipelineRecordId = record.Id, SourceRevision = record.Revision, Stage = 3, ContentHash = record.ContentHash, ContentType = "text/plain", SearchText = text, CreatedAtUtc = now };
            var chunk = new TextChunkEntity { ArtifactId = artifact.Id, SourceRevision = record.Revision, Ordinal = 0, StartOffset = 0, Length = text.Length, Content = text, ContentHash = ChunkHash };
            var vector = new VectorEntity { TextChunk = chunk, SourceRevision = record.Revision, ModelFingerprint = "test", Dimensions = 1, Values = [0, 0, 0, 0], ContentHash = ChunkHash, IsDeleted = isDeleted, IndexGenerationId = generationId, CreatedAtUtc = now };
            context.Artifacts.Add(artifact);
            context.TextChunks.Add(chunk);
            context.Vectors.Add(vector);
            return vector;
        }
    }
}
