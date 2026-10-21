namespace FluxKnowledge.Application.Ports;

/// <summary>Cooperative derived-storage work; budgets never limit authorised source content.</summary>
public interface IIndexProjectionMaintenance
{
    ValueTask<IndexProjectionMaintenanceResult> RunOnceAsync(CancellationToken cancellationToken);
}

// MaximumSegments bounds physical segment attempts; MaximumOrphanMarks separately
// bounds orphan metadata intents. At most two manifests are marked and two reclaimed.
// Marking a manifest examines its segment references, not just MaximumSegments rows.
public sealed record IndexProjectionMaintenanceBudget(int MaximumSegments = 8, long MaximumBytes = 256 * 1024 * 1024,
    int MaximumSeconds = 15, int RetainedInactiveManifests = 2, int MaximumHistoryHours = 24, int MaximumOrphanMarks = 8,
    int OrphanGraceMinutes = 30, int MaximumCandidateObservations = 8, int MaximumMemberDeletes = 256, int MaximumChangeDeletes = 256)
{
    public void Validate()
    {
        if (MaximumSegments <= 0 || MaximumBytes <= 0 || MaximumSeconds <= 0 || RetainedInactiveManifests < 0 || MaximumHistoryHours < 0 ||
            MaximumOrphanMarks <= 0 || OrphanGraceMinutes < 0 || MaximumCandidateObservations <= 0 || MaximumMemberDeletes <= 0 || MaximumChangeDeletes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumSegments));
    }
}

public sealed record IndexProjectionMaintenanceResult(int ReclaimedSegments, long ReclaimedBytes, int ReclaimedManifests,
    string? ReasonCode = null, long ReservedReadBytes = 0, bool WorkRemaining = false);
