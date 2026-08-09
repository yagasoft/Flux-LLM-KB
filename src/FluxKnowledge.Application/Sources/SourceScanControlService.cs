using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

public sealed class SourceScanControlService(
    ISourceRootStore rootStore,
    IOutboxWakeSignal wakeSignal,
    ISourceScanWakeSignal? sourceWakeSignal = null)
{
    public async ValueTask<bool> ReleaseAsync(
        SourceRootId sourceRootId,
        SourceScanRequestId sourceScanRequestId,
        string actor,
        CancellationToken cancellationToken)
    {
        var released = await rootStore.ReleaseAsync(
                sourceRootId,
                sourceScanRequestId,
                actor,
                cancellationToken)
            .ConfigureAwait(false);
        // A retry may observe an already-released durable request after an uncertain
        // commit. A duplicate local wake is safe; omitting the wake can strand work.
        wakeSignal.Notify();
        sourceWakeSignal?.Notify();

        return released;
    }
}
