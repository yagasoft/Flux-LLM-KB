using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;

namespace FluxKnowledge.Infrastructure.Usearch.Search;

public sealed class UsearchNearestNeighbourQuery(
    IEmbeddingProvider embeddingProvider,
    IAnnIndex annIndex) : ISemanticSearch
{
    public async ValueTask<IReadOnlyList<RankedCandidate>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var embedding = await embeddingProvider.CreateEmbeddingAsync(query, cancellationToken).ConfigureAwait(false);
        var matches = await annIndex.SearchAsync(embedding.Values, limit, cancellationToken).ConfigureAwait(false);

        return matches
            .OrderBy(static match => match.Distance)
            .ThenBy(static match => match.VectorId)
            .Select(static (match, index) => new RankedCandidate(match.VectorId, index + 1))
            .ToArray();
    }
}
