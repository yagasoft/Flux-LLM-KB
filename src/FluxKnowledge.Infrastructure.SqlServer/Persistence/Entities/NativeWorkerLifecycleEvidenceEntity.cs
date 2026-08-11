namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>
/// Immutable, sanitised lifecycle observation. OperationId is the replay fence.
/// </summary>
public sealed class NativeWorkerLifecycleEvidenceEntity
{
    public Guid OperationId { get; set; }
    public Guid InstanceId { get; set; }
    public int LifecycleClass { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public int? OutcomeCode { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public NativeWorkerInstanceEntity Instance { get; set; } = null!;
}
