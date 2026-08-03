namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class GpuMiniTaskEntity
{
    public Guid Id { get; set; }
    public Guid ParentJobId { get; set; }
    public long SourceRevision { get; set; }
    public int PriorityLane { get; set; }
    public string ModelRuntimeKey { get; set; } = string.Empty;
    public string SettingsFingerprint { get; set; } = string.Empty;
    public long EstimatedBytes { get; set; }
    public long AdmissionGeneration { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? HandoffLeaseOwner { get; set; }
    public int ExecutionState { get; set; }
    public long CreatedSequence { get; set; }
    public DateTimeOffset? DeferredUntilUtc { get; set; }
    public Guid? BatchId { get; set; }
    public int ReservationAttemptCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public JobEntity ParentJob { get; set; } = null!;
    public GpuBatchEntity? Batch { get; set; }
}
