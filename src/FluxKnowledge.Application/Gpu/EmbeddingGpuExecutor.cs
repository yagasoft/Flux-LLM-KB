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
    CancellationToken stoppingToken = default, IGpuProcessModelResidency? residency = null)
    : IGpuExecutorAdapter, IGpuExecutorRecoveryAdapter
{
    private readonly Guid _instance = Guid.NewGuid();
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _deliveries = new();
    private readonly ConcurrentDictionary<Guid, byte> _unconfirmedNative = new();
    private readonly ConcurrentDictionary<Guid, Guid> _awaitingCleanup = new();
    private readonly ConcurrentDictionary<Guid, GpuOwnedWorkContext> _warmAttempts = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _executions = new();
    public const string Name = "embedding-gpu:bge-m3-v1";
    public string ExecutorKey => Name;

    public async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var work in await requests.ReadRecoveryAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_deliveries.TryGetValue(work.Handle.DispatchId, out var delivery) && delivery.IsValueCreated && !delivery.Value.IsCompleted) continue;
            if (!work.NativeCleanupConfirmed && !work.Detached && work.ExecutorInstanceId != _instance)
            {
                if (work.Owner is null)
                {
                    var state = await requests.ReadUnclaimedResidentDispatchAsync(work, process.Current, cancellationToken).ConfigureAwait(false);
                    if (state == GpuExecutorDispatchState.DeliveryUncertain)
                    {
                        var ownership = new GpuOwnedWorkContext(work.Handle, runtime.RuntimeKey, runtime.SettingsFingerprint, cancellationToken);
                        // The watchdog has ended native admission. Recover the
                        // original allocation without invoking the inference callback.
                        if (residency is null || !await residency.ReleaseAsync(ownership, cancellationToken).ConfigureAwait(false))
                            continue;
                    }
                    if (state != GpuExecutorDispatchState.Acknowledged &&
                        !await requests.ConfirmUnstartedCleanupAsync(work, _instance, Operation(work.Handle, "claim"), process.Current, cancellationToken).ConfigureAwait(false))
                        continue;
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
            _warmAttempts.TryRemove(handle.DispatchId, out _);
            await CompleteAsync(handle, pending).ConfigureAwait(false);
            return;
        }
        if (_awaitingCleanup.TryGetValue(handle.DispatchId, out var completedClaim))
        {
            await PersistCleanupAndCompleteAsync(handle, completedClaim).ConfigureAwait(false);
            return;
        }
        var claimOperation = Operation(handle, "claim");
        if (_warmAttempts.TryGetValue(handle.DispatchId, out var previousWarmAttempt))
        {
            await FinishWarmAsync(handle, previousWarmAttempt, claimOperation).ConfigureAwait(false);
            return;
        }
        var acknowledgement = await lifecycle.AcknowledgeAsync(new(Operation(handle, "ack"), handle), stoppingToken).ConfigureAwait(false);
        if (!acknowledgement.Accepted || !acknowledgement.Committed) return;
        var work = await requests.ClaimExecutionAsync(handle, _instance, claimOperation, process.Current, stoppingToken).ConfigureAwait(false);
        var released = true; // No native callback has started.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ownership = new GpuOwnedWorkContext(handle, runtime.RuntimeKey, runtime.SettingsFingerprint, cancellation.Token);
        var supportsResidency = residency?.Supports(ownership) == true;
        if (supportsResidency) _warmAttempts[handle.DispatchId] = ownership;
        if (work is not null)
        {
            if (!_executions.TryAdd(work.MiniTaskId, cancellation)) throw new InvalidOperationException("embedding-gpu-execution-duplicate");
            var nativeStarted = false;
            try
            {
                // Register cancellation first, then recheck durable withdrawal. This
                // closes the interval in which an earlier cancellation signal had no owner.
                if (await requests.IsExecutionSourceAvailableAsync(handle, _instance, work.MiniTaskId, cancellation.Token).ConfigureAwait(false))
                {
                    nativeStarted = true;
                    released = false;
                    var result = await inference.EmbedBatchAsync(ownership, work.Batch.Chunks.Select(chunk => chunk.SearchText).ToArray(), cancellation.Token).ConfigureAwait(false);
                    released = result.NativeCapacityReleased && ownership.InvalidateAndReadNativeRelease();
                    if ((released || supportsResidency && ownership.ResidentModelUsed) && result.RefusalReason is null &&
                        result.Value is not null && !cancellation.IsCancellationRequested)
                    {
                        await requests.CommitAsync(handle, _instance, work, result.Value, cancellation.Token).ConfigureAwait(false);
                        if (supportsResidency && ownership.ResidentModelUsed &&
                            await requests.ReadResidentResultDigestAsync(handle, _instance, claimOperation, cancellation.Token).ConfigureAwait(false)
                                is { Length: 32 } digest)
                        {
                            var receipt = await lifecycle.RecordReceiptAsync(new(Operation(handle, "receipt"), handle, work.MiniTaskId,
                                GpuMiniTaskBoundaryDisposition.Completed, digest, GpuExecutorEvidenceClass.TaskOutcomeConfirmed),
                                cancellation.Token).ConfigureAwait(false);
                            if (receipt.Accepted && receipt.Committed)
                                await residency!.CompleteAsync(ownership, Operation(handle, "detached-callback"),
                                    work.MiniTaskId, cancellation.Token).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // The registered resource proof decides release. A failed/ambiguous
                // checkpoint commit is recovered from its durable digest, never replayed here.
                released = !nativeStarted || ownership.NativeCapacityReleased;
            }
            finally
            {
                var nativeReleased = ownership.InvalidateAndReadNativeRelease();
                if (nativeStarted) released &= nativeReleased;
                if (!supportsResidency && !released) _unconfirmedNative.TryAdd(handle.DispatchId, 0);
                _executions.TryRemove(work.MiniTaskId, out _);
            }
        }
        if (supportsResidency)
        {
            ownership.Invalidate();
            await FinishWarmAsync(handle, ownership, claimOperation).ConfigureAwait(false);
            return;
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

    private async ValueTask FinishWarmAsync(GpuExecutorBatchHandle handle, GpuOwnedWorkContext ownership, Guid claimOperation)
    {
        // Retrying a lost response reads the original dispatch. It never re-enters inference.
        var settled = await requests.ReadPendingCompletionAsync(handle, CancellationToken.None).ConfigureAwait(false);
        if (settled?.Detached == true)
        {
            _warmAttempts.TryRemove(handle.DispatchId, out _);
            await CompleteAsync(handle, settled).ConfigureAwait(false);
            return;
        }
        if (!await residency!.ReleaseAsync(ownership, CancellationToken.None).ConfigureAwait(false))
        {
            await MarkUncertainAsync(handle).ConfigureAwait(false);
            return;
        }
        _awaitingCleanup[handle.DispatchId] = claimOperation;
        _warmAttempts.TryRemove(handle.DispatchId, out _);
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
        if (completion.Detached)
        {
            if (await requests.RequeueSettledAsync(handle, completion.MiniTaskId, CancellationToken.None).ConfigureAwait(false))
                outboxWake.Notify();
            gpuWake.Notify(GpuSchedulerWakeReason.Reconciliation);
            return;
        }
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
