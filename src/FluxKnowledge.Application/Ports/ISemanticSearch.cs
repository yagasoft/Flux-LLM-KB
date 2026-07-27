using FluxKnowledge.Application.Search;

namespace FluxKnowledge.Application.Ports;

public interface ISemanticSearch
{
    ValueTask<IReadOnlyList<RankedCandidate>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);
}
