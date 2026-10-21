using FluxKnowledge.Application.Indexing;

namespace FluxKnowledge.Application.Ports;

public sealed record IndexProjectionRebuildPlan(Guid OperationId, int SourceFormat, int TargetFormat,
    CorpusPublicationStamp Stamp, Guid BaseGenerationId, string ModelFingerprint, int Dimensions);

public sealed record IndexProjectionRebuildProgress(SegmentedIndexCandidate Candidate,
    IndexGenerationDescriptor? SealedGeneration, bool AlreadyCommitted);

/// <summary>Explicit whole-projection conversion/repair. Ordinary publication never calls this contract.</summary>
public interface IIndexProjectionRebuildStore
{
    ValueTask<IndexProjectionRebuildPlan> ReadRebuildPlanAsync(Guid operationId, int targetFormat, CancellationToken cancellationToken);
    ValueTask<IndexProjectionRebuildProgress> PrepareRebuildAsync(IndexProjectionRebuildPlan plan, CancellationToken cancellationToken);
    ValueTask SealLegacyRebuildAsync(Guid operationId, IndexGenerationDescriptor generation, CancellationToken cancellationToken);
    ValueTask<IndexGenerationDescriptor> ActivateRebuildAsync(Guid operationId, CancellationToken cancellationToken);
    ValueTask AbandonRebuildAsync(Guid operationId, CancellationToken cancellationToken);
}
