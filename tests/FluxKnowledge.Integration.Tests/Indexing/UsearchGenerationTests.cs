using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Usearch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class UsearchGenerationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIndexes_{Guid.NewGuid():N}");

    [Fact]
    public async Task Candidate_is_saved_reopened_validated_and_placed_as_an_immutable_generation()
    {
        var id = Guid.NewGuid();
        var store = new MemoryStore(id);
        var builder = new UsearchGenerationBuilder(store, UsearchIndexOptions.FromConfiguredRoot(_root), new UsearchGenerationValidator());

        var generation = await builder.BuildAndPlaceAsync(id, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
        Assert.Equal(2, generation.VectorCount);
        Assert.Equal(generation, await store.GetGenerationAsync(id, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Active_reader_reuses_a_matching_generation_and_replaces_it_after_the_sql_pointer_changes()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var store = new CachingStore(first, second);
        var options = UsearchIndexOptions.FromConfiguredRoot(_root);
        var builder = new UsearchGenerationBuilder(store, options, new UsearchGenerationValidator());
        await builder.BuildAndPlaceAsync(first, CancellationToken.None);
        await builder.BuildAndPlaceAsync(second, CancellationToken.None);
        store.ResetGenerationReads();
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddScoped<IIndexGenerationStore>(provider => provider.GetRequiredService<CachingStore>());
        services.AddSingleton<UsearchAnnIndex>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var reader = provider.GetRequiredService<UsearchAnnIndex>();

        var initial = await reader.SearchAsync(Vector(0), 1, CancellationToken.None);
        var repeated = await reader.SearchAsync(Vector(0), 1, CancellationToken.None);
        store.ActiveGeneration = second;
        var replaced = await reader.SearchAsync(Vector(1), 1, CancellationToken.None);

        Assert.Equal(11, Assert.Single(initial).VectorId);
        Assert.Equal(11, Assert.Single(repeated).VectorId);
        Assert.Equal(22, Assert.Single(replaced).VectorId);
        Assert.Equal(2, store.GenerationReads);
    }

    private sealed class MemoryStore(Guid id) : IIndexGenerationStore
    {
        private IndexGenerationDescriptor _generation = new(id, "deterministic-tokenhash-v1:256", 256, string.Empty, new string('0', 64), 2);
        private readonly IReadOnlyList<CanonicalVector> _vectors =
        [
            Vector(1, 1, 0),
            Vector(2, 2, 1)
        ];

        public ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(FluxKnowledge.Domain.Common.PipelineRecordId pipelineRecordId, long sourceRevision, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CanonicalTextChunk>>([]);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(Guid indexGenerationId, CancellationToken cancellationToken) => ValueTask.FromResult(_vectors);
        public ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(Guid indexGenerationId, CancellationToken cancellationToken) => ValueTask.FromResult<IndexGenerationDescriptor?>(_generation);
        public ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken cancellationToken) => ValueTask.FromResult<Guid?>(null);
        public ValueTask UpdateGenerationMetadataAsync(IndexGenerationDescriptor generation, CancellationToken cancellationToken) { _generation = generation; return ValueTask.CompletedTask; }

        private static CanonicalVector Vector(long id, long chunkId, int dimension)
        {
            var values = new float[256];
            values[dimension] = 1F;
            var bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return new CanonicalVector(id, chunkId, "deterministic-tokenhash-v1:256", 256, bytes, new string('a', 64), 1);
        }
    }

    private sealed class CachingStore : IIndexGenerationStore
    {
        private readonly Guid _first;
        private readonly Dictionary<Guid, IndexGenerationDescriptor> _generations;

        public CachingStore(Guid first, Guid second)
        {
            _first = first;
            ActiveGeneration = first;
            _generations = new Dictionary<Guid, IndexGenerationDescriptor>
            {
                [first] = new(first, "deterministic-tokenhash-v1:256", 256, string.Empty, new string('0', 64), 1),
                [second] = new(second, "deterministic-tokenhash-v1:256", 256, string.Empty, new string('0', 64), 1)
            };
        }

        public Guid ActiveGeneration { get; set; }
        public int GenerationReads { get; private set; }
        public void ResetGenerationReads() => GenerationReads = 0;
        public ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(FluxKnowledge.Domain.Common.PipelineRecordId pipelineRecordId, long sourceRevision, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CanonicalTextChunk>>([]);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(Guid indexGenerationId, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CanonicalVector>>([Vector(indexGenerationId == _first ? 11 : 22, indexGenerationId == _first ? 0 : 1)]);
        public ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(Guid indexGenerationId, CancellationToken cancellationToken) { GenerationReads++; return ValueTask.FromResult<IndexGenerationDescriptor?>(_generations[indexGenerationId]); }
        public ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken cancellationToken) => ValueTask.FromResult<Guid?>(ActiveGeneration);
        public ValueTask UpdateGenerationMetadataAsync(IndexGenerationDescriptor generation, CancellationToken cancellationToken) { _generations[generation.Id] = generation; return ValueTask.CompletedTask; }
        private static CanonicalVector Vector(long id, int dimension) { var values = UsearchGenerationTests.Vector(dimension).ToArray(); var bytes = new byte[values.Length * sizeof(float)]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return new CanonicalVector(id, id, "deterministic-tokenhash-v1:256", 256, bytes, new string('a', 64), 1); }
    }

    private static IReadOnlyList<float> Vector(int dimension)
    {
        var values = new float[256];
        values[dimension] = 1F;
        return values;
    }
}
