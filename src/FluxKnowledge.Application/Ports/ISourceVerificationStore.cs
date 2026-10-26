using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

public sealed record SourceVerificationAuthority(int Version, long ConfigurationRevision, string RootIdentity,
    long BaselineEpoch, long RecoveryGeneration, long NamespaceGeneration, string PolicyFingerprint,
    string VolumeIdentity, string JournalId, bool CompleteNamespace);

public sealed record SourceVerificationBinding(Guid InventoryEntryId, string RelativePath, string FileId,
    Guid? RevisionId, Guid? ArtifactId, string? Sha256, long ByteLength, string? StoreRelativePath,
    SourceFileObservation? Source);

public sealed record SourceVerificationItem(Guid Id, SourceVerificationBinding Binding);
public sealed record SourceVerificationReceipt(SourceVerificationDigest Source, RetainedFileObservation Retained,
    DateTimeOffset RetainedHashedAtUtc);
public sealed record SourceVerificationBatch(Guid RunId, SourceRootConfiguration Root, SourceScanRequest Request,
    SourceVerificationAuthority Authority, SourceVerificationPolicy Policy, long PolicyRevision,
    DateTimeOffset WindowEndUtc, IReadOnlyList<SourceVerificationItem> Items,
    IReadOnlyList<SourceVerificationReceipt> PriorReceipts, DateTimeOffset CycleCreatedAtUtc, Guid AttemptId);

public interface ISourceVerificationExecutionStore
{
    ValueTask<SourceVerificationBatch?> PrepareAsync(SourceRootConfiguration root, SourceScanRequest request, CancellationToken cancellationToken);
    ValueTask<bool> MayReadAsync(SourceVerificationBatch batch, CancellationToken cancellationToken);
    ValueTask<bool> CheckpointAsync(SourceVerificationBatch batch, SourceVerificationItem item, SourceVerificationDigest source,
        IRetainedFileObservationLease retained, DateTimeOffset retainedHashedAtUtc, long sourceBytesRead, long retainedBytesRead,
        CancellationToken cancellationToken);
    ValueTask DeferAsync(SourceVerificationBatch batch, SourceVerificationItem item, string reason, bool requestCapture, CancellationToken cancellationToken);
    ValueTask FinishBatchAsync(SourceVerificationBatch batch, long sourceBytesRead, long retainedBytesRead, string? reason, CancellationToken cancellationToken);
}

public sealed record SourceVerificationRunStatus(Guid Id, string State, int Total, int Verified, int Resolved,
    int Deferred, long SourceBytesRead, long RetainedBytesRead, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc, DateTimeOffset? NextBatchAtUtc, string? Reason)
{
    public bool ReadCountsIncomplete { get; init; }
}

public sealed record SourceVerificationStatus(SourceRootId SourceRootId, SourceVerificationPolicy Policy,
    bool Paused, DateTimeOffset? LastVerifiedAtUtc, SourceVerificationRunStatus? Run)
{
    public bool FeatureEnabled { get; init; }
    public DateTimeOffset? NextWindowAtUtc { get; init; }
    public DateTimeOffset? WindowEndAtUtc { get; init; }
    public SourceIncrementalStatus? Scanning { get; init; }
}

public sealed record SourceIncrementalStatus(string Mode, string? Reason, long BaselineEpoch, bool NamespaceComplete,
    bool RecoveryRequired, DateTimeOffset? LastReconciliationAtUtc, long DurableJournalLagUsn, int PendingObligations,
    DateTimeOffset? LastBaselineAtUtc);

public interface ISourceVerificationStore
{
    ValueTask<Guid> StartAsync(SourceRootId rootId, bool manual, CancellationToken cancellationToken);
    ValueTask<SourceVerificationStatus?> ReadAsync(SourceRootId rootId, CancellationToken cancellationToken);
}

public interface ISourceVerificationScheduler
{
    ValueTask<int> PumpAsync(CancellationToken cancellationToken);
}
