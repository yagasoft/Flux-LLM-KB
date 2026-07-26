using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Jobs;

namespace FluxKnowledge.Domain.Gpu;

public sealed record GpuMiniTask
{
    public Guid Id { get; private init; }

    public JobId ParentJobId { get; private init; }

    public long SourceRevision { get; private init; }

    public GpuPriorityLane PriorityLane { get; private init; }

    public string ModelRuntimeKey { get; private init; }

    public string SettingsFingerprint { get; private init; }

    public long EstimatedBytes { get; private init; }

    public long AdmissionGeneration { get; private init; }

    public string IdempotencyKey { get; private init; }

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

    private GpuMiniTask(
        Guid id,
        JobId parentJobId,
        long sourceRevision,
        GpuPriorityLane priorityLane,
        string modelRuntimeKey,
        string settingsFingerprint,
        long estimatedBytes,
        long admissionGeneration,
        string idempotencyKey)
    {
        Id = id;
        ParentJobId = parentJobId;
        SourceRevision = sourceRevision;
        PriorityLane = priorityLane;
        ModelRuntimeKey = modelRuntimeKey;
        SettingsFingerprint = settingsFingerprint;
        EstimatedBytes = estimatedBytes;
        AdmissionGeneration = admissionGeneration;
        IdempotencyKey = idempotencyKey;
    }
}
