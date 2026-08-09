using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Coordinates persisted watcher hints; it never reads a source path or indexes content.</summary>
public sealed class SourceWatchCoordinator(ISourceRootWatchStore store, ISourceScanWakeSignal? wakeSignal = null)
{
    private readonly string _leaseOwner = $"source-watch:{Guid.NewGuid():N}";

    public async ValueTask RecordAsync(SourceWatchSignal signal, CancellationToken cancellationToken)
    {
        await store.RecordSignalAsync(signal, cancellationToken).ConfigureAwait(false);
        wakeSignal?.Notify();
    }

    public async ValueTask<int> ReleaseDueAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var released = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await store.ClaimDueBatchAsync(nowUtc, _leaseOwner, leaseDuration, cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                return released;
            }

            await store.ReleaseScanAsync(batch, cancellationToken).ConfigureAwait(false);
            wakeSignal?.Notify();
            released++;
        }

        return released;
    }
}
