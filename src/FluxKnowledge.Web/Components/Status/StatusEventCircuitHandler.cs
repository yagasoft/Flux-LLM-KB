using FluxKnowledge.Application.Contracts;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace FluxKnowledge.Web.Components.Status;

public sealed class StatusEventCircuitHandler(StatusEventFeed feed, TimeProvider timeProvider) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        return feed.PublishAsync(
                new StatusChanged(null, "reconnect", timeProvider.GetUtcNow()),
                cancellationToken)
            .AsTask();
    }
}
