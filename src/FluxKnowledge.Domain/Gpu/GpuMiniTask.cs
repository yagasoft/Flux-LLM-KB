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

    public GpuMiniTaskExecutionState ExecutionState { get; private init; }

    public DateTimeOffset? DeferredUntilUtc { get; private init; }

    public Guid? BatchId { get; private init; }

    public PublicJobState InitialParentJobState => PublicJobState.GpuQueued;

    public static GpuMiniTask Create(
        JobId parentJobId,
        long sourceRevision,
        GpuPriorityLane priorityLane,
        string modelRuntimeKey,
        string settingsFingerprint,
        long estimatedBytes,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(parentJobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRuntimeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        RequireCanonicalOpaqueKey(modelRuntimeKey, nameof(modelRuntimeKey));
        RequireCanonicalOpaqueKey(settingsFingerprint, nameof(settingsFingerprint));
        RequireCanonicalOpaqueKey(idempotencyKey, nameof(idempotencyKey));
        if (!Enum.IsDefined(priorityLane))
        {
            throw new DomainInvariantException("GPU mini-task priority lane is invalid.");
        }

        if (sourceRevision <= 0 || estimatedBytes <= 0)
        {
            throw new DomainInvariantException("GPU mini-task numeric values are invalid.");
        }

        return new GpuMiniTask(
            Guid.NewGuid(), parentJobId, sourceRevision, priorityLane, modelRuntimeKey,
            settingsFingerprint, estimatedBytes, idempotencyKey);
    }

    private static void RequireCanonicalOpaqueKey(string value, string parameterName)
    {
        if (char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException(
                "GPU mini-task opaque keys cannot end with whitespace.",
                parameterName);
        }
    }

    private GpuMiniTask(
        Guid id,
        JobId parentJobId,
        long sourceRevision,
        GpuPriorityLane priorityLane,
        string modelRuntimeKey,
        string settingsFingerprint,
        long estimatedBytes,
        string idempotencyKey)
    {
        Id = id;
        ParentJobId = parentJobId;
        SourceRevision = sourceRevision;
        PriorityLane = priorityLane;
        ModelRuntimeKey = modelRuntimeKey;
        SettingsFingerprint = settingsFingerprint;
        EstimatedBytes = estimatedBytes;
        IdempotencyKey = idempotencyKey;
        ExecutionState = GpuMiniTaskExecutionState.Ready;
        DeferredUntilUtc = null;
        BatchId = null;
        AdmissionGeneration = 0;
    }
}
