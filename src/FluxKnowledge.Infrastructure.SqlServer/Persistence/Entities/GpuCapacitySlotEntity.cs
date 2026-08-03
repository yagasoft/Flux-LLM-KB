namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class GpuCapacitySlotEntity
{
    public string SlotKey { get; set; } = string.Empty;
    public int State { get; set; }
    public Guid? ActiveBatchId { get; set; }
    public string? OwnerKey { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public GpuBatchEntity? ActiveBatch { get; set; }
}
