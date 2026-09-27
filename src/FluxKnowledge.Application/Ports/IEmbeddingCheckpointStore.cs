using FluxKnowledge.Application.Workers;

namespace FluxKnowledge.Application.Ports;

public sealed record EmbeddingWorkBatch(
    Guid GenerationId, Guid CorpusEpoch, EmbeddingProfile Profile,
    IReadOnlyList<CanonicalTextChunk> Chunks, string? CompletedChecksum = null);

public interface IEmbeddingCheckpointStore
{
    ValueTask<EmbeddingWorkBatch> ReadNextAsync(StageWorkItem work, EmbeddingProfile profile, CancellationToken cancellationToken);
    ValueTask CommitAsync(StageWorkItem work, EmbeddingWorkBatch batch,
        IReadOnlyList<EmbeddingResult> results, CancellationToken cancellationToken);
}
