using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;

namespace FluxKnowledge.Application.Gpu;

/// <summary>One bounded background attempt through the existing durable scheduler lifecycle.</summary>
public sealed class EmbeddingGpuExecutor(IEmbeddingGpuRequestStore requests, IGpuExecutorLifecycleSink lifecycle,
    IGpuSchedulerStore scheduler, IEmbeddingGpuInference inference, IGpuInteractiveOwnerProbe process,
    EmbeddingGpuRuntime runtime, IOutboxWakeSignal outboxWake, IGpuSchedulerWakeSignal gpuWake, TimeProvider clock,
    CancellationToken stoppingToken = default) : IGpuExecutorAdapter, IGpuExecutorRecoveryAdapter
{
    private readonly Guid _instance = Guid.NewGuid();
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _deliveries = new();
    private readonly ConcurrentDictionary<Guid, byte> _unconfirmedNative = new();
    private readonly ConcurrentDictionary<Guid, Guid> _awaitingCleanup = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _executions = new();
    public const string Name = "embedding-gpu:bge-m3-v1";
    public string ExecutorKey => Name;

    public async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var work in await requests.ReadRecoveryAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_deliveries.TryGetValue(work.Handle.DispatchId, out var delivery) && delivery.IsValueCreated && !delivery.Value.IsCompleted) continue;
            if (!work.NativeCleanupConfirmed && work.ExecutorInstanceId != _instance)
            {
                if (work.Owner is null)
                {
                    if (!await requests.ConfirmUnstartedCleanupAsync(work, _instance, Operation(work.Handle, "claim"), process.Current, cancellationToken).ConfigureAwait(false)) continue;
                }
                else
                {
                    if (process.Observe(work.Owner) != GpuInteractiveOwnerObservation.Exited ||
                        !await requests.ConfirmExitedCleanupAsync(work, cancellationToken).ConfigureAwait(false)) continue;
                }
            }
            await DeliverAsync(work.Handle, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
    {
        handle.Validate();
        if (handle.ExecutorKey != ExecutorKey) throw new InvalidOperationException("embedding-gpu-executor-mismatch");
        var delivery = _deliveries.GetOrAdd(handle.DispatchId, _ => new(() => RunAsync(handle), LazyThreadSafetyMode.ExecutionAndPublication));
        try { await delivery.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            if (delivery.IsValueCreated && delivery.Value.IsCompleted && !_unconfirmedNative.ContainsKey(handle.DispatchId))
                _deliveries.TryRemove(new KeyValuePair<Guid, Lazy<Task>>(handle.DispatchId, delivery));
        }
    }

    public bool RequestCancellation(Guid miniTaskId)
    {
        if (!_executions.TryGetValue(miniTaskId, out var cancellation)) return false;
        try { cancellation.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    private async Task RunAsync(GpuExecutorBatchHandle handle)
    {
        var pending = await requests.ReadPendingCompletionAsync(handle, stoppingToken).ConfigureAwait(false);
        if (pending is not null)
        {
            _awaitingCleanup.TryRemove(handle.DispatchId, out _);
            await CompleteAsync(handle, pending).ConfigureAwait(false);
            return;
        }
        if (_awaitingCleanup.TryGetValue(handle.DispatchId, out var completedClaim))
        {
            await PersistCleanupAndCompleteAsync(handle, completedClaim).ConfigureAwait(false);
            return;
        }
        var acknowledgement = await lifecycle.AcknowledgeAsync(new(Operation(handle, "ack"), handle), stoppingToken).ConfigureAwait(false);
        if (!acknowledgement.Accepted || !acknowledgement.Committed) return;
        var claimOperation = Operation(handle, "claim");
        var work = await requests.ClaimExecutionAsync(handle, _instance, claimOperation, process.Current, stoppingToken).ConfigureAwait(false);
        var released = true; // No native callback has started.
        if (work is not null)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            if (!_executions.TryAdd(work.MiniTaskId, cancellation)) throw new InvalidOperationException("embedding-gpu-execution-duplicate");
            var ownership = new GpuOwnedWorkContext(handle, runtime.RuntimeKey, runtime.SettingsFingerprint, cancellation.Token);
            try
            {
                released = false;
                var result = await inference.EmbedBatchAsync(ownership, work.Batch.Chunks.Select(chunk => chunk.SearchText).ToArray(), cancellation.Token).ConfigureAwait(false);
                released = result.NativeCapacityReleased && ownership.InvalidateAndReadNativeRelease();
                if (released && result.RefusalReason is null && result.Value is not null && !cancellation.IsCancellationRequested)
                    await requests.CommitAsync(handle, _instance, work, result.Value, cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // The registered resource proof decides release. A failed/ambiguous
                // checkpoint commit is recovered from its durable digest, never replayed here.
                released = ownership.NativeCapacityReleased;
            }
            finally
            {
                released &= ownership.InvalidateAndReadNativeRelease();
                if (!released) _unconfirmedNative.TryAdd(handle.DispatchId, 0);
                _executions.TryRemove(work.MiniTaskId, out _);
            }
        }
        if (!released)
        {
            // Retain this incarnation's delivery after uncertain native cleanup.
            // A repeated dispatch cannot reinterpret a refused ownership read as
            // proof that the previous native callback never started.
            _unconfirmedNative.TryAdd(handle.DispatchId, 0);
            await MarkUncertainAsync(handle).ConfigureAwait(false);
            return;
        }
        // A lost or uncommitted cleanup write must not replay this native attempt.
        // Retain only its claim identity until the durable cleanup proof is read back.
        _awaitingCleanup[handle.DispatchId] = claimOperation;
        await PersistCleanupAndCompleteAsync(handle, claimOperation).ConfigureAwait(false);
    }

    private async ValueTask PersistCleanupAndCompleteAsync(GpuExecutorBatchHandle handle, Guid claimOperation)
    {
        await requests.RecordNativeCleanupAsync(handle, _instance, claimOperation, CancellationToken.None).ConfigureAwait(false);
        var pending = await requests.ReadPendingCompletionAsync(handle, CancellationToken.None).ConfigureAwait(false);
        if (pending is null) return;
        _awaitingCleanup.TryRemove(handle.DispatchId, out _);
        await CompleteAsync(handle, pending).ConfigureAwait(false);
    }

    private async ValueTask CompleteAsync(GpuExecutorBatchHandle handle, EmbeddingGpuCompletion completion)
    {
        var disposition = completion.ResultDigest is null ? GpuMiniTaskBoundaryDisposition.OutcomeUncertain : GpuMiniTaskBoundaryDisposition.Completed;
        var receipt = await lifecycle.RecordReceiptAsync(new(Operation(handle, "receipt"), handle, completion.MiniTaskId, disposition,
            completion.ResultDigest, completion.ResultDigest is null ? GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed : GpuExecutorEvidenceClass.TaskOutcomeConfirmed),
            CancellationToken.None).ConfigureAwait(false);
        var released = false;
        if (receipt.Accepted && receipt.Committed)
        {
            var callback = await lifecycle.HandleCallbackAsync(Operation(handle, "callback"), new(handle,
                completion.ResultDigest is null ? GpuBatchCallbackKind.CapacityReleased : GpuBatchCallbackKind.Completed,
                [new(completion.MiniTaskId, disposition)], CapacityReleased: true), CancellationToken.None).ConfigureAwait(false);
            released = callback.Accepted && callback.Committed;
        }
        if (!released)
        {
            await MarkUncertainAsync(handle).ConfigureAwait(false);
            var outcome = new GpuExecutorTrustedEvidence(Operation(handle, "outcome-proof"), handle, "embedding-native-cleanup",
                completion.CleanupConfirmedAtUtc, GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed);
            var capacity = outcome with { OperationId = Operation(handle, "capacity-proof"), EvidenceClass = GpuExecutorEvidenceClass.CapacityReleaseConfirmed };
            if (!(await lifecycle.RecordTrustedEvidenceAsync(outcome, CancellationToken.None).ConfigureAwait(false)).Committed ||
                !(await lifecycle.RecordTrustedEvidenceAsync(capacity, CancellationToken.None).ConfigureAwait(false)).Committed) return;
            var reconciledOutcome = await scheduler.ReconcileTaskOutcomeAsync(Operation(handle, "reconcile-outcome"),
                new(handle, outcome.OperationId, completion.MiniTaskId), CancellationToken.None).ConfigureAwait(false);
            var reconciledCapacity = await scheduler.ReconcileCapacityAsync(Operation(handle, "reconcile-capacity"),
                new(handle, capacity.OperationId), CancellationToken.None).ConfigureAwait(false);
            released = reconciledOutcome.Committed && reconciledCapacity.Committed;
        }
        if (released && await requests.RequeueSettledAsync(handle, completion.MiniTaskId, CancellationToken.None).ConfigureAwait(false)) outboxWake.Notify();
        if (released) gpuWake.Notify(GpuSchedulerWakeReason.CapacityReleased | GpuSchedulerWakeReason.Reconciliation);
    }

    private async ValueTask MarkUncertainAsync(GpuExecutorBatchHandle handle)
    {
        var reservation = (await scheduler.ReadStaleCapacityReservationsAsync(clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false))
            .SingleOrDefault(value => value.BatchId == handle.BatchId && value.CapacitySlotKey == handle.CapacitySlotKey && value.AdmissionGeneration == handle.AdmissionGeneration);
        if (reservation is not null) await scheduler.MarkCapacityUncertainAsync(Operation(handle, "uncertain"), reservation, CancellationToken.None).ConfigureAwait(false);
    }

    private static Guid Operation(GpuExecutorBatchHandle handle, string purpose)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"embedding-gpu-v1|{handle.DispatchId:N}|{purpose}")).AsSpan(0, 16));
}
