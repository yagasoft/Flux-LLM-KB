namespace FluxKnowledge.Application.Ports;

/// <summary>Durable source-deletion work. SQL remains the authority for claiming and completing a deletion.</summary>
public interface ISourceDeletionStore
{
    const int FileCleanupBatchSize = 16;

    ValueTask<SourceDeletionWorkItem?> ClaimNextAsync(CancellationToken cancellationToken);

    ValueTask<SourceDeletionRunResult> PurgeAsync(
        SourceDeletionWorkItem workItem,
        IndexGenerationCandidateSnapshot? survivorGeneration,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SourceDeletionFileTarget>> ReadPendingCleanupAsync(
        SourceDeletionWorkItem workItem,
        CancellationToken cancellationToken,
        int maxTargets = 1);

    ValueTask<bool> RecordCleanupResultsAsync(
        SourceDeletionWorkItem workItem,
        IReadOnlyList<SourceDeletionFileOutcome> outcomes,
        CancellationToken cancellationToken);

    ValueTask RecordCleanupResultAsync(
        SourceDeletionWorkItem workItem,
        Guid cleanupItemId,
        SourceDeletionFileResult result,
        CancellationToken cancellationToken);

    ValueTask FailAsync(SourceDeletionWorkItem workItem, string reasonCode, CancellationToken cancellationToken);

    ValueTask DeferAsync(SourceDeletionWorkItem workItem, string reasonCode, CancellationToken cancellationToken);
}

public sealed record SourceDeletionWorkItem(Guid OperationId, Guid SourceRootId, Guid LeaseId, string Phase)
{
    public DateTimeOffset? LeaseExpiresAtUtc { get; init; }
}

public sealed record SourceDeletionRunResult(
    bool Completed,
    string Phase,
    string? ReasonCode = null,
    bool ContinueImmediately = false,
    bool RequiresIndexBuild = false);
