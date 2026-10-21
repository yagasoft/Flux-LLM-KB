using FluxKnowledge.Application.Indexing;

namespace FluxKnowledge.Application.Ports;

public sealed record IndexAffectedScope(int Kind, Guid Id);

public sealed record SegmentedIndexCandidate(Guid Id, CorpusPublicationStamp ExpectedStamp,
    CorpusPublicationStamp PublicationStamp, string ModelFingerprint, int Dimensions,
    IReadOnlyList<IndexSegmentDescriptor> ReusedSegments);

public sealed record IndexManifestDescriptor(IndexGenerationDescriptor Generation,
    IReadOnlyList<IndexSegmentDescriptor> Segments);

public sealed record SegmentedProjectionState(CorpusPublicationStamp Stamp, IndexGenerationDescriptor? Generation,
    bool MaintenanceActive, bool ValidatedEmptyCatalogue);

/// <summary>Metadata worklists and immutable shared membership; activation belongs to the stage transaction.</summary>
public interface ISegmentedIndexStore
{
    ValueTask<bool> IsRequiredAsync(CancellationToken cancellationToken);
    ValueTask<SegmentedIndexCandidate> PrepareAsync(Guid draftGenerationId, CancellationToken cancellationToken);
    IAsyncEnumerable<CanonicalVector> ReadBuildVectorsAsync(Guid candidateId, CancellationToken cancellationToken);
    ValueTask SealSegmentAsync(Guid candidateId, IndexSegmentDescriptor segment,
        IReadOnlyList<CanonicalVector> vectors, CancellationToken cancellationToken);
    ValueTask SealManifestAsync(Guid candidateId, IndexManifestDescriptor manifest, CancellationToken cancellationToken);
    ValueTask<IndexManifestDescriptor> ReadManifestAsync(Guid generationId, CancellationToken cancellationToken);
    ValueTask<SegmentedProjectionState> ReadProjectionAsync(CancellationToken cancellationToken);
    ValueTask<bool> TryActivateRefreshAsync(IndexGenerationCandidateSnapshot candidate, CancellationToken cancellationToken);
    ValueTask AbandonAsync(Guid candidateId, CancellationToken cancellationToken);
}
