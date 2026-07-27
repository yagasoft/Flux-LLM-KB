using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Search;

public sealed class HybridSearchService(
    ILexicalSearch lexicalSearch,
    ISemanticSearch semanticSearch,
    ISearchHydrator hydrator,
    IIndexGenerationStore indexGenerationStore) : ISearchService
{
    public async ValueTask<SearchResponse> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        var validated = SearchQueryValidator.Validate(request);
        var lexicalTask = lexicalSearch.SearchAsync(validated.Query, validated.Limit, cancellationToken).AsTask();
        var semanticTask = semanticSearch.SearchAsync(validated.Query, validated.Limit, cancellationToken).AsTask();
        await Task.WhenAll(lexicalTask, semanticTask).ConfigureAwait(false);

        var candidates = ReciprocalRankFusion.Combine(lexicalTask.Result, semanticTask.Result);
        var hits = await hydrator.HydrateAsync(candidates, validated.Limit, cancellationToken).ConfigureAwait(false);
        var activeGeneration = await indexGenerationStore.GetActiveGenerationIdAsync(cancellationToken).ConfigureAwait(false);

        return new SearchResponse(
            hits,
            candidates.Count,
            activeGeneration?.ToString("N") ?? string.Empty,
            "local_first");
    }
}
