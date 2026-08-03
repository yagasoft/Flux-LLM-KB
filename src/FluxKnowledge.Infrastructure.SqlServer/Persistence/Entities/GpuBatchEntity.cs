namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class GpuBatchEntity
{
    public Guid Id { get; set; }
    public string CapacitySlotKey { get; set; } = string.Empty;
    public int PriorityLane { get; set; }
    public string ModelRuntimeKey { get; set; } = string.Empty;
    public string SettingsFingerprint { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public long EstimatedBytes { get; set; }
    public long AdmissionGeneration { get; set; }
    public string OwnerKey { get; set; } = string.Empty;
    public int State { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public GpuCapacitySlotEntity CapacitySlot { get; set; } = null!;
}
