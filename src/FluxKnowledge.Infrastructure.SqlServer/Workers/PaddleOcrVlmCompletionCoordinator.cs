using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
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

    public async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SqlDocumentOcrStore>();
        var candidates = await store.ReadRecoveryCandidatesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Lease is not { } lease)
            {
                await store.RecordBlockedAsync(candidate.Handle, candidate.MiniTaskId, "document-ocr-legacy-owner-unproven", cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (candidate.CleanupConfirmedAtUtc is null || !candidate.HasStoredResult)
            {
                if (candidate.Phase == DocumentOcrExecutionPhase.Blocked && candidate.CleanupConfirmedAtUtc is null) continue;
                var observation = processRecovery is null
                    ? new DocumentOcrRecoveryObservation(null, "document-ocr-cleanup-unproven",
                        !candidate.HasStoredResult && (timeProvider ?? TimeProvider.System).GetUtcNow() < lease.DeadlineAtUtc)
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
            var pending = await store.ReadPendingCompletionAsync(candidate.Handle, cancellationToken).ConfigureAwait(false);
            if (pending is null)
            {
                try
                {
                    var reason = (timeProvider ?? TimeProvider.System).GetUtcNow() >= lease.DeadlineAtUtc
                        ? "document-ocr-runtime-timeout" : "document-ocr-owner-interrupted";
                    var stored = await store.StoreResultAsync(lease, new(false, reason, []), cancellationToken).ConfigureAwait(false);
                    pending = new(candidate.MiniTaskId, stored.ResultDigest);
                }
                catch (InvalidOperationException)
                {
                    pending = await store.ReadPendingCompletionAsync(candidate.Handle, cancellationToken).ConfigureAwait(false);
                    if (pending is null)
                    {
                        var withdrawn = await store.ReleaseWithdrawnAsync(lease, cancellationToken).ConfigureAwait(false);
                        if (!withdrawn.Accepted)
                        {
                            var released = await store.ReleaseUnpublishableAsync(lease, "document-ocr-result-persistence-failed", cancellationToken).ConfigureAwait(false);
                            if (released.Disposition == DocumentOcrLifecycleDisposition.ResultAvailable)
                            {
                                pending = await store.ReadPendingCompletionAsync(candidate.Handle, cancellationToken).ConfigureAwait(false);
                                if (pending is not null) await CompleteAsync(candidate.Handle, pending, cancellationToken).ConfigureAwait(false);
                            }
                            else if (!released.Accepted) await store.RecordBlockedAsync(candidate.Handle, candidate.MiniTaskId,
                                released.ReasonCode, cancellationToken).ConfigureAwait(false);
                        }
                        continue;
                    }
                }
            }
            await CompleteAsync(candidate.Handle, pending, cancellationToken).ConfigureAwait(false);
        }
    }
}
