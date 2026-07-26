using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Usearch;
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
}
