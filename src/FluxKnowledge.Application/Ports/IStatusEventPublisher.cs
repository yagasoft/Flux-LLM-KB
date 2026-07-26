using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.Application.Ports;

public interface IStatusEventPublisher
{
    ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken);
}
