using System.Collections.Concurrent;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Documents;
using FluxKnowledge.Integrations.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>Runs one exact source-bound transcription and settles only after durable native cleanup.</summary>
public sealed class MediaTranscriptionGpuExecutor(IServiceScopeFactory scopes, MediaTranscriptionGpuRuntime runtime,
    IGpuInteractiveOwnerProbe ownerProbe, IDocumentOcrProcessRecovery recovery, IOutboxWakeSignal outboxWake,
    IGpuSchedulerWakeSignal gpuWake, TimeProvider time, IHostApplicationLifetime lifetime) : IGpuExecutorAdapter, IGpuExecutorRecoveryAdapter
{
    private readonly DocumentOcrSupervisorIdentity _supervisor = new(Guid.NewGuid(), ownerProbe.Current);
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _deliveries = new();
    public string ExecutorKey => runtime.ExecutorKey;

    public async ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
    {
        handle.Validate();
        if (handle.ExecutorKey != ExecutorKey) throw new InvalidOperationException("transcription-executor-key-invalid");
        var delivery = _deliveries.GetOrAdd(handle.DispatchId, _ => new(() => RunAsync(handle),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try { await delivery.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            if (delivery.IsValueCreated && delivery.Value.IsCompleted)
                _deliveries.TryRemove(new KeyValuePair<Guid, Lazy<Task>>(handle.DispatchId, delivery));
        }
    }

    private async Task RunAsync(GpuExecutorBatchHandle handle)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SqlMediaTranscriptionGpuStore>();
        using var deliveryBudget = new CancellationTokenSource(TimeSpan.FromMinutes(1), time);
        var token = deliveryBudget.Token;
        var pending = await store.ReadPendingCompletionAsync(handle, token).ConfigureAwait(false);
        if (pending is not null)
        {
            await SettleAsync(store, handle, pending, token).ConfigureAwait(false);
            return;
        }

        var residency = scope.ServiceProvider.GetService<GpuModelResidencyRuntime>();
        var entryModel = residency?.EntryModel(runtime.ModelRuntimeKey, runtime.SettingsFingerprint);
        var executionId = entryModel is { ModelKey: "general" }
            ? await residency!.GetExecutionIdentityAsync(handle, token).ConfigureAwait(false) : Guid.NewGuid();
        var claim = await store.TryClaimExecutionAsync(handle, executionId, _supervisor, token).ConfigureAwait(false);
        if (!claim.Lifecycle.Accepted || claim.Lifecycle.Lease is not { } lease ||
            lease.ExecutionInstanceId != executionId || lease.Supervisor != _supervisor) return;
        var worker = scope.ServiceProvider.GetRequiredService<MediaTranscriptionLocalExecutor>();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        var remaining = lease.DeadlineAtUtc - time.GetUtcNow();
        run.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        var heartbeat = HeartbeatAsync(store, lease, run);
        if (entryModel is { ModelKey: "general" } && residency is not null)
        {
            await RunResidentAsync(store, worker, claim, lease, residency, entryModel, run, heartbeat).ConfigureAwait(false);
            return;
        }

        MediaTranscriptionResult? result;
        try
        {
            result = claim.SourceAvailable && claim.SourceRevisionId is { } source && claim.ContentSha256 is { } hash
                ? await worker.RunAsync(source, hash, new(lease, store), run.Token).ConfigureAwait(false)
                : Failure("transcription-source-unavailable");
        }
        finally
        {
            run.Cancel();
            await heartbeat.ConfigureAwait(false);
        }
        if (result is null) return; // Native cleanup is still unconfirmed.

        using var settlementBudget = new CancellationTokenSource(TimeSpan.FromMinutes(1), time);
        var settleToken = settlementBudget.Token;
        // This fallback is accepted only when the durable phase never permitted native start.
        await store.ConfirmCleanupAsync(lease, new(executionId, DocumentOcrCleanupKind.VerifiedUnstarted, null,
            time.GetUtcNow()), settleToken).ConfigureAwait(false);
        await StoreResultAsync(store, worker, lease, result, settleToken).ConfigureAwait(false);
        pending = await store.ReadPendingCompletionAsync(handle, settleToken).ConfigureAwait(false);
        if (pending is not null)
            await SettleAsync(store, handle, pending, settleToken).ConfigureAwait(false);
    }

    private async Task RunResidentAsync(SqlMediaTranscriptionGpuStore store, MediaTranscriptionLocalExecutor worker,
        MediaTranscriptionGpuClaim claim, DocumentOcrExecutionLease lease, GpuModelResidencyRuntime residency,
        GpuResidentModelKey entryModel, CancellationTokenSource run, Task heartbeat)
    {
        GpuModelResidencyRuntime.NativeBorrow? borrowed = null;
        MediaTranscriptionWarmExecutionResult? outcome = null;
        var observed = default(DateTimeOffset);
        var failure = "transcription-source-unavailable";
        try
        {
            if (claim.SourceAvailable && claim.SourceRevisionId is { } source && claim.ContentSha256 is { } hash)
            {
                failure = "transcription-runtime-unavailable";
                borrowed = await residency.OpenHelperAsync(lease.Handle, lease.ExecutionInstanceId, entryModel,
                    worker.StartWarmProcessAsync, run.Token).ConfigureAwait(false);
                failure = "transcription-worker-interrupted";
                var fence = borrowed.Fence;
                outcome = await worker.RunWarmAsync(source, hash, new(lease, store), borrowed.Process,
                    (claimLease, command, ct) => PermitResidentStartAsync(store, claimLease, fence, command, ct), run.Token)
                    .ConfigureAwait(false);
                observed = time.GetUtcNow();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // A launch may have committed its durable borrow before reporting an error.
            // Read that owner again before accepting an unstarted result.
        }
        finally
        {
            run.Cancel();
            await heartbeat.ConfigureAwait(false);
        }

        using var settlementBudget = new CancellationTokenSource(TimeSpan.FromMinutes(1), time);
        var token = settlementBudget.Token;
        var prepared = await PrepareResultAsync(worker, lease, outcome?.Result ?? Failure(failure), token).ConfigureAwait(false);
        if (borrowed is not null && outcome?.Idle is { Model: { } model } idle && prepared.Result.Succeeded)
        {
            var settled = await residency.SettleHelperAsync(borrowed.Fence, () =>
                store.SettleDetachedCompletionAsync(lease, prepared.Result, borrowed.Fence,
                    new(idle.ResidentId, idle.ExecutionId, idle.Process, model, observed), prepared.UseNativeResult, token), token).ConfigureAwait(false);
            if (settled?.Settlement.Accepted == true)
            {
                RemoveStoredInput(worker, lease, prepared);
                residency.Notify();
                gpuWake.Notify(GpuSchedulerWakeReason.Reconciliation);
                if (await store.RequeueSettledAsync(lease.Handle, lease.MiniTaskId, token).ConfigureAwait(false))
                    outboxWake.Notify();
                return;
            }
            // Withdrawal is final for the transcript. Other rejected idle
            // settlements can follow the original physically released route.
            if (settled?.Settlement.Disposition == DocumentOcrLifecycleDisposition.SourceWithdrawn)
            {
                worker.RemovePendingTranscript(lease.ExecutionInstanceId);
                prepared = new(Failure("transcription-source-withdrawn"), false, false);
            }
        }

        var current = await store.ReadResidentBorrowAsync(lease, token).ConfigureAwait(false);
        if (current is not null)
        {
            if (!await ReleaseResidentAsync(store, residency, lease, current, token).ConfigureAwait(false)) return;
        }
        else if (!(await store.ConfirmCleanupAsync(lease, new(lease.ExecutionInstanceId, DocumentOcrCleanupKind.VerifiedUnstarted,
                     null, time.GetUtcNow()), token).ConfigureAwait(false)).Accepted)
            return;

        await StorePreparedResultAsync(store, worker, lease, prepared, token).ConfigureAwait(false);
        var pending = await store.ReadPendingCompletionAsync(lease.Handle, token).ConfigureAwait(false);
        if (pending is not null) await SettleAsync(store, lease.Handle, pending, token).ConfigureAwait(false);
    }

    private static async ValueTask<bool> PermitResidentStartAsync(SqlMediaTranscriptionGpuStore store, DocumentOcrExecutionLease lease,
        GpuResidentModelFence fence, OwnedWarmModelCommand command, CancellationToken ct) =>
        command.ResidentId == fence.ResidencyId && command.ExecutionId == lease.ExecutionInstanceId &&
        (await store.PermitResidentNativeStartAsync(lease, fence, command.Process, ct).ConfigureAwait(false)).Accepted;

    private async ValueTask<bool> ReleaseResidentAsync(SqlMediaTranscriptionGpuStore store, GpuModelResidencyRuntime residency,
        DocumentOcrExecutionLease lease, GpuResidentModelFence fence, CancellationToken ct)
    {
        if (fence.Helper is not { } helper || fence.Owner.MachineFingerprint != lease.Supervisor.Process.MachineFingerprint ||
            await store.ReadResidentBorrowAsync(lease, ct).ConfigureAwait(false) != fence ||
            !await residency.ConfirmHelperEmptyAsync(fence, ct).ConfigureAwait(false))
            return false;
        var process = new DocumentOcrProcessIdentity(helper.ProcessId, helper.StartedAtUtc, fence.Owner.MachineFingerprint);
        var released = await store.ConfirmResidentCleanupAsync(lease, fence,
            new(fence.ResidencyId, lease.ExecutionInstanceId, process, time.GetUtcNow()), ct).ConfigureAwait(false);
        if (released.Accepted) residency.Forget(fence.ResidencyId);
        return released.Accepted;
    }

    public async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SqlMediaTranscriptionGpuStore>();
        var residency = scope.ServiceProvider.GetService<GpuModelResidencyRuntime>();
        if (residency is not null)
        {
            foreach (var (handle, fence) in await store.ReadUnclaimedResidentsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (IsRunning(handle)) continue;
                var ownerExited = ownerProbe.Observe(fence.Owner) == GpuInteractiveOwnerObservation.Exited;
                await store.ClaimAbandonedResidentAsync(handle, fence, _supervisor, ownerExited, cancellationToken).ConfigureAwait(false);
            }
        }
        foreach (var candidate in await store.ReadRecoveryAsync(cancellationToken).ConfigureAwait(false))
        {
            var lease = candidate.Lease;
            if (IsRunning(lease.Handle)) continue;
            var pending = await store.ReadPendingCompletionAsync(lease.Handle, cancellationToken).ConfigureAwait(false);
            if (pending is { NativeCleanupConfirmed: false })
            {
                // Only the exact detached settlement receipt authorises requeue.
                await SettleAsync(store, lease.Handle, pending, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var borrow = await store.ReadResidentBorrowAsync(lease, cancellationToken).ConfigureAwait(false);
            if (borrow is not null)
            {
                if (residency is null || time.GetUtcNow() < lease.DeadlineAtUtc &&
                    ownerProbe.Observe(borrow.Owner) != GpuInteractiveOwnerObservation.Exited ||
                    !await ReleaseResidentAsync(store, residency, lease, borrow, cancellationToken).ConfigureAwait(false))
                    continue;
            }
            else if (!candidate.NativeCleanupConfirmed || !candidate.HasStoredResult)
            {
                var observation = await recovery.InspectAsync(lease, candidate.Process, candidate.HasStoredResult,
                    candidate.NativeCleanupConfirmed, cancellationToken).ConfigureAwait(false);
                // A live owner may be persisting a valid result after its process exited.
                if (observation.OwnerRunning) continue;
                if (!candidate.NativeCleanupConfirmed &&
                    (observation.Proof is not { } proof ||
                     !(await store.ConfirmCleanupAsync(lease, proof, cancellationToken).ConfigureAwait(false)).Accepted))
                    continue;
            }

            if (pending is null && !candidate.HasStoredResult)
            {
                // Read a result only after physical cleanup and only from this exact execution directory.
                var worker = scope.ServiceProvider.GetRequiredService<MediaTranscriptionLocalExecutor>();
                var result = (borrow is not null && candidate.Process is null ? null :
                    await worker.ReadRecoveredResultAsync(lease.ExecutionInstanceId, cancellationToken).ConfigureAwait(false))
                    ?? Failure("transcription-worker-interrupted");
                await StoreResultAsync(store, worker, lease, result, cancellationToken).ConfigureAwait(false);
                pending = await store.ReadPendingCompletionAsync(lease.Handle, cancellationToken).ConfigureAwait(false);
            }
            if (pending is not null)
                await SettleAsync(store, lease.Handle, pending, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsRunning(GpuExecutorBatchHandle handle) => _deliveries.TryGetValue(handle.DispatchId, out var delivery) &&
        delivery.IsValueCreated && !delivery.Value.IsCompleted;

    private async ValueTask SettleAsync(SqlMediaTranscriptionGpuStore store, GpuExecutorBatchHandle handle,
        MediaTranscriptionGpuCompletion completion, CancellationToken token)
    {
        if (completion.NativeCleanupConfirmed &&
            !(await store.SettleAsync(handle, completion.MiniTaskId, completion.ResultDigest, token).ConfigureAwait(false)).Accepted)
            return;
        var requeued = await store.RequeueSettledAsync(handle, completion.MiniTaskId, token).ConfigureAwait(false);
        if (requeued) outboxWake.Notify();
        if (completion.NativeCleanupConfirmed)
            gpuWake.Notify(GpuSchedulerWakeReason.CapacityReleased | GpuSchedulerWakeReason.Reconciliation);
        else if (requeued)
            gpuWake.Notify(GpuSchedulerWakeReason.Reconciliation);
    }

    private async Task HeartbeatAsync(SqlMediaTranscriptionGpuStore store, DocumentOcrExecutionLease lease, CancellationTokenSource run)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        try
        {
            while (await timer.WaitForNextTickAsync(run.Token).ConfigureAwait(false))
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
                budget.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if ((await store.HeartbeatAsync(lease, budget.Token).ConfigureAwait(false)).Accepted) continue;
                    run.Cancel();
                    return;
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                {
                    // Loss of a database connection is neither a release nor a positive ownership observation.
                }
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
    }

    private static async ValueTask StoreResultAsync(SqlMediaTranscriptionGpuStore store,
        MediaTranscriptionLocalExecutor worker, DocumentOcrExecutionLease lease, MediaTranscriptionResult result, CancellationToken token) =>
        await StorePreparedResultAsync(store, worker, lease,
            await PrepareResultAsync(worker, lease, result, token).ConfigureAwait(false), token).ConfigureAwait(false);

    private sealed record PreparedTranscript(MediaTranscriptionResult Result, bool UseNativeResult, bool PreserveEvidence);

    private static async ValueTask<PreparedTranscript> PrepareResultAsync(MediaTranscriptionLocalExecutor worker,
        DocumentOcrExecutionLease lease, MediaTranscriptionResult result, CancellationToken token)
    {
        var executionId = lease.ExecutionInstanceId;
        var useNativeResult = false;
        var preserveEvidence = false;
        if (result.Succeeded)
        {
            try { await worker.PersistTranscriptAsync(executionId, result.Transcript!, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                // The exact owned helper result is flushed before the child exits.
                // It is an eligible fallback only when the file still matches in full.
                useNativeResult = await worker.ReadRecoveredResultAsync(executionId, token).ConfigureAwait(false) == result;
                if (!useNativeResult)
                {
                    preserveEvidence = true;
                    result = Failure("transcription-result-file-unavailable");
                }
            }
        }

        return new(result, useNativeResult, preserveEvidence);
    }

    private static async ValueTask StorePreparedResultAsync(SqlMediaTranscriptionGpuStore store,
        MediaTranscriptionLocalExecutor worker, DocumentOcrExecutionLease lease, PreparedTranscript prepared, CancellationToken token)
    {
        var stored = await store.StoreResultAsync(lease, prepared.Result, token, prepared.UseNativeResult).ConfigureAwait(false);
        if (stored.Disposition == DocumentOcrLifecycleDisposition.SourceWithdrawn && prepared.Result.Succeeded)
        {
            prepared = new(Failure("transcription-source-withdrawn"), false, false);
            stored = await store.StoreResultAsync(lease, prepared.Result, token).ConfigureAwait(false);
            if (stored.Accepted) worker.RemovePendingTranscript(lease.ExecutionInstanceId);
        }
        if (stored.Accepted) RemoveStoredInput(worker, lease, prepared);
    }

    private static void RemoveStoredInput(MediaTranscriptionLocalExecutor worker, DocumentOcrExecutionLease lease, PreparedTranscript prepared)
    {
        if (!prepared.UseNativeResult && !prepared.PreserveEvidence)
            worker.RemoveSettledFiles(lease.ExecutionInstanceId);
    }

    private static MediaTranscriptionResult Failure(string reason) => new(false, reason, null, null);
}
