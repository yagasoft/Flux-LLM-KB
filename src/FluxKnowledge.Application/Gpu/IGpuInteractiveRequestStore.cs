namespace FluxKnowledge.Application.Gpu;

/// <summary>Persists expiring GPU ownership only; query/candidate payloads remain with the bound executor.</summary>
public interface IGpuInteractiveRequestStore
{
    ValueTask<GpuMiniTaskHandoffResult> HandoffInteractiveAsync(
        GpuInteractiveHandoffRequest request, CancellationToken cancellationToken);
    ValueTask<bool> CancelInteractiveAsync(Guid requestId, Guid executorInstanceId, CancellationToken cancellationToken);
    ValueTask<GpuInteractiveExecutionWork?> ReadInteractiveExecutionAsync(
        GpuExecutorBatchHandle handle, Guid executorInstanceId, GpuExecutorDispatchState requiredDispatchState, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<GpuInteractiveRecoveryWork>> ReadInteractiveRecoveryAsync(CancellationToken cancellationToken);
    ValueTask<bool> CancelLostInteractiveReadyAsync(Guid requestId, GpuInteractiveOwnerIdentity owner, CancellationToken cancellationToken);
}

public sealed record GpuInteractiveRecoveryWork(Guid RequestId, Guid ExecutorInstanceId, GpuInteractiveOwnerIdentity Owner,
    GpuExecutorBatchHandle? Handle, GpuCapacityUncertaintyRequest? Reservation);

// Opaque dispatch metadata only. Cancellation/deadline can prohibit execution but
// never establish native termination or permit capacity release.
public sealed record GpuInteractiveExecutionWork(Guid RequestId, Guid ExecutorInstanceId,
    string ModelRuntimeKey, string SettingsFingerprint, DateTimeOffset ExecutionDeadlineUtc, bool CancellationRequested);

public sealed record GpuInteractiveHandoffRequest(
    Guid RequestId,
    Guid ExecutorInstanceId,
    string ExecutorKey,
    string ModelRuntimeKey,
    string SettingsFingerprint,
    long EstimatedBytes,
    DateTimeOffset QueueDeadlineUtc,
    DateTimeOffset ExecutionDeadlineUtc);
