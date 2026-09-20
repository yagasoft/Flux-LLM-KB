using System.Collections.Concurrent;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using Microsoft.Extensions.DependencyInjection;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>
/// Tracks only this process's in-flight local OCR executions so source deletion can request a
/// cooperative stop. It is not a scheduler or a cross-process ownership claim.
/// </summary>
public sealed class PaddleOcrVlmExecutionRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _executions = new();

    public PaddleOcrVlmExecutionLease Begin(Guid miniTaskId)
    {
        if (miniTaskId == Guid.Empty)
        {
            throw new ArgumentException("An OCR execution requires a mini-task ID.", nameof(miniTaskId));
        }

        var cancellation = new CancellationTokenSource();
        if (!_executions.TryAdd(miniTaskId, cancellation))
        {
            cancellation.Dispose();
            throw new InvalidOperationException("document-ocr-execution-already-active");
        }

        return new PaddleOcrVlmExecutionLease(this, miniTaskId, cancellation);
    }

    public bool IsRunning(Guid miniTaskId) => _executions.ContainsKey(miniTaskId);

    public bool RequestCancellation(Guid miniTaskId)
    {
        if (!_executions.TryGetValue(miniTaskId, out var cancellation))
        {
            return false;
        }

        cancellation.Cancel();
        return true;
    }

    private void End(Guid miniTaskId, CancellationTokenSource cancellation)
    {
        _executions.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(miniTaskId, cancellation));
        cancellation.Dispose();
    }

    public sealed class PaddleOcrVlmExecutionLease : IDisposable
    {
        private readonly PaddleOcrVlmExecutionRegistry _owner;
        private readonly Guid _miniTaskId;
        private CancellationTokenSource? _cancellation;

        internal PaddleOcrVlmExecutionLease(
            PaddleOcrVlmExecutionRegistry owner,
            Guid miniTaskId,
            CancellationTokenSource cancellation)
        {
            _owner = owner;
            _miniTaskId = miniTaskId;
            _cancellation = cancellation;
        }

        public CancellationToken Token => _cancellation?.Token ?? throw new ObjectDisposedException(nameof(PaddleOcrVlmExecutionLease));

        public bool IsCancellationRequested => _cancellation?.IsCancellationRequested == true;

        public void Dispose()
        {
            var cancellation = Interlocked.Exchange(ref _cancellation, null);
            if (cancellation is not null)
            {
                _owner.End(_miniTaskId, cancellation);
            }
        }
    }
}

/// <summary>
/// A deletion-requested local OCR process has no trustworthy extraction outcome. Release only
/// its exact one-task batch as outcome-uncertain; the fenced source deletion then owns cleanup.
/// </summary>
public sealed class PaddleOcrVlmCancellationCoordinator(IServiceScopeFactory scopeFactory)
{
    public async ValueTask ReleaseAsCancelledAsync(
        GpuExecutorBatchHandle handle,
        Guid miniTaskId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        if (miniTaskId == Guid.Empty ||
            !string.Equals(handle.ExecutorKey, PaddleOcrVlmRuntimeContract.ExecutorKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("document-ocr-cancellation-handle-invalid");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IGpuExecutorLifecycleSink>();
        _ = await lifecycle.AcknowledgeAsync(
                new GpuExecutorAcknowledgement(Guid.NewGuid(), handle),
                cancellationToken)
            .ConfigureAwait(false);
        var released = await lifecycle.HandleCallbackAsync(
                Guid.NewGuid(),
                new GpuBatchCallback(
                    handle,
                    GpuBatchCallbackKind.CapacityReleased,
                    [new GpuMiniTaskBoundaryOutcome(miniTaskId, GpuMiniTaskBoundaryDisposition.OutcomeUncertain)],
                    CapacityReleased: true),
                cancellationToken)
            .ConfigureAwait(false);
        if (!released.Accepted)
        {
            throw new InvalidOperationException("document-ocr-cancellation-release-rejected");
        }
    }
}
