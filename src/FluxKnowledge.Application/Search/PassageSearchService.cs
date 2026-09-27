using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.Search;

/// <summary>The existing search/knowledge surface consumes the same complete ordered passages.</summary>
public sealed class PassageSearchService(IHybridPassageRetrieval engine) : ISearchService
{
    public async ValueTask<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var validated = SearchQueryValidator.Validate(request);
        var query = validated.Query.Normalize(NormalizationForm.FormC);
        if (query.Length > 2048) throw new SearchQueryValidationException("Search query must be at most 2048 characters.");
        var result = await engine.SearchAsync(new(query, validated.Limit, "all", null, null), cancellationToken).ConfigureAwait(false);
        var degraded = result.SemanticStatus != "ready" ? result.SemanticStatus :
            result.Warnings.Any(warning => warning is "rerank:unavailable" or "rerank:invalid-output") ? "unavailable" : null;
        if (result.Results.Count == 0 && degraded is not null) throw new PassageRetrievalRefusalException(degraded);
        return new(result.Results.Select((hit, i) => new SearchHit(new(hit.PipelineRecordId), hit.SourceIdentity,
            hit.PipelineRecordRevision, hit.Title, hit.Passage, 1D / (i + 1),
            hit.Explanation.Concat(result.Warnings).Distinct(StringComparer.Ordinal).ToArray())).ToArray(),
            result.CandidateCount, result.IndexGeneration?.ToString("N") ?? string.Empty, "local_first") { DegradedStatus = degraded };
    }
}
