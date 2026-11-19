using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>
/// Completes the existing durable GPU lifecycle only after local OCR output is stored. It then
/// requeues the original Extract message; it never creates a corpus entry itself.
/// </summary>
public sealed class PaddleOcrVlmCompletionCoordinator(
    IServiceScopeFactory scopeFactory,
    IOutboxWakeSignal outboxWakeSignal,
    IDocumentOcrProcessRecovery? processRecovery = null,
    TimeProvider? timeProvider = null)
{
    public async ValueTask CompleteAsync(
        GpuExecutorBatchHandle handle,
        DocumentOcrPendingCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(completion);
        if (!string.Equals(handle.ExecutorKey, PaddleOcrVlmRuntimeContract.ExecutorKey, StringComparison.Ordinal))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SqlDocumentOcrStore>();
        var settlement = await store.SettleCompletionAsync(handle, completion.MiniTaskId,
            completion.ResultDigest, cancellationToken).ConfigureAwait(false);
        if (!settlement.Accepted)
        {
            if (settlement.Disposition == DocumentOcrLifecycleDisposition.InvalidResult)
            {
                var released = await store.ReleaseCorruptResultAsync(handle, completion.MiniTaskId,
                    completion.ResultDigest, cancellationToken).ConfigureAwait(false);
                if (released.Accepted) return;
            }
            await store.RecordBlockedAsync(handle, completion.MiniTaskId, settlement.ReasonCode, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (await store.RequeueCompletedAsync(handle, completion.MiniTaskId, cancellationToken).ConfigureAwait(false))
        {
            outboxWakeSignal.Notify();
        }
    }

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private static readonly Guid RecoverySupervisorId = Guid.NewGuid();

    internal async ValueTask<bool> ConfirmResidentCleanupAsync(SqlDocumentOcrStore store,
        GpuModelResidencyRuntime residency, DocumentOcrExecutionLease lease, GpuResidentModelFence borrow, CancellationToken ct)
    {
        if (borrow.Helper is not { } helper ||
            await store.ReadResidentBorrowAsync(lease.Handle, ct).ConfigureAwait(false) != borrow ||
            !await residency.ConfirmHelperEmptyAsync(borrow, ct).ConfigureAwait(false))
            return false;

        var process = new DocumentOcrProcessIdentity(helper.ProcessId, helper.StartedAtUtc, borrow.Owner.MachineFingerprint);
        var proof = new DocumentOcrResidentReleaseProof(borrow.ResidencyId, lease.ExecutionInstanceId, process, _clock.GetUtcNow());
        DocumentOcrLifecycleResult confirmed;
        try { confirmed = await store.ConfirmResidentCleanupAsync(lease, borrow, proof, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException && !ct.IsCancellationRequested)
        { confirmed = await store.ConfirmResidentCleanupAsync(lease, borrow, proof, ct).ConfigureAwait(false); }
        if (!confirmed.Accepted) return false;
        residency.Forget(borrow.ResidencyId);
        return true;
    }

    internal async ValueTask RecoverUnclaimedResidentAsync(SqlDocumentOcrStore store, GpuModelResidencyRuntime residency,
        GpuExecutorBatchHandle handle, GpuResidentModelFence borrow, DocumentOcrSupervisorIdentity supervisor,
        bool ownerExited, CancellationToken ct)
    {
        var claim = await store.ClaimAbandonedResidentAsync(handle, borrow, supervisor, ownerExited, ct).ConfigureAwait(false);
        if (!claim.Accepted || claim.Lease is not { } lease) return;
        if (!await ConfirmResidentCleanupAsync(store, residency, lease, borrow, ct).ConfigureAwait(false))
            return;
        await ResumeAfterCleanupAsync(store, lease, ct).ConfigureAwait(false);
    }

    public async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SqlDocumentOcrStore>();
        var residency = scope.ServiceProvider.GetService<GpuModelResidencyRuntime>();
        var probe = residency is null ? null : scope.ServiceProvider.GetService<IGpuInteractiveOwnerProbe>() ?? new WindowsInteractiveGpuOwnerProbe();
        var candidates = await store.ReadRecoveryCandidatesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Detached)
            {
                residency?.Notify();
                scope.ServiceProvider.GetService<IGpuSchedulerWakeSignal>()?.Notify(GpuSchedulerWakeReason.Reconciliation);
                var saved = await store.ReadPendingCompletionAsync(candidate.Handle, cancellationToken).ConfigureAwait(false);
                if (saved is not null) await CompleteAsync(candidate.Handle, saved, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (candidate.Lease is not { } lease)
            {
                if (candidate.ResidentBorrow is { } assigned && residency is not null && probe is not null)
                {
                    await RecoverUnclaimedResidentAsync(store, residency, candidate.Handle, assigned,
                        new(RecoverySupervisorId, probe.Current), probe.Observe(assigned.Owner) == GpuInteractiveOwnerObservation.Exited,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }
                await store.RecordBlockedAsync(candidate.Handle, candidate.MiniTaskId, "document-ocr-legacy-owner-unproven", cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (candidate.ResidentBorrow is { } borrow)
            {
                var cleanupOnly = candidate.Phase == DocumentOcrExecutionPhase.Blocked && candidate.Process is null &&
                    candidate.Reason is "document-ocr-resident-source-withdrawn" or "document-ocr-resident-owner-exited";
                if (!cleanupOnly && _clock.GetUtcNow() < lease.DeadlineAtUtc &&
                    probe?.Observe(borrow.Owner) != GpuInteractiveOwnerObservation.Exited)
                    continue;
                if (residency is null ||
                    !await ConfirmResidentCleanupAsync(store, residency, lease, borrow, cancellationToken).ConfigureAwait(false))
                {
                    if (!cleanupOnly)
                        await store.RecordBlockedAsync(candidate.Handle, candidate.MiniTaskId,
                            "document-ocr-resident-cleanup-unproven", cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            else if (candidate.CleanupConfirmedAtUtc is null || !candidate.HasStoredResult)
            {
                if (candidate.Phase == DocumentOcrExecutionPhase.Blocked && candidate.CleanupConfirmedAtUtc is null) continue;
                var observation = processRecovery is null
                    ? new DocumentOcrRecoveryObservation(null, "document-ocr-cleanup-unproven",
                        !candidate.HasStoredResult && _clock.GetUtcNow() < lease.DeadlineAtUtc)
                    : await processRecovery.InspectAsync(lease, candidate.Process, candidate.HasStoredResult,
                        candidate.CleanupConfirmedAtUtc is not null, cancellationToken).ConfigureAwait(false);
                if (observation.OwnerRunning) continue;
                if (candidate.CleanupConfirmedAtUtc is null && observation.Proof is null)
                {
                    await store.RecordBlockedAsync(candidate.Handle, candidate.MiniTaskId, observation.ReasonCode, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (candidate.CleanupConfirmedAtUtc is null && observation.Proof is { } cleanupProof)
                {
                    var proof = await store.ConfirmCleanupAsync(lease, cleanupProof, cancellationToken).ConfigureAwait(false);
                    if (!proof.Accepted)
                    {
                        await store.RecordBlockedAsync(candidate.Handle, candidate.MiniTaskId, proof.ReasonCode, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
            }
            await ResumeAfterCleanupAsync(store, lease, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ResumeAfterCleanupAsync(SqlDocumentOcrStore store, DocumentOcrExecutionLease lease,
        CancellationToken cancellationToken)
    {
        var pending = await store.ReadPendingCompletionAsync(lease.Handle, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            try
            {
                var reason = _clock.GetUtcNow() >= lease.DeadlineAtUtc
                    ? "document-ocr-runtime-timeout" : "document-ocr-owner-interrupted";
                var stored = await store.StoreResultAsync(lease, new(false, reason, []), cancellationToken).ConfigureAwait(false);
                pending = new(lease.MiniTaskId, stored.ResultDigest);
            }
            catch (InvalidOperationException)
            {
                pending = await store.ReadPendingCompletionAsync(lease.Handle, cancellationToken).ConfigureAwait(false);
                if (pending is null)
                {
                    var withdrawn = await store.ReleaseWithdrawnAsync(lease, cancellationToken).ConfigureAwait(false);
                    if (!withdrawn.Accepted)
                    {
                        var released = await store.ReleaseUnpublishableAsync(lease, "document-ocr-result-persistence-failed", cancellationToken).ConfigureAwait(false);
                        if (released.Disposition == DocumentOcrLifecycleDisposition.ResultAvailable)
                        {
                            pending = await store.ReadPendingCompletionAsync(lease.Handle, cancellationToken).ConfigureAwait(false);
                            if (pending is not null) await CompleteAsync(lease.Handle, pending, cancellationToken).ConfigureAwait(false);
                        }
                        else if (!released.Accepted) await store.RecordBlockedAsync(lease.Handle, lease.MiniTaskId,
                            released.ReasonCode, cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
            }
        }
        await CompleteAsync(lease.Handle, pending, cancellationToken).ConfigureAwait(false);
    }
}
