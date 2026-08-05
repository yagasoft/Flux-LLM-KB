namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>
/// SQL-authoritative private fence for one admitted GPU batch.  The retained owner
/// key is deliberately never projected through executor contracts.
/// </summary>
public sealed class GpuExecutorDispatchEntity
{
    public Guid DispatchId { get; set; }
    public Guid BatchId { get; set; }
    public string CapacitySlotKey { get; set; } = string.Empty;
    public string OwnerKey { get; set; } = string.Empty;
    public string ExecutorKey { get; set; } = string.Empty;
    public long AdmissionGeneration { get; set; }
    public int State { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public GpuBatchEntity Batch { get; set; } = null!;
    public GpuCapacitySlotEntity CapacitySlot { get; set; } = null!;
}
