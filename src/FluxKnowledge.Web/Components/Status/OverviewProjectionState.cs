using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.Web.Components.Status;

public sealed class OverviewProjectionState(IProjectionReader reader)
{
    public OverviewProjection Current { get; private set; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        "none",
        new IndexRecoverySummary("Starting", null, null, null, null, 0))
    {
        GpuSchedulerStatus = GpuSchedulerStatusProjection.Empty
    };

    public async ValueTask ReloadAsync(CancellationToken cancellationToken) =>
        Current = await reader.ReadOverviewAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<StatusEventSubscription> SubscribeAndReloadAsync(
        StatusEventFeed statusEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statusEvents);

        var subscription = statusEvents.Subscribe();
        try
        {
            await ReloadAsync(cancellationToken).ConfigureAwait(false);
            return subscription;
        }
        catch
        {
            await subscription.DisposeAsync();
            throw;
        }
    }

    public ValueTask HandleStatusChangedAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statusChanged);
        return string.Equals(statusChanged.Projection, "pipeline", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(statusChanged.Projection, "index-recovery", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(statusChanged.Projection, "gpu-scheduler", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(statusChanged.Projection, "reconnect", StringComparison.OrdinalIgnoreCase)
            ? ReloadAsync(cancellationToken)
            : ValueTask.CompletedTask;
    }
}
