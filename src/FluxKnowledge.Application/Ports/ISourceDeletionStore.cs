namespace FluxKnowledge.Application.Ports;

/// <summary>Durable source-deletion work. SQL remains the authority for claiming and completing a deletion.</summary>
public interface ISourceDeletionStore
{
    ValueTask<SourceDeletionWorkItem?> ClaimNextAsync(CancellationToken cancellationToken);

    ValueTask<SourceDeletionRunResult> PurgeAsync(
        SourceDeletionWorkItem workItem,
        IndexGenerationCandidateSnapshot? survivorGeneration,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SourceDeletionFileTarget>> ReadPendingCleanupAsync(
        SourceDeletionWorkItem workItem,
        CancellationToken cancellationToken);

    ValueTask RecordCleanupResultAsync(
        SourceDeletionWorkItem workItem,
        Guid cleanupItemId,
        SourceDeletionFileResult result,
        CancellationToken cancellationToken);

    ValueTask FailAsync(SourceDeletionWorkItem workItem, string reasonCode, CancellationToken cancellationToken);
}

public sealed record SourceDeletionWorkItem(Guid OperationId, Guid SourceRootId, Guid LeaseId, string Phase);

public sealed record SourceDeletionRunResult(
    bool Completed,
    string Phase,
    string? ReasonCode = null,
    bool ContinueImmediately = false,
    bool RequiresIndexBuild = false);
