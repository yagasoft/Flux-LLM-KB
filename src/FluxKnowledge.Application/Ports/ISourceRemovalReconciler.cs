namespace FluxKnowledge.Application.Ports;

public interface ISourceRemovalReconciler
{
    ValueTask<int> ReconcileAsync(CancellationToken cancellationToken);
}

/// <summary>Durable physical-absence evidence; it does not claim a completed content scan.</summary>
public sealed record SourceGuardedRemovalWitness(int Version, string Kind, Guid SourceRootId, Guid SourceRevisionId,
    string RelativePath, long ConfigurationRevision, string PhysicalIdentityFingerprint, Guid PendingId,
    long PendingGeneration, DateTimeOffset SuppressedAtUtc, long BaselineEpoch, long RecoveryGeneration,
    string? JournalId, long CoveredThroughUsn, Guid? InventoryEntryId, string? FileId, Guid? ArtifactId);
