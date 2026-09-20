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
    IOutboxWakeSignal outboxWakeSignal)
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
        var lifecycle = scope.ServiceProvider.GetRequiredService<IGpuExecutorLifecycleSink>();
        var store = scope.ServiceProvider.GetRequiredService<SqlDocumentOcrStore>();
        _ = await lifecycle.AcknowledgeAsync(
                new GpuExecutorAcknowledgement(Guid.NewGuid(), handle),
                cancellationToken)
            .ConfigureAwait(false);
        _ = await lifecycle.RecordReceiptAsync(
                new GpuExecutorResultReceipt(
                    Guid.NewGuid(),
                    handle,
                    completion.MiniTaskId,
                    GpuMiniTaskBoundaryDisposition.Completed,
                    completion.ResultDigest,
                    GpuExecutorEvidenceClass.TaskOutcomeConfirmed),
                cancellationToken)
            .ConfigureAwait(false);
        _ = await lifecycle.HandleCallbackAsync(
                Guid.NewGuid(),
                new GpuBatchCallback(
                    handle,
                    GpuBatchCallbackKind.Completed,
                    [new GpuMiniTaskBoundaryOutcome(
                        completion.MiniTaskId,
                        GpuMiniTaskBoundaryDisposition.Completed)],
                    CapacityReleased: true),
                cancellationToken)
            .ConfigureAwait(false);
        if (await store.RequeueCompletedAsync(handle, completion.MiniTaskId, cancellationToken).ConfigureAwait(false))
        {
            outboxWakeSignal.Notify();
        }
    }

    public async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SqlDocumentOcrStore>();
        var pending = await store.ReadPendingCompletionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var completion in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CompleteAsync(
                    completion.Handle,
                    new DocumentOcrPendingCompletion(completion.MiniTaskId, completion.ResultDigest),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
