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
        new IndexRecoverySummary("Starting", null, null, null, null, 0));

    public async ValueTask ReloadAsync(CancellationToken cancellationToken) =>
        Current = await reader.ReadOverviewAsync(cancellationToken).ConfigureAwait(false);

    public ValueTask HandleStatusChangedAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statusChanged);
        return string.Equals(statusChanged.Projection, "pipeline", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(statusChanged.Projection, "index-recovery", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(statusChanged.Projection, "reconnect", StringComparison.OrdinalIgnoreCase)
            ? ReloadAsync(cancellationToken)
            : ValueTask.CompletedTask;
    }
}
