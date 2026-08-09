using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Web.Components.Sources;

public sealed class SourceRootDetailPageState(
    ISourceRootProjectionReader reader,
    IDeferredContentReprocessor? reprocessor)
{
    public Guid RootId { get; private set; }

    public SourceRootDetailProjection? Detail { get; private set; }

    public bool CanReprocessDeferredContent =>
        Detail?.CanReprocessDeferredContent == true && reprocessor is not null;

    public string ReprocessUnavailableMessage => Detail?.CanReprocessDeferredContent == true
        ? DeferredContentReplayResult.LocalOperationUnavailable.Message
        : DeferredContentReplayResult.Unavailable.Message;

    public async ValueTask LoadAsync(Guid rootId, CancellationToken cancellationToken)
    {
        RootId = rootId;
        Detail = await reader.ReadRootAsync(rootId, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask HandleStatusChangedAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statusChanged);
        return RootId != Guid.Empty &&
            (string.Equals(statusChanged.Projection, "sources", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(statusChanged.Projection, "reconnect", StringComparison.OrdinalIgnoreCase))
            ? LoadAsync(RootId, cancellationToken)
            : ValueTask.CompletedTask;
    }

    public ValueTask<DeferredContentReplayResult> ReprocessDeferredContentAsync(CancellationToken cancellationToken)
    {
        if (Detail is { CanReprocessDeferredContent: true } detail && reprocessor is not null)
        {
            return reprocessor.ReprocessAsync(detail.ReprocessableActivities, cancellationToken);
        }

        return ValueTask.FromResult(Detail?.CanReprocessDeferredContent == true
            ? DeferredContentReplayResult.LocalOperationUnavailable
            : DeferredContentReplayResult.Unavailable);
    }
}
