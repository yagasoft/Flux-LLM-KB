namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

/// <summary>
/// Private, SQL-authoritative attestation of one application-owned native worker.
/// It contains no process path, pipe, nonce, command line or environment data.
/// </summary>
public sealed class NativeWorkerInstanceEntity
{
    public Guid InstanceId { get; set; }
    public string ExecutorKey { get; set; } = string.Empty;
    public int? ProcessId { get; set; }
    public DateTimeOffset? ProcessStartedAtUtc { get; set; }
    public string ExecutableFingerprint { get; set; } = string.Empty;
    public string ProtocolVersion { get; set; } = string.Empty;
    public int State { get; set; }
    public DateTimeOffset LaunchedAtUtc { get; set; }
    public DateTimeOffset? ConnectedAtUtc { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
    public DateTimeOffset? ExitedAtUtc { get; set; }
    public Guid? ActiveDispatchId { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public GpuExecutorDispatchEntity? ActiveDispatch { get; set; }
}
