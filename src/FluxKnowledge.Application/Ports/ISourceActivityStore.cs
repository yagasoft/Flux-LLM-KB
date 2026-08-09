using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

public interface ISourceActivityStore
{
    ValueTask<SourceActivity> FindOrCreateAsync(
        SourceActivityDraft draft,
        CancellationToken cancellationToken);
}
