namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>
/// Append-only trusted evidence metadata.  Raw external evidence is not stored.
/// </summary>
public sealed class GpuExecutorEvidenceEntity
{
    public Guid OperationId { get; set; }
    public Guid DispatchId { get; set; }
    public Guid BatchId { get; set; }
    public string CapacitySlotKey { get; set; } = string.Empty;
    public string ExecutorKey { get; set; } = string.Empty;
    public long AdmissionGeneration { get; set; }
    public int EvidenceClass { get; set; }
    public string VerifierKey { get; set; } = string.Empty;
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public GpuExecutorDispatchEntity Dispatch { get; set; } = null!;
    public GpuBatchEntity Batch { get; set; } = null!;
    public GpuCapacitySlotEntity CapacitySlot { get; set; } = null!;
}
