using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Application.Ports;

public sealed record CanonicalTextChunk(
    long Id,
    int Ordinal,
    int StartOffset,
    int Length,
    string Content,
    string ContentHash,
    string PassagePolicyFingerprint = "",
    string ContextHeader = "")
{
    public string SearchText => ContextHeader.Length == 0 ? Content : ContextHeader + "\n" + Content;
    public string SearchInputHash => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(SearchText)));
}

public sealed record CanonicalVector(
    long VectorId,
    long TextChunkId,
    string ModelFingerprint,
    int Dimensions,
    byte[] Values,
    string TextChunkContentHash,
    string PayloadChecksum,
    long SourceRevision);

public sealed record CorpusPublicationStamp(Guid CorpusEpoch, long CorpusVersion);

public sealed record IndexPublicationSnapshot(
    IReadOnlyList<CanonicalVector> Vectors,
    CorpusPublicationStamp? ExpectedCorpusStamp = null,
    CorpusPublicationStamp? PublicationStamp = null);

public sealed record IndexGenerationDescriptor(
    Guid Id,
    string ModelFingerprint,
    int Dimensions,
    string IndexPath,
    string MetadataChecksum,
    long VectorCount,
    CorpusPublicationStamp? CorpusStamp = null);

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

    /// <summary>Preview the exact publication winner without exposing or committing pending work.</summary>
    ValueTask<IReadOnlyList<CanonicalVector>> ReadPublicationVectorsAsync(
        Guid draftGenerationId, CancellationToken cancellationToken) => ReadEligibleVectorsAsync(cancellationToken);

    async ValueTask<IndexPublicationSnapshot> ReadPublicationSnapshotAsync(
        Guid draftGenerationId, CancellationToken cancellationToken) =>
        new(await ReadPublicationVectorsAsync(draftGenerationId, cancellationToken));

    ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(
        Guid indexGenerationId,
        CancellationToken cancellationToken);

    ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken cancellationToken);

    ValueTask UpdateGenerationMetadataAsync(
        IndexGenerationDescriptor generation,
        CancellationToken cancellationToken);
}
