using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Search;

namespace FluxKnowledge.Application.Ports;

public interface ISearchHydrator
{
    ValueTask<IReadOnlyList<SearchHit>> HydrateAsync(
        IReadOnlyList<FusedCandidate> candidates,
        int limit,
        CancellationToken cancellationToken);
}
