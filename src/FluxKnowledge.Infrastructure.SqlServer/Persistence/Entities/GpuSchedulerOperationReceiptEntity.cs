namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>
/// Private replay evidence for one in-process lifecycle transaction. It is not a
/// scheduler API, status projection, or executor record.
/// </summary>
public sealed class GpuSchedulerOperationReceiptEntity
{
    public Guid OperationId { get; set; }
    public string OperationKind { get; set; } = string.Empty;
    public string? RequestFingerprint { get; set; }
    public Guid? BatchId { get; set; }
    public string? CapacitySlotKey { get; set; }
    public string? OwnerKey { get; set; }
    public long? AdmissionGeneration { get; set; }
    public bool Accepted { get; set; }
    public bool Committed { get; set; }
    public int WakeReasons { get; set; }
    public int? AdmissionDisposition { get; set; }
    public DateTimeOffset? DeferredUntilUtc { get; set; }
    public long? WakeGeneration { get; set; }
    public DateTimeOffset? NextDeferredAtUtc { get; set; }
    public Guid? WakeConsumptionOperationId { get; set; }
    public int? EffectiveAdmissionReasons { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
