using System.Collections.Concurrent;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
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

        var executionId = Guid.NewGuid();
        var claim = await store.TryClaimExecutionAsync(handle, executionId, _supervisor, token).ConfigureAwait(false);
        if (!claim.Lifecycle.Accepted || claim.Lifecycle.Lease is not { } lease ||
            lease.ExecutionInstanceId != executionId || lease.Supervisor != _supervisor) return;
        var worker = scope.ServiceProvider.GetRequiredService<MediaTranscriptionLocalExecutor>();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        var remaining = lease.DeadlineAtUtc - time.GetUtcNow();
        run.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        var heartbeat = HeartbeatAsync(store, lease, run);
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

    public async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SqlMediaTranscriptionGpuStore>();
        foreach (var candidate in await store.ReadRecoveryAsync(cancellationToken).ConfigureAwait(false))
        {
            var lease = candidate.Lease;
            if (_deliveries.TryGetValue(lease.Handle.DispatchId, out var running) &&
                running.IsValueCreated && !running.Value.IsCompleted)
                continue;
            if (!candidate.NativeCleanupConfirmed || !candidate.HasStoredResult)
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

            var pending = await store.ReadPendingCompletionAsync(lease.Handle, cancellationToken).ConfigureAwait(false);
            if (pending is null && !candidate.HasStoredResult)
            {
                // Read a result only after physical cleanup and only from this exact execution directory.
                var worker = scope.ServiceProvider.GetRequiredService<MediaTranscriptionLocalExecutor>();
                var result = await worker.ReadRecoveredResultAsync(lease.ExecutionInstanceId, cancellationToken).ConfigureAwait(false)
                    ?? Failure("transcription-worker-interrupted");
                await StoreResultAsync(store, worker, lease, result, cancellationToken).ConfigureAwait(false);
                pending = await store.ReadPendingCompletionAsync(lease.Handle, cancellationToken).ConfigureAwait(false);
            }
            if (pending is not null)
                await SettleAsync(store, lease.Handle, pending, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask SettleAsync(SqlMediaTranscriptionGpuStore store, GpuExecutorBatchHandle handle,
        MediaTranscriptionGpuCompletion completion, CancellationToken token)
    {
        if (!completion.NativeCleanupConfirmed ||
            !(await store.SettleAsync(handle, completion.MiniTaskId, completion.ResultDigest, token).ConfigureAwait(false)).Accepted)
            return;
        if (await store.RequeueSettledAsync(handle, completion.MiniTaskId, token).ConfigureAwait(false))
            outboxWake.Notify();
        gpuWake.Notify(GpuSchedulerWakeReason.CapacityReleased | GpuSchedulerWakeReason.Reconciliation);
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
        MediaTranscriptionLocalExecutor worker, DocumentOcrExecutionLease lease, MediaTranscriptionResult result, CancellationToken token)
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

        var stored = await store.StoreResultAsync(lease, result, token, useNativeResult).ConfigureAwait(false);
        if (stored.Disposition == DocumentOcrLifecycleDisposition.SourceWithdrawn && result.Succeeded)
        {
            stored = await store.StoreResultAsync(lease, Failure("transcription-source-withdrawn"), token).ConfigureAwait(false);
            if (stored.Accepted) worker.RemovePendingTranscript(executionId);
            useNativeResult = false;
        }

        if (stored.Accepted && !useNativeResult && !preserveEvidence)
            worker.RemoveSettledFiles(executionId);
    }

    private static MediaTranscriptionResult Failure(string reason) => new(false, reason, null, null);
}
