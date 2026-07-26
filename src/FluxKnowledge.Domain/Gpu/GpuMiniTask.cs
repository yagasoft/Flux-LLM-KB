using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;

namespace FluxKnowledge.Domain.Gpu;

public sealed record GpuMiniTask(
    Guid Id,
    JobId ParentJobId,
    long SourceRevision,
    GpuPriorityLane PriorityLane,
    string ModelRuntimeKey,
    string SettingsFingerprint,
    long EstimatedBytes,
    long AdmissionGeneration,
    string IdempotencyKey)
{
    public PublicJobState InitialParentJobState => PublicJobState.GpuQueued;

    public static GpuMiniTask Create(
        JobId parentJobId,
        long sourceRevision,
        GpuPriorityLane priorityLane,
        string modelRuntimeKey,
        string settingsFingerprint,
        long estimatedBytes,
        long admissionGeneration,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(parentJobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRuntimeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (sourceRevision <= 0 || estimatedBytes < 0 || admissionGeneration < 0)
        {
            throw new DomainInvariantException("GPU mini-task numeric values are invalid.");
        }

        return new GpuMiniTask(
            Guid.NewGuid(), parentJobId, sourceRevision, priorityLane, modelRuntimeKey,
            settingsFingerprint, estimatedBytes, admissionGeneration, idempotencyKey);
    }
}
