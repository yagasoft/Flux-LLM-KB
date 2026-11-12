namespace FluxKnowledge.Application.Gpu;

/// <summary>An explicitly admitted model profile; the executor retains its existing default profile.</summary>
public sealed record GpuInteractiveExecutionProfile(string RuntimeKey, string SettingsFingerprint, long EstimatedBytes);

public enum GpuInteractiveSettlementKind { NoHandoff, CancelledBeforeExecution, Released, RetainedUncertain }

public sealed record GpuInteractiveSettlement(Guid RequestId, Guid ExecutorInstanceId, GpuExecutorBatchHandle? Handle,
    GpuInteractiveSettlementKind Kind, bool NativeCallbackEntered, bool NativeCapacityReleased, string? ObservationFailureType=null);

/// <summary>Result cancellation may precede settlement. Owners must join Settlement before admitting another cohort.</summary>
public sealed record GpuInteractiveOperation<T>(Guid RequestId, Guid ExecutorInstanceId, string ExecutorKey,
    Task<T> Result, Task<GpuInteractiveSettlement> Settlement);

public enum GpuInteractiveRequestState { NotFound, Queued, Owned, CancelledBeforeExecution, Released, Uncertain }

public sealed record GpuInteractiveRequestSnapshot(Guid RequestId, Guid ExecutorInstanceId,
    string? RuntimeKey, string? SettingsFingerprint, GpuExecutorBatchHandle? Handle, GpuInteractiveRequestState State);

/// <summary>Exact request proof, independent of globally paged recovery listings and subsequent slot owners.</summary>
public interface IGpuInteractiveSettlementStore
{
    ValueTask<GpuInteractiveRequestSnapshot> ReadInteractiveRequestAsync(Guid requestId, Guid executorInstanceId,
        CancellationToken cancellationToken);
}
