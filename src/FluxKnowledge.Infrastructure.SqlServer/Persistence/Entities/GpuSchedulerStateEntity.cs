namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class GpuSchedulerStateEntity
{
    public int Id { get; set; }
    public long WakeGeneration { get; set; }
    public int PendingWakeReasons { get; set; }
    public DateTimeOffset? NextDeferredAtUtc { get; set; }
    public Guid? InFlightWakeOperationId { get; set; }
    public long? InFlightWakeGeneration { get; set; }
    public int InFlightWakeReasons { get; set; }
    public DateTimeOffset? InFlightNextDeferredAtUtc { get; set; }
    public int? InFlightEffectiveAdmissionReasons { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
