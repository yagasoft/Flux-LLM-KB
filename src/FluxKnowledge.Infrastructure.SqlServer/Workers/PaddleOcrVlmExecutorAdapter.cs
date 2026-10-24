using System.Collections.Concurrent;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
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
        var work = await store.ReadExecutionWorkAsync(handle, GpuExecutorDispatchState.PendingDelivery, CancellationToken.None).ConfigureAwait(false);
        if (work is null) return;
        // This invocation generates the identity once and has not launched anything yet. A
        // lost commit response may be read back with that same identity; redelivery has a new one.
        var executionId = Guid.NewGuid();
        using var claimBudget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        DocumentOcrLifecycleResult claimed;
        try { claimed = await store.TryClaimExecutionAsync(handle, work.MiniTaskId, executionId, _supervisor, claimBudget.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException && !claimBudget.IsCancellationRequested)
        { claimed = await store.TryClaimExecutionAsync(handle, work.MiniTaskId, executionId, _supervisor, claimBudget.Token).ConfigureAwait(false); }
        if (!claimed.Accepted || claimed.Lease is not { } lease || lease.ExecutionInstanceId != executionId || lease.Supervisor != _supervisor) return;
        using var execution = executionRegistry.Begin(work.MiniTaskId);
        var remaining = lease.DeadlineAtUtc - _clock.GetUtcNow();
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _clock);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(execution.Token, applicationLifetime.ApplicationStopping, deadline.Token);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        var heartbeat = HeartbeatAsync(store, lease, heartbeatStop.Token, run);
        DocumentOcrExecutionResult result;
        try
        {
            var retained = await scope.ServiceProvider.GetRequiredService<IRetainedSourceReader>()
                .ReadBytesAsync(work.RetainedSourceRevisionId, run.Token).ConfigureAwait(false);
            result = retained.ContentSha256 != work.ContentSha256
                ? Refused("document-ocr-source-content-mismatch")
                : await scope.ServiceProvider.GetRequiredService<IDocumentOcrExecutor>()
                    .ExecuteAsync(retained, work.PageIndexes, new(lease, store), run.Token).ConfigureAwait(false);
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
        // Purely managed refusals and synthetic executors never received native start permission.
        // The SQL phase check rejects this proof for any native-permitted execution.
        var unstartedProof = new DocumentOcrCleanupProof(lease.ExecutionInstanceId,
            DocumentOcrCleanupKind.VerifiedUnstarted, null, _clock.GetUtcNow());
        try { await store.ConfirmCleanupAsync(lease, unstartedProof, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException && !ct.IsCancellationRequested)
        { await store.ConfirmCleanupAsync(lease, unstartedProof, ct).ConfigureAwait(false); }
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
