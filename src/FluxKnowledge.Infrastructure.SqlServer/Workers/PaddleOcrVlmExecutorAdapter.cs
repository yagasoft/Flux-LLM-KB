using System.Collections.Concurrent;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>
/// The one in-process local executor admitted for PaddleOCR-VL. Repeated delivery prompts share
/// one execution per durable dispatch, while a process restart can safely resume from a stored
/// result through the completion coordinator.
/// </summary>
public sealed class PaddleOcrVlmExecutorAdapter(
    IServiceScopeFactory scopeFactory,
    PaddleOcrVlmCompletionCoordinator completionCoordinator,
    PaddleOcrVlmExecutionRegistry executionRegistry,
    PaddleOcrVlmCancellationCoordinator cancellationCoordinator,
    IHostApplicationLifetime applicationLifetime) : IGpuExecutorAdapter
{
    private readonly ConcurrentDictionary<Guid, Task> _deliveries = new();

    public string ExecutorKey => PaddleOcrVlmRuntimeContract.ExecutorKey;

    public ValueTask DeliverAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        if (!string.Equals(handle.ExecutorKey, ExecutorKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("document-ocr-executor-key-invalid");
        }

        var delivery = _deliveries.GetOrAdd(handle.DispatchId, _ => DeliverCoreAsync(handle));
        return AwaitDeliveryAsync(handle.DispatchId, delivery, cancellationToken);
    }

    private async ValueTask AwaitDeliveryAsync(Guid dispatchId, Task delivery, CancellationToken cancellationToken)
    {
        try
        {
            await delivery.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (delivery.IsCompleted)
            {
                _deliveries.TryRemove(new KeyValuePair<Guid, Task>(dispatchId, delivery));
            }
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

        // The first read avoids claiming a source that has already been paused or deleted. The
        // acknowledgement itself is the cross-process execution claim; a second read closes the
        // source-state race before any retained content is opened.
        var preflightWork = await store.ReadExecutionWorkAsync(
                handle,
                GpuExecutorDispatchState.PendingDelivery,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (preflightWork is null)
        {
            return;
        }

        var lifecycle = scope.ServiceProvider.GetRequiredService<IGpuExecutorLifecycleSink>();
        var acknowledgement = await lifecycle.AcknowledgeAsync(
                new GpuExecutorAcknowledgement(Guid.NewGuid(), handle),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!acknowledgement.Accepted || !acknowledgement.Committed)
        {
            return;
        }

        var work = await store.ReadExecutionWorkAsync(
                handle,
                GpuExecutorDispatchState.Acknowledged,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (work is null)
        {
            await cancellationCoordinator.ReleaseAsCancelledAsync(
                    handle,
                    preflightWork.MiniTaskId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        using var execution = executionRegistry.Begin(work.MiniTaskId);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            execution.Token,
            applicationLifetime.ApplicationStopping);
        DocumentOcrExecutionResult result;
        try
        {
            result = await ExecuteAsync(scope.ServiceProvider, work, executionCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
            await cancellationCoordinator.ReleaseAsCancelledAsync(handle, work.MiniTaskId, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        if (executionCancellation.IsCancellationRequested)
        {
            await cancellationCoordinator.ReleaseAsCancelledAsync(handle, work.MiniTaskId, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        DocumentOcrStoredResult stored;
        try
        {
            stored = await store.StoreResultAsync(handle, work.MiniTaskId, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Inference has ended. An ambiguous commit may already have saved the result, so
            // recover that receipt before considering uncertain settlement. If SQL cannot
            // establish either outcome, retain the reservation rather than guess.
            var persisted = await store.ReadPendingCompletionAsync(handle, CancellationToken.None).ConfigureAwait(false);
            if (persisted is not null)
            {
                await completionCoordinator.CompleteAsync(handle, persisted, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await cancellationCoordinator.ReleaseAsCancelledAsync(handle, work.MiniTaskId, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        await completionCoordinator.CompleteAsync(
                handle,
                new DocumentOcrPendingCompletion(work.MiniTaskId, stored.ResultDigest),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async ValueTask<DocumentOcrExecutionResult> ExecuteAsync(
        IServiceProvider services,
        DocumentOcrExecutionWork work,
        CancellationToken cancellationToken)
    {
        try
        {
            var retained = await services.GetRequiredService<IRetainedSourceReader>()
                .ReadBytesAsync(work.RetainedSourceRevisionId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(retained.ContentSha256, work.ContentSha256, StringComparison.Ordinal))
            {
                return Refused("document-ocr-source-content-mismatch");
            }

            return await services.GetRequiredService<IDocumentOcrExecutor>()
                .ExecuteAsync(retained, work.PageIndexes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("document-ocr-", StringComparison.Ordinal))
        {
            return Refused(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Refused("document-ocr-retained-source-invalid");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Refused("document-ocr-inference-failed");
        }
    }

    private static DocumentOcrExecutionResult Refused(string reasonCode) =>
        new(false, reasonCode, []);
}
