using FluxKnowledge.Domain.Jobs;

namespace FluxKnowledge.Application.Ports;

public interface IOutboxStore
{
    ValueTask EnqueueAsync(DispatchMessage message, CancellationToken cancellationToken);
}
