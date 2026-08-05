namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>
/// Append-only executor outcome receipt.  It retains classification and digest
/// only; result payloads and artefacts are intentionally outside this boundary.
/// </summary>
public sealed class GpuExecutorResultReceiptEntity
{
    public Guid OperationId { get; set; }
    public Guid DispatchId { get; set; }
    public Guid BatchId { get; set; }
    public Guid MiniTaskId { get; set; }
    public string ExecutorKey { get; set; } = string.Empty;
    public long AdmissionGeneration { get; set; }
    public int Disposition { get; set; }
    public int EvidenceClass { get; set; }
    public byte[]? OpaqueResultDigest { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public GpuExecutorDispatchEntity Dispatch { get; set; } = null!;
    public GpuBatchEntity Batch { get; set; } = null!;
    public GpuMiniTaskEntity MiniTask { get; set; } = null!;
}
