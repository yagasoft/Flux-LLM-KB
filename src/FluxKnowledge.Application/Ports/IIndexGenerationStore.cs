using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Application.Ports;

public sealed record CanonicalTextChunk(
    long Id,
    int Ordinal,
    int StartOffset,
    int Length,
    string Content,
    string ContentHash);

public sealed record CanonicalVector(
    long VectorId,
    long TextChunkId,
    string ModelFingerprint,
    int Dimensions,
    byte[] Values,
    string ContentHash,
    long SourceRevision);

public sealed record IndexGenerationDescriptor(
    Guid Id,
    string ModelFingerprint,
    int Dimensions,
    string IndexPath,
    string MetadataChecksum,
    long VectorCount);

public interface IIndexGenerationStore
{
    ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(
        PipelineRecordId pipelineRecordId,
        long sourceRevision,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CanonicalVector>> ReadEligibleVectorsAsync(
        CancellationToken cancellationToken);

    ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken);

    ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken cancellationToken);

    ValueTask UpdateGenerationMetadataAsync(
        IndexGenerationDescriptor generation,
        CancellationToken cancellationToken);
}
