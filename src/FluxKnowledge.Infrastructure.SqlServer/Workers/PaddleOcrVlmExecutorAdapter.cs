using System.Collections.Concurrent;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Documents;
using FluxKnowledge.Integrations.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>One exact durable OCR claim owns native execution; duplicate prompts only recover its state.</summary>
public sealed class PaddleOcrVlmExecutorAdapter(
    IServiceScopeFactory scopeFactory,
    PaddleOcrVlmCompletionCoordinator completionCoordinator,
    PaddleOcrVlmExecutionRegistry executionRegistry,
    IHostApplicationLifetime applicationLifetime,
    TimeProvider? timeProvider = null,
    DocumentOcrSupervisorIdentity? supervisor = null) : IGpuExecutorAdapter
{
    private static readonly Guid HostInstance = Guid.NewGuid();
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _deliveries = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly DocumentOcrSupervisorIdentity _supervisor = supervisor ?? new(HostInstance, new WindowsInteractiveGpuOwnerProbe().Current);
    public string ExecutorKey => PaddleOcrVlmRuntimeContract.ExecutorKey;

    public ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
    {
        handle.Validate();
        if (handle.ExecutorKey != ExecutorKey) throw new InvalidOperationException("document-ocr-executor-key-invalid");
        var delivery = _deliveries.GetOrAdd(handle.DispatchId, _ => new(() => DeliverCoreAsync(handle), LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitDeliveryAsync(handle.DispatchId, delivery, cancellationToken);
    }

    private async ValueTask AwaitDeliveryAsync(Guid dispatchId, Lazy<Task> delivery, CancellationToken ct)
    {
        try { await delivery.Value.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            if (delivery.IsValueCreated && delivery.Value.IsCompleted)
                _deliveries.TryRemove(new KeyValuePair<Guid, Lazy<Task>>(dispatchId, delivery));
        }
    }

    private async Task DeliverCoreAsync(GpuExecutorBatchHandle handle)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SqlDocumentOcrStore>();
        var pending = await store.ReadPendingCompletionAsync(handle, CancellationToken.None).ConfigureAwait(false);
        if (pending is not null)
        {
            await completionCoordinator.CompleteAsync(handle, pending, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        using var claimBudget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var preassigned = await store.ReadResidentBorrowAsync(handle, claimBudget.Token).ConfigureAwait(false);
        var residency = scope.ServiceProvider.GetService<GpuModelResidencyRuntime>();
        var work = await store.ReadExecutionWorkAsync(handle, GpuExecutorDispatchState.PendingDelivery, claimBudget.Token).ConfigureAwait(false);
        if (work is null)
        {
            if (preassigned is not null && residency is not null)
                await completionCoordinator.RecoverUnclaimedResidentAsync(store, residency, handle, preassigned,
                    _supervisor, ownerExited: false, claimBudget.Token).ConfigureAwait(false);
            return;
        }
        var executionId = preassigned is null ? Guid.NewGuid() : handle.DispatchId;
        DocumentOcrLifecycleResult claimed;
        try { claimed = await store.TryClaimExecutionAsync(handle, work.MiniTaskId, executionId, _supervisor, claimBudget.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException && !claimBudget.IsCancellationRequested)
        { claimed = await store.TryClaimExecutionAsync(handle, work.MiniTaskId, executionId, _supervisor, claimBudget.Token).ConfigureAwait(false); }
        if (claimed.Disposition == DocumentOcrLifecycleDisposition.SourceWithdrawn && preassigned is not null && residency is not null)
        {
            await completionCoordinator.RecoverUnclaimedResidentAsync(store, residency, handle, preassigned,
                _supervisor, ownerExited: false, claimBudget.Token).ConfigureAwait(false);
            return;
        }
        if (!claimed.Accepted || claimed.Lease is not { } lease || lease.ExecutionInstanceId != executionId || lease.Supervisor != _supervisor) return;
        using var execution = executionRegistry.Begin(work.MiniTaskId);
        var remaining = lease.DeadlineAtUtc - _clock.GetUtcNow();
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _clock);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(execution.Token, applicationLifetime.ApplicationStopping, deadline.Token);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        var heartbeat = HeartbeatAsync(store, lease, heartbeatStop.Token, run);
        DocumentOcrExecutionResult result;
        GpuModelResidencyRuntime.NativeBorrow? nativeBorrow = null;
        DocumentOcrResidentIdleProof? idle = null;
        var warmAttempted = false;
        try
        {
            var retained = await scope.ServiceProvider.GetRequiredService<IRetainedSourceReader>()
                .ReadBytesAsync(work.RetainedSourceRevisionId, run.Token).ConfigureAwait(false);
            var executor = scope.ServiceProvider.GetRequiredService<IDocumentOcrExecutor>();
            var model = SqlDocumentOcrStore.ResidentModel;
            if (retained.ContentSha256 != work.ContentSha256)
                result = Refused("document-ocr-source-content-mismatch");
            else if (executor is PaddleOcrVlmLocalExecutor native && residency is not null &&
                (preassigned is not null || residency.EntryModel(model.RuntimeKey, model.SettingsFingerprint) == model))
            {
                warmAttempted = true;
                nativeBorrow = await residency.OpenHelperAsync(handle, executionId, model, native.StartWarmProcessAsync, run.Token)
                    .ConfigureAwait(false);
                var fence = nativeBorrow.Fence;
                var warm = await native.ExecuteWarmAsync(retained, work.PageIndexes, new(lease, store), nativeBorrow.Process,
                    async (claim, command, token) => command.ResidentId == fence.ResidencyId &&
                        command.ExecutionId == claim.ExecutionInstanceId &&
                        (await store.PermitResidentNativeStartAsync(claim, fence, command.Process, token).ConfigureAwait(false)).Accepted,
                    run.Token).ConfigureAwait(false);
                result = deadline.IsCancellationRequested && warm.Result.ReasonCode == "document-ocr-owner-interrupted"
                    ? Refused("document-ocr-runtime-timeout") : warm.Result;
                if (warm.Idle is { Model: { } modelKey } acknowledgement)
                    idle = new(acknowledgement.ResidentId, acknowledgement.ExecutionId, acknowledgement.Process, modelKey, _clock.GetUtcNow());
            }
            else if (preassigned is not null)
                result = Refused("document-ocr-resident-helper-unavailable");
            else
                result = await executor.ExecuteAsync(retained, work.PageIndexes, new(lease, store), run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            result = Refused(deadline.IsCancellationRequested ? "document-ocr-runtime-timeout" : "document-ocr-owner-interrupted");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            result = Refused("document-ocr-inference-failed");
        }
        finally
        {
            heartbeatStop.Cancel();
            try { await heartbeat.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { _ = heartbeat.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted); }
        }
        using var settlementBudget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = settlementBudget.Token;
        var borrow = nativeBorrow?.Fence ?? preassigned;
        if (borrow is null && warmAttempted)
            borrow = await store.ReadResidentBorrowAsync(handle, ct).ConfigureAwait(false);
        if (borrow is not null)
        {
            if (idle is not null && residency is not null &&
                await TryParkAsync(scope.ServiceProvider, store, residency, lease, borrow, result, idle, ct).ConfigureAwait(false))
                return;
            if (residency is null ||
                !await completionCoordinator.ConfirmResidentCleanupAsync(store, residency, lease, borrow, ct).ConfigureAwait(false))
            {
                await store.RecordBlockedAsync(handle, lease.MiniTaskId, "document-ocr-resident-cleanup-unproven", ct)
                    .ConfigureAwait(false);
                return;
            }
        }
        else
        {
            // The SQL phase and residency checks reject this proof after native start permission.
            var proof = new DocumentOcrCleanupProof(lease.ExecutionInstanceId,
                DocumentOcrCleanupKind.VerifiedUnstarted, null, _clock.GetUtcNow());
            try { await store.ConfirmCleanupAsync(lease, proof, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException && !ct.IsCancellationRequested)
            { await store.ConfirmCleanupAsync(lease, proof, ct).ConfigureAwait(false); }
        }
        try
        {
            var stored = await store.StoreResultAsync(lease, result, ct).ConfigureAwait(false);
            await completionCoordinator.CompleteAsync(handle, new(work.MiniTaskId, stored.ResultDigest), ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            var saved = await store.ReadPendingCompletionAsync(handle, ct).ConfigureAwait(false);
            if (saved is not null) await completionCoordinator.CompleteAsync(handle, saved, ct).ConfigureAwait(false);
            else
            {
                var withdrawn = await store.ReleaseWithdrawnAsync(lease, ct).ConfigureAwait(false);
                if (!withdrawn.Accepted)
                {
                    var released = await store.ReleaseUnpublishableAsync(lease, "document-ocr-result-persistence-failed", ct).ConfigureAwait(false);
                    if (released.Disposition == DocumentOcrLifecycleDisposition.ResultAvailable)
                    {
                        saved = await store.ReadPendingCompletionAsync(handle, ct).ConfigureAwait(false);
                        if (saved is not null)
                        {
                            await completionCoordinator.CompleteAsync(handle, saved, ct).ConfigureAwait(false);
                            return;
                        }
                    }
                    if (!released.Accepted) await store.RecordBlockedAsync(handle, lease.MiniTaskId, released.ReasonCode, ct).ConfigureAwait(false);
                }
                throw;
            }
        }
    }

    private async ValueTask<bool> TryParkAsync(IServiceProvider services, SqlDocumentOcrStore store,
        GpuModelResidencyRuntime residency, DocumentOcrExecutionLease lease, GpuResidentModelFence borrow,
        DocumentOcrExecutionResult result, DocumentOcrResidentIdleProof idle, CancellationToken ct)
    {
        DocumentOcrDetachedSettlement? parked;
        try
        {
            parked = await residency.SettleHelperAsync(borrow,
                () => store.SettleDetachedCompletionAsync(lease, result, borrow, idle, ct), ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException && !ct.IsCancellationRequested)
        {
            // Keep the exact observation and execution identity after an ambiguous commit.
            parked = await residency.SettleHelperAsync(borrow,
                () => store.SettleDetachedCompletionAsync(lease, result, borrow, idle, ct), ct).ConfigureAwait(false);
        }
        if (parked?.Settlement.Accepted != true) return false;

        residency.Notify();
        services.GetService<IGpuSchedulerWakeSignal>()?.Notify(GpuSchedulerWakeReason.Reconciliation);
        var pending = await store.ReadPendingCompletionAsync(lease.Handle, ct).ConfigureAwait(false);
        if (pending is not null)
            await completionCoordinator.CompleteAsync(lease.Handle, pending, ct).ConfigureAwait(false);
        return true;
    }

    private async Task HeartbeatAsync(SqlDocumentOcrStore store, DocumentOcrExecutionLease lease, CancellationToken ct, CancellationTokenSource run)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var result = await store.HeartbeatAsync(lease, ct).ConfigureAwait(false);
                    if (!result.Accepted) { run.Cancel(); return; }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                {
                    // SQL unavailability proves neither ownership loss nor capacity release.
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private static DocumentOcrExecutionResult Refused(string reason) => new(false, reason, []);
}
